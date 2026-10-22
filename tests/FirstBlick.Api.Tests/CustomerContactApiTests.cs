using System.Net;
using System.Text.Json;
using FirstBlick.Api;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FirstBlick.Api.Tests.PostgresApiFixture;

namespace FirstBlick.Api.Tests;

public sealed class CustomerContactApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private static CustomerContactDetails Details(string name = "Objektleitung", bool primary = false) => new(name, "Hausverwaltung", "kontakt@example.test", "0421 123", "Am Vormittag erreichbar", primary);
    private static string Path(Customer customer, string suffix = "") => $"/api/v1/customers/{customer.Id}/contacts{suffix}";

    [PostgresApiFact]
    public async Task Contacts_persist_through_add_edit_archive_restore_with_immutable_identity_and_audit()
    {
        var (owner, client, customer) = await Setup(); var key = Guid.NewGuid().ToString("N");
        var first = await Ok<CustomerContactsView>(Send(client, HttpMethod.Post, Path(customer), Details(primary: true), customer.Revision, key));
        var contact = Assert.Single(first.Contacts); Assert.Equal(owner.UserId, contact.CreatedBy); Assert.Equal(owner.UserId, contact.UpdatedBy);
        Assert.Equal(customer.Id, first.CustomerId); Assert.Equal(owner.TenantId, first.TenantId); Assert.Equal(customer.Revision + 1, first.Revision);
        using (var replay = await Send(client, HttpMethod.Post, Path(customer), Details(primary: true), customer.Revision, key))
        { Assert.True(replay.IsSuccessStatusCode); Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single()); }
        var updated = await Ok<CustomerContactsView>(Send(client, HttpMethod.Post, Path(customer, $"/{contact.Id}/update"), Details("Buchhaltung", true), first.Revision));
        Assert.Equal("Buchhaltung", Assert.Single(updated.Contacts).Name); Assert.Equal(contact.CreatedAt, updated.Contacts[0].CreatedAt);
        var archived = await Ok<CustomerContactsView>(Send(client, HttpMethod.Post, Path(customer, $"/{contact.Id}/archive"), new { }, updated.Revision));
        Assert.True(archived.Contacts[0].Archived); Assert.False(archived.Contacts[0].IsPrimary); Assert.Equal(owner.UserId, archived.Contacts[0].ArchivedBy);
        await Error(Send(client, HttpMethod.Post, Path(customer, $"/{contact.Id}/update"), Details(), archived.Revision), "contact_archived");
        var restored = await Ok<CustomerContactsView>(Send(client, HttpMethod.Post, Path(customer, $"/{contact.Id}/restore"), new { }, archived.Revision));
        Assert.False(restored.Contacts[0].Archived); Assert.False(restored.Contacts[0].IsPrimary); Assert.Null(restored.Contacts[0].ArchivedBy);
        using var read = await client.GetAsync(Path(customer)); Assert.Equal($"\"{restored.Revision}\"", read.Headers.ETag?.Tag);
        var stored = await fixture.Read<Customer>(owner.TenantId, customer.Id); Assert.Equal(restored.Contacts, stored.Contacts);
        Assert.Equal("Bisheriger Kontakt", stored.ContactName); Assert.Equal("zentrale@example.test", stored.Email);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            Assert.Equal(4, await db.Audit.IgnoreQueryFilters().CountAsync(x => x.TenantId == owner.TenantId && x.EntityId == customer.Id && x.Action.StartsWith("customer.contact_")));
        });
    }

    [PostgresApiFact]
    public async Task Generic_customer_edits_preserve_omitted_or_null_contacts_and_reject_fabrication_or_deletion()
    {
        var (owner, client, customer) = await Setup();
        var book = await Ok<CustomerContactsView>(Send(client, HttpMethod.Post, Path(customer), Details(), customer.Revision));
        var edited = await Ok<Customer>(Send(client, HttpMethod.Put, $"/api/v1/customers/{customer.Id}", new { name = "Neue Firmenbezeichnung" }, book.Revision));
        Assert.Equal(book.Contacts, edited.Contacts);
        var explicitNull = await Ok<Customer>(Send(client, HttpMethod.Put, $"/api/v1/customers/{customer.Id}", new { name = "Noch ein Name", contacts = (object?)null }, edited.Revision));
        Assert.Equal(book.Contacts, explicitNull.Contacts);
        await Error(Send(client, HttpMethod.Put, $"/api/v1/customers/{customer.Id}", new { name = "Nicht löschen", contacts = Array.Empty<CustomerContact>() }, explicitNull.Revision), "contact_command_required");
        explicitNull.Contacts![0] = explicitNull.Contacts[0] with { Name = "Gefälscht" };
        await Error(Send(client, HttpMethod.Put, $"/api/v1/customers/{customer.Id}", explicitNull, explicitNull.Revision), "contact_command_required");
        await Error(Send(client, HttpMethod.Post, "/api/v1/customers", explicitNull), "command_required");
        var retained = await fixture.Read<Customer>(owner.TenantId, customer.Id); Assert.Equal(book.Contacts, retained.Contacts); Assert.Equal("Noch ein Name", retained.Name);
    }

    [PostgresApiFact]
    public async Task Historical_customers_read_as_empty_without_changing_stored_data_or_revision()
    {
        var (owner, client, customer) = await Setup();
        var before = await Stored(owner.TenantId, customer.Id); Assert.DoesNotContain("\"contacts\"", before, StringComparison.Ordinal);
        var book = await Ok<CustomerContactsView>(client.GetAsync(Path(customer))); Assert.Empty(book.Contacts); Assert.Equal(customer.Revision, book.Revision);
        Assert.Equal(before, await Stored(owner.TenantId, customer.Id));
        var legacyUpdate = await Ok<Customer>(Send(client, HttpMethod.Put, $"/api/v1/customers/{customer.Id}", new { customer.Name, contacts = Array.Empty<CustomerContact>() }, customer.Revision));
        Assert.Null(legacyUpdate.Contacts); Assert.DoesNotContain("\"contacts\"", await Stored(owner.TenantId, customer.Id), StringComparison.Ordinal);
    }

    [PostgresApiFact]
    public async Task Concurrent_customer_revisions_cannot_commit_two_primary_contacts()
    {
        var (owner, client, customer) = await Setup();
        var responses = await Task.WhenAll(new[] { "Eins", "Zwei" }.Select(name => Send(client, HttpMethod.Post, Path(customer), Details(name, true), customer.Revision)));
        Assert.Single(responses, x => x.IsSuccessStatusCode); Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        var current = await fixture.Read<Customer>(owner.TenantId, customer.Id); Assert.True(Assert.Single(current.Contacts!).IsPrimary);
        var second = await Ok<CustomerContactsView>(Send(client, HttpMethod.Post, Path(customer), Details("Drei", true), current.Revision));
        Assert.Equal("Drei", Assert.Single(second.Contacts, x => x.IsPrimary).Name); Assert.Equal(2, second.Contacts.Count);
    }

    [PostgresApiFact]
    public async Task Customer_contacts_are_tenant_bound_and_cannot_be_changed_through_another_customer()
    {
        var (owner, client, customer) = await Setup(); var (_, other, otherCustomer) = await Setup();
        var first = await Ok<CustomerContactsView>(Send(client, HttpMethod.Post, Path(customer), Details(), customer.Revision));
        var contact = Assert.Single(first.Contacts);
        await Error(other.GetAsync(Path(customer)), "not_found", HttpStatusCode.NotFound);
        await Error(Send(other, HttpMethod.Post, Path(otherCustomer, $"/{contact.Id}/archive"), new { }, otherCustomer.Revision), "not_found", HttpStatusCode.NotFound);
        await Error(Send(other, HttpMethod.Post, Path(customer), Details(), customer.Revision), "not_found", HttpStatusCode.NotFound);
        var stored = await fixture.Read<Customer>(owner.TenantId, customer.Id); Assert.False(Assert.Single(stored.Contacts!).Archived);
        await Ok<Customer>(Send(client, HttpMethod.Delete, $"/api/v1/customers/{customer.Id}", revision: first.Revision));
        await Error(client.GetAsync(Path(customer)), "not_found", HttpStatusCode.NotFound);
    }

    [PostgresApiFact]
    public async Task Current_rights_are_checked_before_replaying_a_prior_contact_command()
    {
        var (owner, client, customer) = await Setup(); var key = Guid.NewGuid().ToString("N");
        await Ok<CustomerContactsView>(Send(client, HttpMethod.Post, Path(customer), Details(), customer.Revision, key));
        var accounting = await fixture.CreateUser(owner.TenantId, BusinessRole.Accounting); var reader = await fixture.Login(accounting);
        await Ok<CustomerContactsView>(reader.GetAsync(Path(customer)));
        await Error(Send(reader, HttpMethod.Post, Path(customer), Details(), customer.Revision), "forbidden", HttpStatusCode.Forbidden);
        var worker = await fixture.CreateUser(owner.TenantId, BusinessRole.Worker); var workerClient = await fixture.Login(worker);
        await Error(workerClient.GetAsync(Path(customer)), "forbidden", HttpStatusCode.Forbidden);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>(); var membership = await db.Memberships.IgnoreQueryFilters().SingleAsync(x => x.UserId == owner.UserId && x.TenantId == owner.TenantId);
            membership.Role = BusinessRole.Accounting; await db.SaveChangesAsync();
        });
        await Error(Send(client, HttpMethod.Post, Path(customer), Details(), customer.Revision, key), "forbidden", HttpStatusCode.Forbidden);
    }

    [PostgresApiFact]
    public async Task Invalid_inputs_and_missing_revision_do_not_commit_contacts()
    {
        var (owner, client, customer) = await Setup();
        await Error(Send(client, HttpMethod.Post, Path(customer), Details()), "revision_required", HttpStatusCode.PreconditionRequired);
        await Error(Send(client, HttpMethod.Post, Path(customer), Details() with { Email = "invalid" }, customer.Revision), "invalid_email");
        await Error(Send(client, HttpMethod.Post, Path(customer), Details() with { Function = null! }, customer.Revision), "contact_fields");
        var stored = await fixture.Read<Customer>(owner.TenantId, customer.Id); Assert.Null(stored.Contacts); Assert.Equal(customer.Revision, stored.Revision);
    }

    private async Task<(ApiTestUser Owner, HttpClient Client, Customer Customer)> Setup()
    {
        var tenant = await fixture.CreateTenant(); var owner = await fixture.CreateUser(tenant.Id); var client = await fixture.Login(owner);
        var customer = await Ok<Customer>(Send(client, HttpMethod.Post, "/api/v1/customers", new { name = "Hausverwaltung", contactName = "Bisheriger Kontakt", email = "zentrale@example.test" }));
        return (owner, client, customer);
    }

    private Task<string> Stored(Guid tenant, Guid id) => fixture.InScope(async services => await services.GetRequiredService<FirstBlickDbContext>().Entities.IgnoreQueryFilters()
        .Where(x => x.TenantId == tenant && x.Kind == "customers" && x.Id == id).Select(x => x.Data).SingleAsync());

    private static async Task<T> Ok<T>(Task<HttpResponseMessage> pending)
    {
        using var response = await pending; var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {text}"); return AppJson.Read<T>(text);
    }

    private static async Task Error(Task<HttpResponseMessage> pending, string code, HttpStatusCode status = HttpStatusCode.BadRequest)
    {
        using var response = await pending; var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(status, response.StatusCode); Assert.Equal(code, JsonDocument.Parse(text).RootElement.GetProperty("code").GetString());
    }
}
