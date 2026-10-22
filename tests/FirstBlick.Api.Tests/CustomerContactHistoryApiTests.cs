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

public sealed class CustomerContactHistoryApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private static CustomerContactDetails Details(string name = "Objektleitung", bool primary = false) => new(name, "Verwaltung", "kontakt@example.test", "0421 123", "Am Vormittag", primary);
    private static string Path(Customer customer, string suffix = "") => $"/api/v1/customers/{customer.Id}/contacts{suffix}";

    [PostgresApiFact]
    public async Task Contact_history_retains_values_actor_and_primary_demotion_without_duplicate_replay_entries()
    {
        var (owner, client, customer) = await Setup(); var key = Guid.NewGuid().ToString("N");
        var first = await Ok<CustomerContactsView>(Send(client, HttpMethod.Post, Path(customer), Details(primary: true), customer.Revision, key));
        var contact = Assert.Single(first.Contacts);
        var original = await Ok<CustomerContactHistoryView>(client.GetAsync(Path(customer, $"/{contact.Id}/history")));
        var created = Assert.Single(original.Items); Assert.Null(created.Before); Assert.Equal(owner.UserId, created.RecordedBy);
        Assert.Equal("Synthetic API test person", created.RecordedByName); Assert.Equal(CustomerContactChangeKind.Created, created.ChangeKind);
        var originalBytes = await Stored(owner.TenantId, created.Id);
        using (var replay = await Send(client, HttpMethod.Post, Path(customer), Details(primary: true), customer.Revision, key))
        { Assert.True(replay.IsSuccessStatusCode); Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single()); }
        Assert.Single((await Ok<CustomerContactHistoryView>(client.GetAsync(Path(customer, $"/{contact.Id}/history")))).Items);
        var second = await Ok<CustomerContactsView>(Send(client, HttpMethod.Post, Path(customer), Details("Buchhaltung", true), first.Revision));
        var updated = await Ok<CustomerContactsView>(Send(client, HttpMethod.Post, Path(customer, $"/{contact.Id}/update"), Details("Objektleitung Neu") with { Phone = "0421 456" }, second.Revision));
        var archived = await Ok<CustomerContactsView>(Send(client, HttpMethod.Post, Path(customer, $"/{contact.Id}/archive"), new { }, updated.Revision));
        var restored = await Ok<CustomerContactsView>(Send(client, HttpMethod.Post, Path(customer, $"/{contact.Id}/restore"), new { }, archived.Revision));
        var history = await Ok<CustomerContactHistoryView>(client.GetAsync(Path(customer, $"/{contact.Id}/history")));
        Assert.Equal(restored.Revision, history.CustomerRevision); Assert.Equal(5, history.Total);
        Assert.Equal(new[] { CustomerContactChangeKind.Restored, CustomerContactChangeKind.Archived, CustomerContactChangeKind.Updated,
            CustomerContactChangeKind.PrimaryCleared, CustomerContactChangeKind.Created }, history.Items.Select(x => x.ChangeKind));
        Assert.Equal("0421 123", history.Items[2].Before!.Phone); Assert.Equal("0421 456", history.Items[2].After.Phone);
        Assert.Equal(originalBytes, await Stored(owner.TenantId, created.Id));
        var persisted = await fixture.Read<Customer>(owner.TenantId, customer.Id);
        CustomerContactHistory.EnsureCompleteHistory(persisted, history.Items);
        var archivedCustomer = await Ok<Customer>(Send(client, HttpMethod.Delete, $"/api/v1/customers/{customer.Id}", revision: restored.Revision));
        Assert.True(archivedCustomer.Archived);
        var retained = await Ok<CustomerContactHistoryView>(client.GetAsync(Path(customer, $"/{contact.Id}/history")));
        Assert.Equal(5, retained.Total); Assert.Equal(archivedCustomer.Revision, retained.CustomerRevision);
        Assert.Equal(AppJson.Serialize(history.Items), AppJson.Serialize(retained.Items));
        Assert.Equal(originalBytes, await Stored(owner.TenantId, created.Id));
        await fixture.InScope(async services => Assert.Equal(6, await services.GetRequiredService<FirstBlickDbContext>()
            .Links.IgnoreQueryFilters().CountAsync(x => x.TenantId == owner.TenantId && x.TargetKind == "customers"
                && x.TargetId == customer.Id && x.SourceKind == "customer-contact-changes")));
    }

    [PostgresApiFact]
    public async Task Customer_history_does_not_hide_other_references_that_prevent_customer_archival()
    {
        var (owner, client, customer) = await Setup();
        var book = await Ok<CustomerContactsView>(Send(client, HttpMethod.Post, Path(customer), Details(), customer.Revision));
        var contact = Assert.Single(book.Contacts);
        var history = await Ok<CustomerContactHistoryView>(client.GetAsync(Path(customer, $"/{contact.Id}/history")));
        var originalBytes = await Stored(owner.TenantId, Assert.Single(history.Items).Id);
        await Ok<Building>(Send(client, HttpMethod.Post, "/api/v1/buildings", new
        { customerId = customer.Id, name = "Weiter verwendetes Objekt", address = "Teststraße 1" }));
        await Error(Send(client, HttpMethod.Delete, $"/api/v1/customers/{customer.Id}", revision: book.Revision), "record_in_use", HttpStatusCode.BadRequest);
        var retainedCustomer = await fixture.Read<Customer>(owner.TenantId, customer.Id);
        Assert.False(retainedCustomer.Archived); Assert.Equal(book.Revision, retainedCustomer.Revision);
        var retained = await Ok<CustomerContactHistoryView>(client.GetAsync(Path(customer, $"/{contact.Id}/history")));
        Assert.Equal(AppJson.Serialize(history), AppJson.Serialize(retained));
        Assert.Equal(originalBytes, await Stored(owner.TenantId, history.Items[0].Id));
    }

    [PostgresApiFact]
    public async Task History_is_customer_and_tenant_bound_read_only_and_uses_current_roles()
    {
        var (owner, client, customer) = await Setup();
        var first = await Ok<CustomerContactsView>(Send(client, HttpMethod.Post, Path(customer), Details(), customer.Revision));
        var contact = Assert.Single(first.Contacts); var historyPath = Path(customer, $"/{contact.Id}/history");
        var (_, otherClient, otherCustomer) = await Setup();
        await Error(otherClient.GetAsync(historyPath), "not_found", HttpStatusCode.NotFound);
        await Error(client.GetAsync(Path(customer, $"/{otherCustomer.Id}/history")), "not_found", HttpStatusCode.NotFound);
        var reader = await fixture.Login(await fixture.CreateUser(owner.TenantId, BusinessRole.Accounting));
        var record = Assert.Single((await Ok<CustomerContactHistoryView>(reader.GetAsync(historyPath))).Items);
        var worker = await fixture.Login(await fixture.CreateUser(owner.TenantId, BusinessRole.Worker));
        await Error(worker.GetAsync(historyPath), "forbidden", HttpStatusCode.Forbidden);
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete })
        {
            using var denied = await Send(client, method, "/api/v1/customer-contact-changes" + (method == HttpMethod.Post ? "" : $"/{record.Id}"), record, 1);
            Assert.False(denied.IsSuccessStatusCode);
        }
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            var membership = await db.Memberships.IgnoreQueryFilters().SingleAsync(x => x.TenantId == owner.TenantId && x.UserId == owner.UserId);
            membership.Role = BusinessRole.Worker; await db.SaveChangesAsync();
        });
        await Error(client.GetAsync(historyPath), "forbidden", HttpStatusCode.Forbidden);
    }

    [PostgresApiFact]
    public async Task Legacy_contacts_have_no_invented_history_and_failed_commands_leave_no_partial_entries()
    {
        var tenant = await fixture.CreateTenant(); var owner = await fixture.CreateUser(tenant.Id); var client = await fixture.Login(owner);
        var now = DateTimeOffset.UtcNow.AddMinutes(-2);
        var customer = new Customer { TenantId = tenant.Id, Name = "Bestandskunde", CreatedAt = now, UpdatedAt = now };
        var contact = CustomerContactWorkflow.Add(customer, Details(), owner.UserId, now);
        await fixture.Save(customer, owner.UserId);
        var initial = await Ok<CustomerContactHistoryView>(client.GetAsync(Path(customer, $"/{contact.Id}/history")));
        Assert.Empty(initial.Items); Assert.Equal(0, initial.Total); Assert.Equal(contact, initial.Current);
        await Error(Send(client, HttpMethod.Post, Path(customer, $"/{contact.Id}/update"), Details() with { Email = "invalid" }, customer.Revision), "invalid_email", HttpStatusCode.BadRequest);
        await Error(Send(client, HttpMethod.Post, Path(customer, $"/{contact.Id}/update"), Details(), customer.Revision + 1), "revision_conflict", HttpStatusCode.Conflict);
        Assert.Empty((await Ok<CustomerContactHistoryView>(client.GetAsync(Path(customer, $"/{contact.Id}/history")))).Items);
        await Ok<CustomerContactsView>(Send(client, HttpMethod.Post, Path(customer, $"/{contact.Id}/update"), Details("Neuer Name"), customer.Revision));
        var first = Assert.Single((await Ok<CustomerContactHistoryView>(client.GetAsync(Path(customer, $"/{contact.Id}/history")))).Items);
        Assert.Equal(contact, first.Before); Assert.Equal(CustomerContactChangeKind.Updated, first.ChangeKind);
    }

    [PostgresApiFact]
    public async Task History_paginates_by_customer_revision_and_rejects_tampered_original_values()
    {
        var (owner, client, customer) = await Setup();
        var book = await Ok<CustomerContactsView>(Send(client, HttpMethod.Post, Path(customer), Details(), customer.Revision));
        var contact = Assert.Single(book.Contacts);
        for (var i = 0; i < 21; i++) book = await Ok<CustomerContactsView>(Send(client, HttpMethod.Post, Path(customer, $"/{contact.Id}/update"), Details($"Kontakt {i}"), book.Revision));
        var first = await Ok<CustomerContactHistoryView>(client.GetAsync(Path(customer, $"/{contact.Id}/history")));
        var second = await Ok<CustomerContactHistoryView>(client.GetAsync(Path(customer, $"/{contact.Id}/history?page=2")));
        Assert.Equal(22, first.Total); Assert.Equal(20, first.Items.Count); Assert.Equal(2, second.Items.Count);
        Assert.Empty(first.Items.Select(x => x.Id).Intersect(second.Items.Select(x => x.Id)));
        Assert.Equal(first.Items[^1].Before, second.Items[0].After);
        await Error(client.GetAsync(Path(customer, $"/{contact.Id}/history?page=0")), "contact_history_page", HttpStatusCode.BadRequest);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            var stored = await db.Entities.IgnoreQueryFilters().SingleAsync(x => x.TenantId == owner.TenantId && x.Id == first.Items[0].Id && x.Kind == "customer-contact-changes");
            var corrupted = AppJson.Read<CustomerContactChange>(stored.Data); corrupted.Before = corrupted.Before! with { Phone = "tampered" };
            stored.Data = AppJson.Serialize(corrupted); await db.SaveChangesAsync();
        });
        await Error(client.GetAsync(Path(customer, $"/{contact.Id}/history")), "contact_history_digest", HttpStatusCode.BadRequest);
    }

    private async Task<(ApiTestUser, HttpClient, Customer)> Setup()
    {
        var tenant = await fixture.CreateTenant(); var owner = await fixture.CreateUser(tenant.Id); var client = await fixture.Login(owner);
        var customer = await Ok<Customer>(Send(client, HttpMethod.Post, "/api/v1/customers", new { name = "Hausverwaltung" }));
        return (owner, client, customer);
    }
    private Task<string> Stored(Guid tenantId, Guid id) => fixture.InScope(async services => await services.GetRequiredService<FirstBlickDbContext>()
        .Entities.IgnoreQueryFilters().Where(x => x.TenantId == tenantId && x.Kind == "customer-contact-changes" && x.Id == id).Select(x => x.Data).SingleAsync());
    private static async Task<T> Ok<T>(Task<HttpResponseMessage> pending)
    {
        using var response = await pending; var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {text}"); return AppJson.Read<T>(text);
    }
    private static async Task Error(Task<HttpResponseMessage> pending, string code, HttpStatusCode status)
    {
        using var response = await pending; var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(status, response.StatusCode); Assert.Equal(code, JsonDocument.Parse(text).RootElement.GetProperty("code").GetString());
    }
}
