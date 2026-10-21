using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FirstBlick.Api.Tests;

public sealed class DiscoveryApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private static readonly DateTimeOffset WorkStart = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);
    private async Task<(ApiTestUser User, HttpClient Client)> Session(BusinessRole role = BusinessRole.ManagingDirector)
    {
        var tenant = await fixture.CreateTenant(); var user = await fixture.CreateUser(tenant.Id, role); return (user, await fixture.Login(user));
    }
    private async Task<(Customer Customer, Building Building, Project Project)> Project(ApiTestUser user, string name = "Synthetic discovery project")
    {
        var customer = await fixture.Save(new Customer { TenantId = user.TenantId, Name = "Synthetic customer", Address = "Synthetic street 1" }, user.UserId);
        var building = await fixture.Save(new Building { TenantId = user.TenantId, CustomerId = customer.Id, Name = "Synthetic building", Address = "Synthetic street 1" }, user.UserId);
        var project = await fixture.Save(new Project { TenantId = user.TenantId, CustomerId = customer.Id, BuildingId = building.Id, Name = name }, user.UserId);
        return (customer, building, project);
    }
    private async Task Update<T>(ApiTestUser user, Guid id, Action<T> change, string action = "update") where T : TenantEntity
        => await fixture.InScope(async services =>
        {
            var scope = services.GetRequiredService<TenantScope>(); scope.TenantId = user.TenantId; scope.UserId = user.UserId; scope.Role = BusinessRole.ManagingDirector;
            var store = services.GetRequiredService<EntityStore>(); var entity = await store.Get<T>(id); change(entity);
            await store.Save(entity, action, false); await services.GetRequiredService<FirstBlickDbContext>().SaveChangesAsync();
        });
    private static async Task<JsonElement> Ok(Task<HttpResponseMessage> pending)
    {
        using var response = await pending; var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {text}");
        Assert.True(response.Headers.CacheControl?.NoStore); using var document = JsonDocument.Parse(text); return document.RootElement.Clone();
    }
    private static async Task Error(Task<HttpResponseMessage> pending, HttpStatusCode status, string? code = null)
    {
        using var response = await pending; var text = await response.Content.ReadAsStringAsync(); Assert.Equal(status, response.StatusCode);
        if (code is not null) { using var json = JsonDocument.Parse(text); Assert.Equal(code, json.RootElement.GetProperty("code").GetString()); }
    }
    private static Guid[] Ids(JsonElement response, string field = "id") => response.GetProperty("items").EnumerateArray().Select(x => x.GetProperty(field).GetGuid()).ToArray();

    [PostgresApiFact]
    public async Task Search_whitelists_fields_and_tenant_before_stable_pagination_and_treats_query_as_literal()
    {
        var (user, client) = await Session();
        var customers = new List<Customer>();
        for (var i = 0; i < 3; i++) customers.Add(await fixture.Save(new Customer { TenantId = user.TenantId, Name = "Find-me " + i, Notes = "private-secret-marker" }, user.UserId));
        await fixture.Save(new Employee { TenantId = user.TenantId, Name = "private-secret-marker" }, user.UserId);
        await fixture.Save(new Customer { TenantId = user.TenantId, Name = "Coverage 50% exact" }, user.UserId);
        var (foreign, _) = await Session();
        var hidden = await fixture.Save(new Customer { TenantId = foreign.TenantId, Name = "Find-me foreign" }, foreign.UserId);
        var first = await Ok(client.GetAsync("/api/v1/search?q=Find-me&page=1&pageSize=2"));
        var next = await Ok(client.GetAsync("/api/v1/search?q=Find-me&page=2&pageSize=2"));
        Assert.Equal(3, first.GetProperty("total").GetInt64()); Assert.Equal(3, next.GetProperty("total").GetInt64());
        Assert.Equal(2, Ids(first).Length); Assert.Single(Ids(next)); Assert.Empty(Ids(first).Intersect(Ids(next)));
        Assert.Equal(customers.Select(x => x.Id).Order(), Ids(first).Concat(Ids(next)).Order()); Assert.DoesNotContain(hidden.Id, Ids(first));
        Assert.Equal(Ids(first), Ids(await Ok(client.GetAsync("/api/v1/search?q=Find-me&page=1&pageSize=2"))));
        Assert.Equal(0, (await Ok(client.GetAsync("/api/v1/search?q=private-secret-marker"))).GetProperty("total").GetInt64());
        Assert.Equal(1, (await Ok(client.GetAsync("/api/v1/search?q=50%25"))).GetProperty("total").GetInt64());
        Assert.Equal(0, (await Ok(client.GetAsync("/api/v1/search?q=" + Uri.EscapeDataString("' OR 1=1 --")))).GetProperty("total").GetInt64());
        Assert.DoesNotContain("notes", first.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [PostgresApiFact]
    public async Task Discovery_rejects_invalid_duplicate_unknown_and_unbounded_query_parameters()
    {
        var (_, client) = await Session();
        foreach (var query in new[] { "q=x", "q=ok&page=0", "q=ok&page=10001", "q=ok&pageSize=51", "q=ok&pageSize=-1", "q=ok&page=1.2", "q=ok&page=1&page=2", "q=ok&q=no", "q=ok&offset=5", "q=ok&kind=employees", "q=ok&kind=bank-transactions" })
            await Error(client.GetAsync("/api/v1/search?" + query), HttpStatusCode.BadRequest);
        await Error(client.GetAsync("/api/v1/search?q=" + new string('x', 201)), HttpStatusCode.BadRequest, "search_query");
        await Error(client.GetAsync("/api/v1/self/data-copy?userId=" + Guid.NewGuid()), HttpStatusCode.BadRequest, "unsupported_query");
        await Error(client.GetAsync("/api/v1/self/data-copy?tenantId=" + Guid.NewGuid()), HttpStatusCode.BadRequest, "unsupported_query");
    }

    [PostgresApiFact]
    public async Task Worker_search_uses_current_assignments_and_personal_profile_and_counts_only_visible_results()
    {
        var (user, client) = await Session(BusinessRole.Worker); var (_, _, visible) = await Project(user, "Marker assigned");
        var (_, _, hidden) = await Project(user, "Marker unassigned");
        var employee = await fixture.Save(new Employee { TenantId = user.TenantId, UserId = user.UserId, Name = "Synthetic worker" }, user.UserId);
        var assignment = await fixture.Save(new ScheduleAssignment { TenantId = user.TenantId, ProjectId = visible.Id, EmployeeId = employee.Id, Title = "Work", Start = WorkStart, End = WorkStart.AddHours(1) }, user.UserId);
        var task = await fixture.Save(new TaskItem { TenantId = user.TenantId, ProjectId = visible.Id, Title = "Marker task" }, user.UserId);
        var personalTask = await fixture.Save(new TaskItem { TenantId = user.TenantId, ProjectId = hidden.Id, AssignedEmployeeId = employee.Id, Title = "Marker own task" }, user.UserId);
        await fixture.Save(new TaskItem { TenantId = user.TenantId, ProjectId = hidden.Id, Title = "Marker hidden task" }, user.UserId);
        var found = await Ok(client.GetAsync("/api/v1/search?q=marker"));
        Assert.Equal(new[] { visible.Id, task.Id, personalTask.Id }.Order(), Ids(found).Order()); Assert.Equal(3, found.GetProperty("total").GetInt64());
        await Update<ScheduleAssignment>(user, assignment.Id, x => x.Cancelled = true);
        found = await Ok(client.GetAsync("/api/v1/search?q=marker")); Assert.Equal([personalTask.Id], Ids(found)); Assert.Equal(1, found.GetProperty("total").GetInt64());
        await Update<Employee>(user, employee.Id, x => x.Active = false);
        Assert.Equal(0, (await Ok(client.GetAsync("/api/v1/search?q=marker"))).GetProperty("total").GetInt64());
        await Error(client.GetAsync("/api/v1/search?q=marker&kind=quotes"), HttpStatusCode.Forbidden);
    }

    [PostgresApiFact]
    public async Task Document_search_applies_source_record_and_project_visibility_without_storage_metadata()
    {
        var (user, client) = await Session(BusinessRole.Worker); var (_, _, project) = await Project(user); var (_, _, otherProject) = await Project(user);
        var employee = await fixture.Save(new Employee { TenantId = user.TenantId, UserId = user.UserId, Name = "Owner" }, user.UserId);
        var other = await fixture.Save(new Employee { TenantId = user.TenantId, Name = "Other person" }, user.UserId);
        await fixture.Save(new ScheduleAssignment { TenantId = user.TenantId, ProjectId = project.Id, EmployeeId = employee.Id, Title = "Work", Start = WorkStart, End = WorkStart.AddHours(1) }, user.UserId);
        var ownTime = await Time(user, employee, project, "own activity"); var otherTime = await Time(user, other, project, "private activity");
        var ownDocument = await Document(user, project.Id, "DocumentMarker own", "time-entries", ownTime.Id);
        await Document(user, project.Id, "DocumentMarker other", "time-entries", otherTime.Id);
        await Document(user, otherProject.Id, "DocumentMarker unassigned"); await Document(user, null, "DocumentMarker unbound");
        var response = await Ok(client.GetAsync("/api/v1/search?q=DocumentMarker&kind=documents"));
        Assert.Equal([ownDocument.Id], Ids(response)); Assert.Equal(1, response.GetProperty("total").GetInt64());
        Assert.DoesNotContain("objectKey", response.GetRawText(), StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("versionId", response.GetRawText(), StringComparison.OrdinalIgnoreCase);
        var history = await Ok(client.GetAsync($"/api/v1/projects/{project.Id}/history"));
        Assert.Contains(ownDocument.Id, Ids(history, "entityId")); Assert.DoesNotContain(otherTime.Id, Ids(history, "entityId"));
    }

    [PostgresApiFact]
    public async Task Project_history_follows_current_incoming_links_not_shared_customers_or_banks_and_projects_generic_titles()
    {
        var (user, client) = await Session(); var (customer, building, project) = await Project(user);
        var secondProject = await fixture.Save(new Project { TenantId = user.TenantId, CustomerId = customer.Id, BuildingId = building.Id, Name = "Different private project" }, user.UserId);
        var task = await fixture.Save(new TaskItem { TenantId = user.TenantId, ProjectId = project.Id, Title = "PERSON private-payroll 998877 EUR" }, user.UserId);
        var otherTask = await fixture.Save(new TaskItem { TenantId = user.TenantId, ProjectId = secondProject.Id, Title = "Private other task" }, user.UserId);
        var invoice = await Invoice(user, customer, project); var secondInvoice = await Invoice(user, customer, secondProject);
        var bank = await fixture.Save(new BankTransaction { TenantId = user.TenantId, AccountReference = "Synthetic", ExternalId = "Synthetic", BookedOn = new(2026, 10, 20), Amount = 238, Currency = "EUR", Counterparty = "PERSON private-payroll" }, user.UserId);
        var allocation = await fixture.Save(new PaymentAllocation { TenantId = user.TenantId, InvoiceId = invoice.Id, BankTransactionId = bank.Id, Amount = 119, AllocatedAt = WorkStart, AllocatedBy = user.UserId }, user.UserId);
        var otherAllocation = await fixture.Save(new PaymentAllocation { TenantId = user.TenantId, InvoiceId = secondInvoice.Id, BankTransactionId = bank.Id, Amount = 119, AllocatedAt = WorkStart, AllocatedBy = user.UserId }, user.UserId);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            db.Audit.Add(new AuditEntry { TenantId = user.TenantId, ActorId = Guid.NewGuid(), Kind = "tasks", EntityId = task.Id, Revision = task.Revision, Action = "PERSON private-payroll 998877 EUR", ContentDigest = "secret-body" });
            await db.SaveChangesAsync();
        });
        var response = await Ok(client.GetAsync($"/api/v1/projects/{project.Id}/history?pageSize=50"));
        var ids = Ids(response, "entityId"); Assert.Contains(task.Id, ids); Assert.Contains(invoice.Id, ids); Assert.Contains(allocation.Id, ids);
        foreach (var excluded in new[] { customer.Id, building.Id, secondProject.Id, otherTask.Id, secondInvoice.Id, bank.Id, otherAllocation.Id }) Assert.DoesNotContain(excluded, ids);
        Assert.Equal("current-accessible-project-links", response.GetProperty("associationScope").GetString());
        var payload = response.GetRawText();
        foreach (var secret in new[] { "PERSON", "private-payroll", "998877", "secret-body", "actorId", "correlationId" }) Assert.DoesNotContain(secret, payload, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(response.GetProperty("items").EnumerateArray(), x => x.GetProperty("entityId").GetGuid() == task.Id && x.GetProperty("action").GetString() == "record.changed");
        await Update<TaskItem>(user, task.Id, x => x.ProjectId = secondProject.Id);
        Assert.DoesNotContain(task.Id, Ids(await Ok(client.GetAsync($"/api/v1/projects/{project.Id}/history?pageSize=50")), "entityId"));
        Assert.Contains(task.Id, Ids(await Ok(client.GetAsync($"/api/v1/projects/{secondProject.Id}/history?pageSize=50")), "entityId"));
    }

    [PostgresApiFact]
    public async Task Worker_history_hides_other_personal_and_financial_records_and_revokes_project_access_immediately()
    {
        var (user, client) = await Session(BusinessRole.Worker); var (customer, _, project) = await Project(user);
        var employee = await fixture.Save(new Employee { TenantId = user.TenantId, UserId = user.UserId, Name = "Current person" }, user.UserId);
        var other = await fixture.Save(new Employee { TenantId = user.TenantId, Name = "Private person" }, user.UserId);
        var assignment = await fixture.Save(new ScheduleAssignment { TenantId = user.TenantId, ProjectId = project.Id, EmployeeId = employee.Id, Title = "Work", Start = WorkStart, End = WorkStart.AddHours(1) }, user.UserId);
        var ownTime = await Time(user, employee, project, "Own private activity"); var otherTime = await Time(user, other, project, "Other secret activity"); var invoice = await Invoice(user, customer, project);
        var first = await Ok(client.GetAsync($"/api/v1/projects/{project.Id}/history?pageSize=1"));
        Assert.Equal(3, first.GetProperty("total").GetInt64()); Assert.Single(first.GetProperty("items").EnumerateArray());
        var all = await Ok(client.GetAsync($"/api/v1/projects/{project.Id}/history?pageSize=50"));
        Assert.Equal(new[] { project.Id, assignment.Id, ownTime.Id }.Order(), Ids(all, "entityId").Order());
        Assert.DoesNotContain(otherTime.Id, Ids(all, "entityId")); Assert.DoesNotContain(invoice.Id, Ids(all, "entityId"));
        Assert.DoesNotContain("activity", all.GetRawText(), StringComparison.OrdinalIgnoreCase);
        await Update<ScheduleAssignment>(user, assignment.Id, x => x.Cancelled = true);
        await Error(client.GetAsync($"/api/v1/projects/{project.Id}/history"), HttpStatusCode.NotFound, "not_found");
        var (foreign, _) = await Session(); var (_, _, foreignProject) = await Project(foreign);
        await Error(client.GetAsync($"/api/v1/projects/{foreignProject.Id}/history"), HttpStatusCode.NotFound, "not_found");
    }

    [PostgresApiFact]
    public async Task Self_copy_contains_only_own_current_tenant_categories_and_safe_identity_with_verified_zip_and_file_hashes()
    {
        var (user, client) = await Session(); var (_, _, project) = await Project(user);
        var otherUser = await fixture.CreateUser(user.TenantId); var own = await fixture.Save(new Employee { TenantId = user.TenantId, UserId = user.UserId, Name = "own profile deliberately excluded" }, user.UserId);
        var other = await fixture.Save(new Employee { TenantId = user.TenantId, UserId = otherUser.UserId, Name = "other-profile-secret" }, user.UserId);
        var time = await Time(user, own, project, "own-time-data"); await Time(user, other, project, "other-time-secret");
        var diary = await fixture.Save(new DiaryEntry { TenantId = user.TenantId, ProjectId = project.Id, EmployeeId = own.Id, Date = new(2026, 10, 20), Work = "own-diary-data" }, user.UserId);
        await fixture.Save(new DiaryEntry { TenantId = user.TenantId, ProjectId = project.Id, EmployeeId = other.Id, Date = new(2026, 10, 20), Work = "other-diary-secret" }, user.UserId);
        var definition = new FormDefinition { TenantId = user.TenantId, Name = "Synthetic form", Fields = [new() { Key = "answer", Label = "Answer", Kind = FormFieldKind.ShortText }] };
        FormRules.Publish(definition); await fixture.Save(definition, user.UserId);
        var submission = await fixture.Save(FormRules.Submit(definition, project.Id, user.UserId, new() { ["answer"] = "own-form-data" }, WorkStart), user.UserId);
        await fixture.Save(FormRules.Submit(definition, project.Id, otherUser.UserId, new() { ["answer"] = "other-form-secret" }, WorkStart), otherUser.UserId);
        var preference = await fixture.Save(new NotificationPreference { TenantId = user.TenantId, UserId = user.UserId }, user.UserId);
        await fixture.Save(new NotificationPreference { TenantId = user.TenantId, UserId = otherUser.UserId }, otherUser.UserId);
        var archived = await Time(user, own, project, "archived-secret", WorkStart.AddHours(2)); await Update<TimeEntry>(user, archived.Id, x => x.Archived = true);
        // The same account can belong to another tenant; that does not widen this copy's scope.
        var foreign = await fixture.CreateTenant(); var foreignSubject = new ApiTestUser(foreign.Id, user.UserId, user.Email, user.Password);
        var (_, _, foreignProject) = await Project(foreignSubject);
        var foreignEmployee = await fixture.Save(new Employee { TenantId = foreign.Id, UserId = user.UserId, Name = "same user elsewhere" }, user.UserId);
        await Time(foreignSubject, foreignEmployee, foreignProject, "foreign-time-secret");
        var files = await Copy(client); using var records = JsonDocument.Parse(files["records.json"]);
        Assert.Equal(new[] { time.Id, diary.Id, submission.Id, preference.Id }.Order(), records.RootElement.EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).Order());
        using var profile = JsonDocument.Parse(files["profile.json"]); Assert.Equal(user.UserId, profile.RootElement.GetProperty("id").GetGuid()); Assert.Equal(user.Email, profile.RootElement.GetProperty("email").GetString());
        var combined = string.Join('\n', files.Values.Select(Encoding.UTF8.GetString));
        foreach (var value in new[] { "other-profile-secret", "other-time-secret", "other-diary-secret", "other-form-secret", "archived-secret", "foreign-time-secret", "passwordHash", "securityStamp", "concurrencyStamp", "recoveryToken", "own profile deliberately excluded" })
            Assert.DoesNotContain(value, combined, StringComparison.OrdinalIgnoreCase);
        using var manifest = JsonDocument.Parse(files["manifest.json"]); Assert.Equal("self-service-data-copy", manifest.RootElement.GetProperty("purpose").GetString());
        Assert.All(manifest.RootElement.GetProperty("coverage").EnumerateArray(), item => Assert.Equal(1, item.GetProperty("records").GetInt32()));
        await fixture.InScope(async services =>
        {
            var audit = await services.GetRequiredService<FirstBlickDbContext>().Audit.IgnoreQueryFilters().SingleAsync(x => x.TenantId == user.TenantId && x.Action == "self.data-copy");
            Assert.Equal(user.UserId, audit.ActorId); Assert.Equal(user.UserId, audit.EntityId); Assert.Equal("identity", audit.Kind); Assert.Equal(64, audit.ContentDigest.Length);
        });
    }

    [PostgresApiFact]
    public async Task Self_copy_rechecks_personal_policy_role_and_membership_and_does_not_offer_other_subjects()
    {
        var (user, client) = await Session(BusinessRole.Worker); var (_, _, project) = await Project(user);
        var employee = await fixture.Save(new Employee { TenantId = user.TenantId, UserId = user.UserId, Name = "Worker" }, user.UserId);
        var time = await Time(user, employee, project, "own time");
        var files = await Copy(client); Assert.Contains(time.Id.ToString(), Encoding.UTF8.GetString(files["records.json"]));
        await Update<Employee>(user, employee.Id, x => x.Active = false);
        files = await Copy(client); Assert.Equal("[]", Encoding.UTF8.GetString(files["records.json"]));
        await ChangeMembership(user, BusinessRole.Accounting, true); await Update<Employee>(user, employee.Id, x => x.Active = true);
        files = await Copy(client); Assert.Equal("[]", Encoding.UTF8.GetString(files["records.json"]));
        using (var manifest = JsonDocument.Parse(files["manifest.json"])) Assert.All(manifest.RootElement.GetProperty("coverage").EnumerateArray(), x => Assert.False(x.GetProperty("included").GetBoolean()));
        await ChangeMembership(user, BusinessRole.Partner, true);
        files = await Copy(client); Assert.Equal("[]", Encoding.UTF8.GetString(files["records.json"]));
        await Error(client.GetAsync("/api/v1/search?q=marker"), HttpStatusCode.Forbidden);
        await ChangeMembership(user, BusinessRole.Worker, false);
        await Error(client.GetAsync("/api/v1/self/data-copy"), HttpStatusCode.Forbidden);
    }

    [PostgresApiFact]
    public async Task Self_copy_fails_closed_when_a_stored_record_disagrees_with_its_tenant_metadata()
    {
        var (user, client) = await Session(); var (_, _, project) = await Project(user);
        var employee = await fixture.Save(new Employee { TenantId = user.TenantId, UserId = user.UserId, Name = "Owner" }, user.UserId);
        var time = await Time(user, employee, project, "own-time");
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            var row = await db.Entities.IgnoreQueryFilters().SingleAsync(x => x.TenantId == user.TenantId && x.Kind == "time-entries" && x.Id == time.Id);
            var json = JsonNode.Parse(row.Data)!; json["tenantId"] = Guid.NewGuid(); row.Data = json.ToJsonString(); await db.SaveChangesAsync();
        });
        await Error(client.GetAsync("/api/v1/self/data-copy"), HttpStatusCode.BadRequest, "data_copy_source");
        var auditCount = await fixture.InScope(async services => await services.GetRequiredService<FirstBlickDbContext>().Audit.IgnoreQueryFilters().CountAsync(x => x.TenantId == user.TenantId && x.Action == "self.data-copy"));
        Assert.Equal(0, auditCount);
    }

    [PostgresApiFact]
    public async Task Search_and_self_copy_recheck_revocation_after_actually_waiting_for_the_tenant_lock()
    {
        foreach (var path in new[] { "/api/v1/search?q=marker", "/api/v1/self/data-copy" })
        {
            var (user, client) = await Session();
            await using var connection = new NpgsqlConnection(fixture.ConnectionString); await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var acquire = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@tenant,0))", connection, transaction))
            {
                acquire.Parameters.AddWithValue("tenant", user.TenantId.ToString()); await acquire.ExecuteNonQueryAsync();
            }
            var response = client.GetAsync(path); var waiting = false;
            // Observe a real PostgreSQL lock waiter, rather than assuming a sleep has reached the vulnerable point.
            for (var attempt = 0; attempt < 150 && !waiting; attempt++)
            {
                await using var inspect = new NpgsqlCommand("""
                    SELECT EXISTS(SELECT 1 FROM pg_locks WHERE locktype='advisory' AND NOT granted
                      AND database=(SELECT oid FROM pg_database WHERE datname=current_database()))
                    """, connection, transaction);
                waiting = (bool)(await inspect.ExecuteScalarAsync())!;
                if (!waiting) await Task.Delay(20);
            }
            Assert.True(waiting, "The request must reach the held tenant advisory lock before membership revocation.");
            await using (var revoke = new NpgsqlCommand("UPDATE \"Memberships\" SET \"Active\"=false,\"Revision\"=\"Revision\"+1 WHERE \"TenantId\"=@tenant AND \"UserId\"=@user", connection, transaction))
            {
                revoke.Parameters.AddWithValue("tenant", user.TenantId); revoke.Parameters.AddWithValue("user", user.UserId); Assert.Equal(1, await revoke.ExecuteNonQueryAsync());
            }
            await transaction.CommitAsync(); await Error(response, HttpStatusCode.Forbidden, "forbidden");
        }
    }

    [PostgresApiFact]
    public async Task Self_copy_rejects_a_record_budget_overrun_before_returning_any_partial_archive_or_audit()
    {
        var (user, client) = await Session(); var (_, _, project) = await Project(user);
        var employee = await fixture.Save(new Employee { TenantId = user.TenantId, UserId = user.UserId, Name = "Owner" }, user.UserId);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            // Valid, nonoverlapping source records, inserted together solely to exercise the bulk-read budget.
            for (var index = 0; index < 5001; index++)
            {
                var entry = new TimeEntry { TenantId = user.TenantId, EmployeeId = employee.Id, ProjectId = project.Id,
                    Start = WorkStart.AddHours(index * 2), End = WorkStart.AddHours(index * 2 + 1), Activity = "Synthetic budget entry" };
                entry.Validate();
                db.Entities.Add(new StoredEntity { TenantId = entry.TenantId, Kind = "time-entries", Id = entry.Id, Revision = entry.Revision,
                    Data = AppJson.Serialize(entry), CreatedAt = entry.CreatedAt, UpdatedAt = entry.UpdatedAt });
            }
            await db.SaveChangesAsync();
        });
        await Error(client.GetAsync("/api/v1/self/data-copy"), HttpStatusCode.BadRequest, "data_copy_budget");
        Assert.Equal(0, await fixture.InScope(async services => await services.GetRequiredService<FirstBlickDbContext>().Audit.IgnoreQueryFilters()
            .CountAsync(x => x.TenantId == user.TenantId && x.Action == "self.data-copy")));
    }

    private Task<TimeEntry> Time(ApiTestUser user, Employee employee, Project project, string text, DateTimeOffset? start = null) => fixture.Save(new TimeEntry
    { TenantId = user.TenantId, EmployeeId = employee.Id, ProjectId = project.Id, Start = start ?? WorkStart, End = (start ?? WorkStart).AddHours(1), Activity = text }, user.UserId);
    private async Task ChangeMembership(ApiTestUser user, BusinessRole role, bool active) => await fixture.InScope(async services =>
    {
        var db = services.GetRequiredService<FirstBlickDbContext>(); var member = await db.Memberships.SingleAsync(x => x.TenantId == user.TenantId && x.UserId == user.UserId);
        member.Role = role; member.Active = active; member.Revision++; await db.SaveChangesAsync();
    });
    private async Task<DocumentObject> Document(ApiTestUser user, Guid? projectId, string name, string? sourceKind = null, Guid? sourceId = null)
        => await fixture.InScope(async services =>
        {
            var document = new DocumentObject { TenantId = user.TenantId, UploadedBy = user.UserId, Name = name, ContentType = "text/plain", Size = 1,
                Sha256 = new string('a', 64), ObjectKey = "synthetic-private-object-" + Guid.NewGuid(), VersionId = "private-provider-version", State = "Ready",
                ProjectId = projectId, ProjectKind = projectId.HasValue ? "projects" : null, SourceKind = sourceKind, SourceId = sourceId };
            var db = services.GetRequiredService<FirstBlickDbContext>(); db.Documents.Add(document);
            db.Audit.Add(new AuditEntry { TenantId = user.TenantId, ActorId = user.UserId, Kind = "documents", EntityId = document.Id, Action = "document.ready", Revision = 1, ContentDigest = new string('b', 64) });
            await db.SaveChangesAsync(); return document;
        });
    private async Task<Invoice> Invoice(ApiTestUser user, Customer customer, Project project)
    {
        var number = Guid.NewGuid().ToString("N");
        var quote = new Quote { TenantId = user.TenantId, CustomerId = customer.Id, ProjectId = project.Id, Title = "Private contract title", Lines = [new() { Description = "Synthetic roof work", Quantity = 1, UnitMaterialCost = 100, VatPercent = 19 }] };
        CommercialWorkflow.IssueQuote(quote, "Q-" + number); CommercialWorkflow.FreezePresentation(quote, "Synthetic business", customer, WorkStart);
        CommercialWorkflow.AcceptQuote(quote, quote.IssuedDigest, WorkStart, new(2026, 10, 20)); await fixture.Save(quote, user.UserId);
        var order = CommercialWorkflow.CreateOrder(quote, "O-" + number); await fixture.Save(order, user.UserId);
        var invoice = InvoiceWorkflow.DraftFromOrder(order, customer, InvoiceKind.Final); invoice.SellerName = "Synthetic business"; invoice.SellerAddress = "Synthetic workshop";
        invoice.ServiceFrom = new(2026, 10, 1); invoice.ServiceUntil = new(2026, 10, 20); invoice.DueDate = new(2026, 11, 3);
        InvoiceWorkflow.IssueForOrder(invoice, order, [], [], "I-" + number, new(2026, 10, 20)); return await fixture.Save(invoice, user.UserId);
    }
    private static string Sha(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    private static async Task<Dictionary<string, byte[]>> Copy(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/self/data-copy"); var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {Encoding.UTF8.GetString(bytes)}");
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType); Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(Sha(bytes), response.Headers.GetValues("X-Content-SHA256").Single());
        Assert.Equal("firstblick.self-data-copy/1.0.0", response.Headers.GetValues("X-FirstBlick-Export-Schema").Single());
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read); var result = new Dictionary<string, byte[]>();
        foreach (var entry in archive.Entries)
        {
            using var stream = entry.Open(); using var destination = new MemoryStream(); await stream.CopyToAsync(destination); result.Add(entry.FullName, destination.ToArray());
        }
        Assert.Equal(new[] { "manifest.json", "membership.json", "profile.json", "records.json" }, result.Keys.Order());
        using var manifest = JsonDocument.Parse(result["manifest.json"]);
        foreach (var entry in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            var file = result[entry.GetProperty("name").GetString()!]; Assert.Equal(file.Length, entry.GetProperty("bytes").GetInt32()); Assert.Equal(Sha(file), entry.GetProperty("sha256").GetString());
        }
        return result;
    }
}
