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

public sealed class CustomerCommunicationApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    [PostgresApiFact]
    public async Task Journal_preserves_original_and_snapshots_through_later_master_data_edits_and_a_separate_void()
    {
        var s = await Setup(); var (customer, contact, project) = await References(s);
        var body = Request() with { ContactId = contact.Id, ProjectId = project.Id, Text = "Erste Zeile\nZweite Zeile" };
        var key = Guid.NewGuid().ToString("N");
        var created = await Ok<CustomerCommunicationChange>(Send(s.Client, HttpMethod.Post, Path(customer), body, customer.Revision, key));
        var original = created.Entry; original.Validate();
        Assert.Equal(customer.Revision + 1, created.CustomerRevision); Assert.Equal(s.Owner.UserId, original.RecordedBy);
        Assert.Equal(customer.Name, original.CustomerName); Assert.Equal(contact.Name, original.Contact!.Name); Assert.Equal(project.Name, original.Project!.Name);
        Assert.Equal(body.Text, original.Text); Assert.True(original.OccurredAt < customer.CreatedAt);
        var storedOriginal = await Stored(s.Owner.TenantId, original.Id);
        using (var replay = await Send(s.Client, HttpMethod.Post, Path(customer), body, customer.Revision, key))
        {
            Assert.True(replay.IsSuccessStatusCode, await replay.Content.ReadAsStringAsync());
            Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
            Assert.Equal(original.Id, AppJson.Read<CustomerCommunicationChange>(await replay.Content.ReadAsStringAsync()).Entry.Id);
        }
        var edited = await Ok<Customer>(Send(s.Client, HttpMethod.Put, $"/api/v1/customers/{customer.Id}", new { name = "Umbenannte Verwaltung" }, created.CustomerRevision));
        var changedContact = await Ok<CustomerContactsView>(Send(s.Client, HttpMethod.Post, $"/api/v1/customers/{customer.Id}/contacts/{contact.Id}/update",
            new CustomerContactDetails("Neue Objektleitung", "", "neu@example.test", "", "", false), edited.Revision));
        var archivedContact = await Ok<CustomerContactsView>(Send(s.Client, HttpMethod.Post, $"/api/v1/customers/{customer.Id}/contacts/{contact.Id}/archive", new { }, changedContact.Revision));
        project.Name = "Umbenanntes Projekt";
        await Ok<Project>(Send(s.Client, HttpMethod.Put, $"/api/v1/projects/{project.Id}", project, project.Revision));
        var correction = new CustomerCommunicationVoidRequest(original.Digest, "Falsches Gespräch zugeordnet"); var voidKey = Guid.NewGuid().ToString("N");
        var voided = await Ok<CustomerCommunicationChange>(Send(s.Client, HttpMethod.Post, VoidPath(customer, original.Id), correction, archivedContact.Revision, voidKey));
        Assert.Equal(archivedContact.Revision + 1, voided.CustomerRevision); Assert.NotNull(voided.Voiding);
        CustomerCommunicationWorkflow.EnsureVoidSource(original, voided.Voiding);
        Assert.Equal(s.Owner.UserId, voided.Voiding.VoidedBy); Assert.Equal(correction.Reason, voided.Voiding.Reason);
        using (var replay = await Send(s.Client, HttpMethod.Post, VoidPath(customer, original.Id), correction, archivedContact.Revision, voidKey))
        { Assert.True(replay.IsSuccessStatusCode); Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single()); }
        var history = await Ok<CustomerCommunicationsView>(s.Client.GetAsync(Path(customer))); var item = Assert.Single(history.Items);
        Assert.Equal(1, history.Total); Assert.Equal("Umbenannte Verwaltung", history.CustomerName);
        Assert.Equal(customer.Name, item.Entry.CustomerName); Assert.Equal(contact.Name, item.Entry.Contact!.Name); Assert.Equal("Dachsanierung", item.Entry.Project!.Name);
        Assert.Equal(voided.Voiding.Id, item.Voiding!.Id); Assert.Equal(voided.CustomerRevision, history.CustomerRevision);
        Assert.Equal(storedOriginal, await Stored(s.Owner.TenantId, original.Id));
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            Assert.Equal(1, await db.Entities.IgnoreQueryFilters().CountAsync(x => x.TenantId == s.Owner.TenantId && x.Kind == "customer-communication-voids"));
            Assert.Equal(1, await db.Audit.IgnoreQueryFilters().CountAsync(x => x.TenantId == s.Owner.TenantId && x.EntityId == original.Id));
        });
    }

    [PostgresApiFact]
    public async Task Legacy_null_project_number_is_normalized_only_in_the_new_snapshot()
    {
        var s = await Setup(); var project = await ProjectFor(s.Client, s.Customer); project.Number = null!;
        project = await Ok<Project>(Send(s.Client, HttpMethod.Put, $"/api/v1/projects/{project.Id}", project, project.Revision));
        var retained = await Stored(s.Owner.TenantId, project.Id);
        var created = await Ok<CustomerCommunicationChange>(Send(s.Client, HttpMethod.Post, Path(s.Customer),
            Request() with { ProjectId = project.Id }, s.Customer.Revision));
        Assert.Equal("", created.Entry.Project!.Number); created.Entry.Validate();
        Assert.Equal(retained, await Stored(s.Owner.TenantId, project.Id));
        Assert.Null((await fixture.Read<Project>(s.Owner.TenantId, project.Id)).Number);
    }

    [PostgresApiFact]
    public async Task Missing_stale_and_concurrent_revisions_cannot_write_two_entries()
    {
        var s = await Setup();
        await Error(Send(s.Client, HttpMethod.Post, Path(s.Customer), Request()), "revision_required", HttpStatusCode.PreconditionRequired);
        var responses = await Task.WhenAll(new[] { "Gespräch A", "Gespräch B" }.Select(subject =>
            Send(s.Client, HttpMethod.Post, Path(s.Customer), Request() with { Subject = subject }, s.Customer.Revision)));
        Assert.Single(responses, x => x.IsSuccessStatusCode); Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        foreach (var response in responses) response.Dispose();
        await Error(Send(s.Client, HttpMethod.Post, Path(s.Customer), Request(), s.Customer.Revision), "revision_conflict", HttpStatusCode.Conflict);
        var history = await Ok<CustomerCommunicationsView>(s.Client.GetAsync(Path(s.Customer)));
        Assert.Single(history.Items); Assert.Equal(s.Customer.Revision + 1, history.CustomerRevision);
    }

    [PostgresApiFact]
    public async Task Wrong_digest_duplicate_correction_and_changed_idempotent_body_are_rejected_without_mutating_original()
    {
        var s = await Setup(); var key = Guid.NewGuid().ToString("N"); var body = Request();
        var created = await Ok<CustomerCommunicationChange>(Send(s.Client, HttpMethod.Post, Path(s.Customer), body, s.Customer.Revision, key));
        var retained = await Stored(s.Owner.TenantId, created.Entry.Id);
        await Error(Send(s.Client, HttpMethod.Post, Path(s.Customer), body with { Text = "Geänderter Wiederholungsversuch" }, s.Customer.Revision, key), "idempotency_conflict", HttpStatusCode.Conflict);
        await Error(Send(s.Client, HttpMethod.Post, VoidPath(s.Customer, created.Entry.Id), new CustomerCommunicationVoidRequest(new string('0', 64), "Korrektur"), created.CustomerRevision), "communication_digest");
        var correction = new CustomerCommunicationVoidRequest(created.Entry.Digest, "Doppelt erfasst");
        var voided = await Ok<CustomerCommunicationChange>(Send(s.Client, HttpMethod.Post, VoidPath(s.Customer, created.Entry.Id), correction, created.CustomerRevision));
        await Error(Send(s.Client, HttpMethod.Post, VoidPath(s.Customer, created.Entry.Id), correction, voided.CustomerRevision), "communication_already_void");
        Assert.Equal(retained, await Stored(s.Owner.TenantId, created.Entry.Id));
        Assert.Equal(voided.CustomerRevision, (await fixture.Read<Customer>(s.Owner.TenantId, s.Customer.Id)).Revision);
    }

    [PostgresApiFact]
    public async Task Invalid_input_contact_or_project_does_not_advance_customer_or_create_a_journal_record()
    {
        var s = await Setup(); var (customer, contact, _) = await References(s);
        var other = await Ok<Customer>(Send(s.Client, HttpMethod.Post, "/api/v1/customers", new { name = "Andere Verwaltung" }));
        var otherProject = await ProjectFor(s.Client, other);
        await Error(Send(s.Client, HttpMethod.Post, Path(customer), Request() with { OccurredAt = WholeSecond(DateTimeOffset.UtcNow.AddDays(1)) }, customer.Revision), "communication_time");
        await Error(Send(s.Client, HttpMethod.Post, Path(customer), Request() with { ContactId = Guid.NewGuid() }, customer.Revision), "communication_contact");
        await Error(Send(s.Client, HttpMethod.Post, Path(customer), Request() with { ProjectId = otherProject.Id }, customer.Revision), "communication_project");
        await Error(Send(s.Client, HttpMethod.Post, Path(customer), Request() with { Channel = CustomerCommunicationChannel.Note, Direction = CustomerCommunicationDirection.Outgoing }, customer.Revision), "communication_direction");
        using (var forged = await Send(s.Client, HttpMethod.Post, Path(customer), new { channel = "Note", direction = "Internal", occurredAt = Request().OccurredAt, subject = "Notiz", text = "Text", recordedBy = Guid.NewGuid() }, customer.Revision))
            Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Equal(customer.Revision, (await fixture.Read<Customer>(s.Owner.TenantId, customer.Id)).Revision);
        var archived = await Ok<CustomerContactsView>(Send(s.Client, HttpMethod.Post, $"/api/v1/customers/{customer.Id}/contacts/{contact.Id}/archive", new { }, customer.Revision));
        await Error(Send(s.Client, HttpMethod.Post, Path(customer), Request() with { ContactId = contact.Id }, archived.Revision), "communication_contact");
        var history = await Ok<CustomerCommunicationsView>(s.Client.GetAsync(Path(customer))); Assert.Empty(history.Items); Assert.Equal(0, history.Total);
        Assert.Equal(archived.Revision, history.CustomerRevision);
    }

    [PostgresApiFact]
    public async Task Both_tenant_and_customer_boundaries_apply_to_sources_and_voids()
    {
        var s = await Setup(); var foreign = await Setup();
        var created = await Ok<CustomerCommunicationChange>(Send(s.Client, HttpMethod.Post, Path(s.Customer), Request(), s.Customer.Revision));
        var other = await Ok<Customer>(Send(s.Client, HttpMethod.Post, "/api/v1/customers", new { name = "Zweiter Kunde" }));
        var correction = new CustomerCommunicationVoidRequest(created.Entry.Digest, "Falsche Zuordnung");
        await Error(foreign.Client.GetAsync(Path(s.Customer)), "not_found", HttpStatusCode.NotFound);
        await Error(Send(foreign.Client, HttpMethod.Post, Path(s.Customer), Request(), created.CustomerRevision), "not_found", HttpStatusCode.NotFound);
        await Error(Send(foreign.Client, HttpMethod.Post, VoidPath(foreign.Customer, created.Entry.Id), correction, foreign.Customer.Revision), "not_found", HttpStatusCode.NotFound);
        await Error(Send(s.Client, HttpMethod.Post, VoidPath(other, created.Entry.Id), correction, other.Revision), "not_found", HttpStatusCode.NotFound);
        var foreignProject = await ProjectFor(foreign.Client, foreign.Customer);
        await Error(Send(s.Client, HttpMethod.Post, Path(s.Customer), Request() with { ProjectId = foreignProject.Id }, created.CustomerRevision), "not_found", HttpStatusCode.NotFound);
        Assert.Null(Assert.Single((await Ok<CustomerCommunicationsView>(s.Client.GetAsync(Path(s.Customer)))).Items).Voiding);
    }

    [PostgresApiFact]
    public async Task Current_roles_are_required_before_replaying_and_readers_cannot_record_or_void()
    {
        var s = await Setup(); var key = Guid.NewGuid().ToString("N"); var body = Request();
        var created = await Ok<CustomerCommunicationChange>(Send(s.Client, HttpMethod.Post, Path(s.Customer), body, s.Customer.Revision, key));
        var reader = await fixture.Login(await fixture.CreateUser(s.Owner.TenantId, BusinessRole.Accounting));
        await Ok<CustomerCommunicationsView>(reader.GetAsync(Path(s.Customer)));
        await Error(Send(reader, HttpMethod.Post, Path(s.Customer), body, created.CustomerRevision), "forbidden", HttpStatusCode.Forbidden);
        await Error(Send(reader, HttpMethod.Post, VoidPath(s.Customer, created.Entry.Id), new CustomerCommunicationVoidRequest(created.Entry.Digest, "Korrektur"), created.CustomerRevision), "forbidden", HttpStatusCode.Forbidden);
        var worker = await fixture.Login(await fixture.CreateUser(s.Owner.TenantId, BusinessRole.Worker));
        await Error(worker.GetAsync(Path(s.Customer)), "forbidden", HttpStatusCode.Forbidden);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            var membership = await db.Memberships.IgnoreQueryFilters().SingleAsync(x => x.TenantId == s.Owner.TenantId && x.UserId == s.Owner.UserId);
            membership.Role = BusinessRole.Accounting; await db.SaveChangesAsync();
        });
        await Error(Send(s.Client, HttpMethod.Post, Path(s.Customer), body, s.Customer.Revision, key), "forbidden", HttpStatusCode.Forbidden);
        Assert.Equal(created.CustomerRevision, (await fixture.Read<Customer>(s.Owner.TenantId, s.Customer.Id)).Revision);
    }

    [PostgresApiFact]
    public async Task Journal_is_available_with_core_module_and_blocks_generic_mutation_and_source_reparenting()
    {
        var s = await Setup(["core"]); var (customer, contact, project) = await References(s);
        var created = await Ok<CustomerCommunicationChange>(Send(s.Client, HttpMethod.Post, Path(customer), Request() with { ContactId = contact.Id, ProjectId = project.Id }, customer.Revision));
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete })
        {
            var path = "/api/v1/customer-communications" + (method == HttpMethod.Post ? "" : "/" + created.Entry.Id);
            using var response = await Send(s.Client, method, path, method == HttpMethod.Delete ? null : created.Entry, 1);
            Assert.False(response.IsSuccessStatusCode);
        }
        await Error(Send(s.Client, HttpMethod.Delete, $"/api/v1/customers/{customer.Id}", revision: created.CustomerRevision), "record_in_use");
        await Error(Send(s.Client, HttpMethod.Delete, $"/api/v1/projects/{project.Id}", revision: project.Revision), "record_in_use");
        var other = await Ok<Customer>(Send(s.Client, HttpMethod.Post, "/api/v1/customers", new { name = "Neuer Auftraggeber" }));
        var replacement = await ProjectFor(s.Client, other);
        project.CustomerId = other.Id; project.BuildingId = replacement.BuildingId;
        await Error(Send(s.Client, HttpMethod.Put, $"/api/v1/projects/{project.Id}", project, project.Revision), "communication_project");
        Assert.Equal(customer.Id, (await fixture.Read<Project>(s.Owner.TenantId, project.Id)).CustomerId);
        await Ok<CustomerCommunicationsView>(s.Client.GetAsync(Path(customer)));
    }

    [PostgresApiFact]
    public async Task Restored_archived_customer_history_remains_readable_but_blocks_new_commands_and_replay()
    {
        var s = await Setup(); var key = Guid.NewGuid().ToString("N"); var body = Request();
        var created = await Ok<CustomerCommunicationChange>(Send(s.Client, HttpMethod.Post, Path(s.Customer), body, s.Customer.Revision, key));
        var original = await Stored(s.Owner.TenantId, created.Entry.Id);
        // Canonical migration may retain an already archived master. The public
        // archive command correctly refuses to manufacture this state once linked.
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            var row = await db.Entities.IgnoreQueryFilters().SingleAsync(x => x.TenantId == s.Owner.TenantId && x.Kind == "customers" && x.Id == s.Customer.Id);
            var customer = AppJson.Read<Customer>(row.Data); customer.Archived = true; customer.Validate();
            row.Data = AppJson.Serialize(customer); row.Archived = true; await db.SaveChangesAsync();
        });
        var retainedCustomer = await Stored(s.Owner.TenantId, s.Customer.Id);
        var history = await Ok<CustomerCommunicationsView>(s.Client.GetAsync(Path(s.Customer)));
        Assert.True(history.CustomerArchived); Assert.Equal(created.Entry.Id, Assert.Single(history.Items).Entry.Id);
        await Error(Send(s.Client, HttpMethod.Post, Path(s.Customer), body, created.CustomerRevision), "not_found", HttpStatusCode.NotFound);
        await Error(Send(s.Client, HttpMethod.Post, Path(s.Customer), body, s.Customer.Revision, key), "not_found", HttpStatusCode.NotFound);
        await Error(Send(s.Client, HttpMethod.Post, VoidPath(s.Customer, created.Entry.Id), new CustomerCommunicationVoidRequest(created.Entry.Digest, "Korrektur"), created.CustomerRevision), "not_found", HttpStatusCode.NotFound);
        Assert.Equal(original, await Stored(s.Owner.TenantId, created.Entry.Id));
        Assert.Equal(retainedCustomer, await Stored(s.Owner.TenantId, s.Customer.Id));
    }

    [PostgresApiFact]
    public async Task History_is_read_only_deterministic_bounded_and_exposes_current_etag_and_browser_time_zone()
    {
        var s = await Setup();
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            var tenant = await db.Tenants.SingleAsync(x => x.Id == s.Owner.TenantId); tenant.TimeZone = "W. Europe Standard Time"; await db.SaveChangesAsync();
        });
        var entries = new List<CustomerCommunication>(); var currentRevision = s.Customer.Revision;
        for (var i = 0; i < 23; i++)
        {
            var change = await Ok<CustomerCommunicationChange>(Send(s.Client, HttpMethod.Post, Path(s.Customer),
                Request() with { Subject = $"Gespräch {i:00}", OccurredAt = Request().OccurredAt.AddMinutes(i) }, currentRevision));
            entries.Add(change.Entry); currentRevision = change.CustomerRevision;
        }
        var customerBytes = await Stored(s.Owner.TenantId, s.Customer.Id);
        using var response = await s.Client.GetAsync(Path(s.Customer));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal($"\"{currentRevision}\"", response.Headers.ETag?.Tag);
        var first = AppJson.Read<CustomerCommunicationsView>(await response.Content.ReadAsStringAsync());
        var second = await Ok<CustomerCommunicationsView>(s.Client.GetAsync(Path(s.Customer) + "?page=2"));
        var empty = await Ok<CustomerCommunicationsView>(s.Client.GetAsync(Path(s.Customer) + "?page=100000"));
        Assert.Equal("Europe/Berlin", first.TimeZone); Assert.Equal(23, first.Total); Assert.Equal(20, first.PageSize); Assert.Equal(20, first.Items.Count);
        Assert.Equal(3, second.Items.Count); Assert.Empty(empty.Items); Assert.Equal(23, empty.Total);
        Assert.Equal(entries.AsEnumerable().Reverse().Select(x => x.Id), first.Items.Concat(second.Items).Select(x => x.Entry.Id));
        Assert.Equal(currentRevision, second.CustomerRevision); Assert.Equal(customerBytes, await Stored(s.Owner.TenantId, s.Customer.Id));
    }

    private sealed record SetupData(ApiTestUser Owner, HttpClient Client, Customer Customer);
    private async Task<SetupData> Setup(string[]? modules = null)
    {
        var tenant = await fixture.CreateTenant(modules: modules); var owner = await fixture.CreateUser(tenant.Id); var client = await fixture.Login(owner);
        var customer = await Ok<Customer>(Send(client, HttpMethod.Post, "/api/v1/customers", new { name = "Synthetische Hausverwaltung" }));
        return new(owner, client, customer);
    }
    private async Task<(Customer Customer, CustomerContact Contact, Project Project)> References(SetupData s)
    {
        var book = await Ok<CustomerContactsView>(Send(s.Client, HttpMethod.Post, $"/api/v1/customers/{s.Customer.Id}/contacts",
            new CustomerContactDetails("Objektleitung", "Verwaltung", "kontakt@example.test", "0123 456", "", false), s.Customer.Revision));
        return (await fixture.Read<Customer>(s.Owner.TenantId, s.Customer.Id), Assert.Single(book.Contacts), await ProjectFor(s.Client, s.Customer));
    }
    private static async Task<Project> ProjectFor(HttpClient client, Customer customer)
    {
        var building = await Ok<Building>(Send(client, HttpMethod.Post, "/api/v1/buildings", new { customerId = customer.Id, name = "Testobjekt", address = "Synthetische Straße 1" }));
        return await Ok<Project>(Send(client, HttpMethod.Post, "/api/v1/projects", new { customerId = customer.Id, buildingId = building.Id, name = "Dachsanierung", number = "SYN-2026" }));
    }
    private static CustomerCommunicationRequest Request() => new(CustomerCommunicationChannel.Telephone, CustomerCommunicationDirection.Incoming,
        WholeSecond(DateTimeOffset.UtcNow.AddDays(-1)), "Terminabstimmung", "Besichtigung am Folgetag vereinbart.");
    private static DateTimeOffset WholeSecond(DateTimeOffset value) => DateTimeOffset.FromUnixTimeSeconds(value.ToUnixTimeSeconds());
    private static string Path(Customer customer) => $"/api/v1/customers/{customer.Id}/communications";
    private static string VoidPath(Customer customer, Guid entryId) => Path(customer) + $"/{entryId}/void";
    private Task<string> Stored(Guid tenant, Guid id) => fixture.InScope(async services => await services.GetRequiredService<FirstBlickDbContext>().Entities.IgnoreQueryFilters()
        .Where(x => x.TenantId == tenant && x.Id == id).Select(x => x.Data).SingleAsync());
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
