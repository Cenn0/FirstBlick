using System.Net;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static FirstBlick.Api.Tests.PostgresApiFixture;

namespace FirstBlick.Api.Tests;

public sealed class CollectionApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);
    private static DateOnly Review => Today.AddDays(7);

    [PostgresApiFact]
    public async Task An_explicit_case_is_unique_idempotent_audited_and_has_no_external_send_job()
    {
        var g = await Setup(); var client = await fixture.Login(g.User);
        var body = new OpenCollectionRequest(g.Invoice.Id, g.User.UserId, Review, "Invoice and ledger reviewed"); var key = Guid.NewGuid().ToString("N");
        var first = await Send(client, HttpMethod.Post, "/api/v1/collection-cases/from-invoice", body, key: key);
        var value = await Success<CollectionCase>(first);
        var replay = await Send(client, HttpMethod.Post, "/api/v1/collection-cases/from-invoice", body, key: key);
        // PostgreSQL JSONB canonicalizes property order in receipts. Replay must preserve all typed values, not whitespace or ordering.
        Assert.Equal(AppJson.Serialize(value), AppJson.Serialize(await Success<CollectionCase>(replay)));
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        await Error(await Send(client, HttpMethod.Post, "/api/v1/collection-cases/from-invoice", body), "collection_exists");
        var counts = await Counts(g.User.TenantId); Assert.Equal((1, 1, 1, 1), counts);
        Assert.Equal(g.Invoice.IssuedDigest, value.InvoiceDigest); value.Validate();
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            var jobs = await db.Jobs.IgnoreQueryFilters().Where(x => x.TenantId == g.User.TenantId && x.Kind == "collection-cases").ToListAsync();
            Assert.All(jobs, job => Assert.Equal("entity.changed", job.Type)); Assert.All(jobs, job => Assert.False(job.ExternalAttemptStarted));
        });
    }

    [PostgresApiFact]
    public async Task A_payment_after_preview_rejects_old_text_and_new_approval_preserves_exact_frozen_sources()
    {
        var g = await Setup(); var client = await fixture.Login(g.User); var value = await Open(client, g);
        var before = await Counts(g.User.TenantId); var preview = await Preview(client, value);
        Assert.Equal(before, await Counts(g.User.TenantId)); Assert.Equal(119m, preview.Ledger.Balance.Collectible); Assert.Single(preview.Ledger.Sources);
        var payment = PaymentWorkflow.Allocate(g.Invoice, await Bank(g, 20), [], [], 20, 0, 0, "", g.User.UserId, DateTimeOffset.UtcNow);
        await fixture.Save(payment, g.User.UserId);
        await Error(await Send(client, HttpMethod.Post, $"/api/v1/collection-cases/{value.Id}/approve-step",
            new ApproveCollectionStepRequest(preview.Input, preview.Digest), value.Revision), "collection_preview_changed");
        Assert.Empty((await fixture.Read<CollectionCase>(g.User.TenantId, value.Id)).Steps);
        var refreshed = await Preview(client, value); Assert.Equal(99m, refreshed.Ledger.Balance.Collectible); Assert.Equal(2, refreshed.Ledger.Sources.Count);
        var key = Guid.NewGuid().ToString("N"); var approve = new ApproveCollectionStepRequest(refreshed.Input, refreshed.Digest);
        var result = await Success<CollectionCase>(await Send(client, HttpMethod.Post, $"/api/v1/collection-cases/{value.Id}/approve-step", approve, value.Revision, key));
        var replay = await Success<CollectionCase>(await Send(client, HttpMethod.Post, $"/api/v1/collection-cases/{value.Id}/approve-step", approve, value.Revision, key));
        Assert.Equal(result.StateDigest, replay.StateDigest); Assert.Single(result.Steps); Assert.Equal(99m, result.Steps[0].Preview.Ledger.Balance.Collectible); result.Validate();
        await Error(await Send(client, HttpMethod.Post, $"/api/v1/collection-cases/{value.Id}/approve-step", approve, value.Revision), "revision_conflict", HttpStatusCode.Conflict);
    }

    [PostgresApiFact]
    public async Task Dispute_resolution_manual_closure_and_reopening_leave_invoice_and_payment_ledger_unchanged()
    {
        var g = await Setup(); var client = await fixture.Login(g.User); var value = await Open(client, g);
        var invoiceBefore = AppJson.Serialize(await fixture.Read<Invoice>(g.User.TenantId, g.Invoice.Id));
        value = await Action<OpenCollectionDisputeRequest>(client, value, "dispute", new(10.25m, "Partial disputed measurement", "Customer letter 1", Review));
        Assert.Equal(CollectionCaseStatus.Disputed, value.Status);
        await Error(await Send(client, HttpMethod.Post, $"/api/v1/collection-cases/{value.Id}/step-preview", new CollectionPreviewRequest(Input()), value.Revision), "collection_step_blocked");
        value = await Action<ResolveCollectionDisputeRequest>(client, value, "resolve-dispute", new("Measurement clarified", "Signed meeting note", Review));
        Assert.Equal(CollectionCaseStatus.Active, value.Status); Assert.NotNull(value.Disputes[0].ResolvedBy);
        await Error(await Send(client, HttpMethod.Post, $"/api/v1/collection-cases/{value.Id}/close", new CloseCollectionRequest(CollectionClosureKind.Settled, "Paid"), value.Revision), "collection_not_settled");
        value = await Action<CloseCollectionRequest>(client, value, "close", new(CollectionClosureKind.ManualHandling, "Owner follows separately", "Owner instruction"));
        Assert.Equal(CollectionCaseStatus.Closed, value.Status);
        await Error(await Send(client, HttpMethod.Post, "/api/v1/collection-cases/from-invoice", new OpenCollectionRequest(g.Invoice.Id, g.User.UserId, Review, "New case")), "collection_exists");
        value = await Action<ReopenCollectionRequest>(client, value, "reopen", new(g.User.UserId, Review, "Resume handling", "Owner decision"));
        Assert.Equal(CollectionCaseStatus.Active, value.Status); Assert.Single(value.Closures); Assert.NotNull(value.Closures[0].ReopenedAt);
        Assert.Equal(invoiceBefore, AppJson.Serialize(await fixture.Read<Invoice>(g.User.TenantId, g.Invoice.Id)));
        value.Validate();
    }

    [PostgresApiFact]
    public async Task Contact_recording_binds_the_approved_text_and_actual_utc_contact_evidence()
    {
        var g = await Setup(); var client = await fixture.Login(g.User); var value = await Open(client, g); var preview = await Preview(client, value);
        value = await Action<ApproveCollectionStepRequest>(client, value, "approve-step", new(preview.Input, preview.Digest)); var step = value.Steps.Single();
        await Error(await Send(client, HttpMethod.Post, $"/api/v1/collection-cases/{value.Id}/record-contact",
            new RecordCollectionContactRequest(step.Id, step.Digest, CollectionContactChannel.Email, DateTimeOffset.UtcNow.AddDays(1), "Future message"), value.Revision), "invalid_collection_time");
        var at = DateTimeOffset.UtcNow;
        value = await Action<RecordCollectionContactRequest>(client, value, "record-contact", new(step.Id, step.Digest, CollectionContactChannel.Email, at, "Saved sent-email copy MAIL-17"));
        Assert.Equal(at, value.Steps[0].Contact!.OccurredAt); Assert.Equal(g.User.UserId, value.Steps[0].Contact!.RecordedBy);
        Assert.Equal(step.Preview.Text, value.Steps[0].Preview.Text); value.Validate();
    }

    [PostgresApiFact]
    public async Task Worklist_filters_before_paging_uses_frozen_invoice_labels_and_never_loads_deep_evidence()
    {
        var g = await Setup(); var client = await fixture.Login(g.User); var first = await Open(client, g);
        var secondInvoice = await Invoice(g.User, "Frozen special %_ customer", "COL-2");
        var second = await Open(client, (g.User, secondInvoice));
        var thirdInvoice = await Invoice(g.User, "Frozen normal customer", "COL-3");
        var third = await Open(client, (g.User, thirdInvoice));
        second = await Action<CloseCollectionRequest>(client, second, "close", new(CollectionClosureKind.ManualHandling, "Owner handles case", "Owner note"));
        await fixture.InScope(async services =>
        {
            var scope = services.GetRequiredService<TenantScope>(); scope.TenantId = g.User.TenantId; scope.UserId = g.User.UserId;
            var store = services.GetRequiredService<EntityStore>(); var customer = await store.Get<Customer>(secondInvoice.CustomerId);
            customer.Name = "Changed customer master data"; await store.Save(customer, "test.update-master", false); await services.GetRequiredService<FirstBlickDbContext>().SaveChangesAsync();
        });
        var literal = await Success<CollectionWorklistResponse>(await client.GetAsync("/api/v1/collection-cases/worklist?q=%25_&page=1&pageSize=1"));
        Assert.Equal(1, literal.Total); Assert.Equal(second.Id, Assert.Single(literal.Items).Id); Assert.Equal("Frozen special %_ customer", literal.Items[0].BuyerName);
        var closed = await Success<CollectionWorklistResponse>(await client.GetAsync("/api/v1/collection-cases/worklist?status=Closed&pageSize=1"));
        Assert.Equal(1, closed.Total); Assert.Equal(second.Id, Assert.Single(closed.Items).Id);
        var allFirst = await Success<CollectionWorklistResponse>(await client.GetAsync("/api/v1/collection-cases/worklist?page=1&pageSize=1"));
        var allSecond = await Success<CollectionWorklistResponse>(await client.GetAsync("/api/v1/collection-cases/worklist?page=2&pageSize=1"));
        Assert.Equal(3, allFirst.Total); Assert.Equal(3, allSecond.Total); Assert.NotEqual(allFirst.Items[0].Id, allSecond.Items[0].Id);
        var raw = await client.GetAsync("/api/v1/collection-cases/worklist"); var json = await raw.Content.ReadAsStringAsync();
        Assert.DoesNotContain("steps", json); Assert.DoesNotContain("events", json); Assert.DoesNotContain("ledger", json); Assert.Contains("no-store", raw.Headers.CacheControl!.ToString());
        Assert.NotEqual(first.Id, third.Id);
    }

    [PostgresApiFact]
    public async Task Worklist_rejects_duplicate_unknown_malformed_and_unsupported_query_values()
    {
        var g = await Setup(); var client = await fixture.Login(g.User);
        foreach (var query in new[] { "page=1&page=2", "status=Active&status=Closed", "q=ab&q=cd", "unknown=1", "page=-1", "pageSize=101", "page=0", "status=3", "status=active", "q=x", "invoiceId=abc", "invoiceId=00000000-0000-0000-0000-000000000000" })
        {
            var response = await client.GetAsync("/api/v1/collection-cases/worklist?" + query);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [PostgresApiFact]
    public async Task Roles_tenants_module_disabling_and_current_assignees_are_enforced_server_side()
    {
        var g = await Setup(); var client = await fixture.Login(g.User); var value = await Open(client, g);
        foreach (var role in Enum.GetValues<BusinessRole>())
        {
            var user = await fixture.CreateUser(g.User.TenantId, role); var roleClient = await fixture.Login(user);
            var allowed = role is BusinessRole.ManagingDirector or BusinessRole.Office or BusinessRole.Accounting;
            foreach (var path in new[] { "/api/v1/collection-cases/worklist", $"/api/v1/collection-cases/{value.Id}", $"/api/v1/collection-cases/{value.Id}/balance", "/api/v1/collection-cases/assignees" })
                Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, (await roleClient.GetAsync(path)).StatusCode);
        }
        var foreign = await Setup(); var foreignClient = await fixture.Login(foreign.User);
        Assert.Equal(HttpStatusCode.NotFound, (await foreignClient.GetAsync($"/api/v1/collection-cases/{value.Id}")).StatusCode);
        await Error(await Send(client, HttpMethod.Post, $"/api/v1/collection-cases/{value.Id}/review", new ReviewCollectionRequest(foreign.User.UserId, Review, "Foreign owner"), value.Revision), "collection_owner_unavailable");
        var worker = await fixture.CreateUser(g.User.TenantId, BusinessRole.Worker);
        await Error(await Send(client, HttpMethod.Post, $"/api/v1/collection-cases/{value.Id}/review", new ReviewCollectionRequest(worker.UserId, Review, "Non-finance owner"), value.Revision), "collection_owner_unavailable");
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>(); var tenant = await db.Tenants.SingleAsync(x => x.Id == g.User.TenantId);
            tenant.Modules = tenant.Modules.Where(x => x != "finance").ToArray(); await db.SaveChangesAsync();
        });
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/collection-cases/{value.Id}")).StatusCode);
        await Error(await Send(client, HttpMethod.Post, $"/api/v1/collection-cases/{value.Id}/step-preview", new CollectionPreviewRequest(Input()), value.Revision), "module_disabled", HttpStatusCode.Forbidden);
        await Error(await Send(client, HttpMethod.Post, $"/api/v1/collection-cases/{value.Id}/review", new ReviewCollectionRequest(g.User.UserId, Review.AddDays(1), "Next review"), value.Revision), "module_disabled", HttpStatusCode.Forbidden);
    }

    [PostgresApiFact]
    public async Task Generic_replace_create_and_archive_are_rejected_without_history_or_receipt_changes()
    {
        var g = await Setup(); var client = await fixture.Login(g.User); var value = await Open(client, g); var before = await Counts(g.User.TenantId);
        await Error(await Send(client, HttpMethod.Post, "/api/v1/collection-cases", value), "command_required");
        await Error(await Send(client, HttpMethod.Put, $"/api/v1/collection-cases/{value.Id}", value, value.Revision), "command_required");
        await Error(await Send(client, HttpMethod.Delete, $"/api/v1/collection-cases/{value.Id}", new { }, value.Revision), "command_required");
        Assert.Equal(before, await Counts(g.User.TenantId)); Assert.False((await fixture.Read<CollectionCase>(g.User.TenantId, value.Id)).Archived);
    }

    [PostgresApiFact]
    public async Task Metadata_mismatch_fails_closed_instead_of_returning_a_partial_or_foreign_balance()
    {
        var g = await Setup(); var client = await fixture.Login(g.User); var value = await Open(client, g);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Entities\" SET \"Data\"=jsonb_set(\"Data\",'{{revision}}','999'::jsonb) WHERE \"TenantId\"={g.User.TenantId} AND \"Kind\"='invoices' AND \"Id\"={g.Invoice.Id}");
        });
        await Error(await client.GetAsync($"/api/v1/collection-cases/{value.Id}/balance"), "collection_source_metadata");
    }

    [PostgresApiFact]
    public async Task Over_budget_sources_return_no_partial_balance_or_preview()
    {
        var g = await Setup(); var client = await fixture.Login(g.User); var value = await Open(client, g); var bank = await Bank(g, 100);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>(); var now = DateTimeOffset.UtcNow;
            for (var i = 0; i < 1000; i++)
            {
                var payment = new PaymentAllocation { TenantId = g.User.TenantId, InvoiceId = g.Invoice.Id, BankTransactionId = bank.Id,
                    Amount = 0.01m, AllocatedAt = now, AllocatedBy = g.User.UserId };
                payment.PostedDigest = PaymentWorkflow.AllocationDigest(payment); payment.Validate();
                db.Entities.Add(new StoredEntity { TenantId = g.User.TenantId, Kind = "payment-allocations", Id = payment.Id, Data = AppJson.Serialize(payment),
                    Revision = payment.Revision, CreatedAt = payment.CreatedAt, UpdatedAt = payment.UpdatedAt });
            }
            await db.SaveChangesAsync();
        });
        await Error(await client.GetAsync($"/api/v1/collection-cases/{value.Id}/balance"), "collection_source_budget");
        await Error(await Send(client, HttpMethod.Post, $"/api/v1/collection-cases/{value.Id}/step-preview", new CollectionPreviewRequest(Input()), value.Revision), "collection_source_budget");
    }

    [PostgresApiFact]
    public async Task Current_role_is_rechecked_after_observed_database_lock_wait_before_sensitive_reads()
    {
        var g = await Setup(); var client = await fixture.Login(g.User); _ = await Open(client, g);
        await using var connection = new NpgsqlConnection(fixture.ConnectionString); await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using (var acquire = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@tenant,0))", connection, tx))
        { acquire.Parameters.AddWithValue("tenant", g.User.TenantId.ToString()); await acquire.ExecuteNonQueryAsync(); }
        var response = client.GetAsync("/api/v1/collection-cases/worklist"); var waiting = false;
        for (var attempt = 0; attempt < 150 && !waiting; attempt++)
        {
            await using var inspect = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_locks WHERE locktype='advisory' AND NOT granted AND database=(SELECT oid FROM pg_database WHERE datname=current_database()))", connection, tx);
            waiting = (bool)(await inspect.ExecuteScalarAsync())!; if (!waiting) await Task.Delay(20);
        }
        Assert.True(waiting, "Observe the request waiting before withdrawing its role.");
        await using (var revoke = new NpgsqlCommand("UPDATE \"Memberships\" SET \"Role\"='Worker',\"Revision\"=\"Revision\"+1 WHERE \"TenantId\"=@tenant AND \"UserId\"=@user", connection, tx))
        { revoke.Parameters.AddWithValue("tenant", g.User.TenantId); revoke.Parameters.AddWithValue("user", g.User.UserId); Assert.Equal(1, await revoke.ExecuteNonQueryAsync()); }
        await tx.CommitAsync(); var result = await response; Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        Assert.DoesNotContain(g.Invoice.BuyerName, await result.Content.ReadAsStringAsync());
    }

    private Task<(int Cases, int Audit, int Jobs, int Receipts)> Counts(Guid tenant) => fixture.InScope(async services =>
    {
        var db = services.GetRequiredService<FirstBlickDbContext>();
        return (await db.Entities.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenant && x.Kind == "collection-cases"),
            await db.Audit.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenant && x.Kind == "collection-cases"),
            await db.Jobs.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenant && x.Kind == "collection-cases"),
            await db.Receipts.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenant));
    });
    private async Task<(ApiTestUser User, Invoice Invoice)> Setup()
    {
        var tenant = await fixture.CreateTenant(); var user = await fixture.CreateUser(tenant.Id);
        return (user, await Invoice(user, "Frozen original invoice customer", "COL-1"));
    }
    private async Task<Invoice> Invoice(ApiTestUser user, string customerName, string number)
    {
        // This isolated historical fixture is explicit: the normal HTTP issue command never backdates an issued invoice.
        var customer = await fixture.Save(new Customer { TenantId = user.TenantId, Name = customerName, Address = "Synthetic customer address" }, user.UserId);
        var building = await fixture.Save(new Building { TenantId = user.TenantId, CustomerId = customer.Id, Name = "Synthetic retained roof", Address = "Synthetic roof address" }, user.UserId);
        var project = await fixture.Save(new Project { TenantId = user.TenantId, CustomerId = customer.Id, BuildingId = building.Id, Name = "Historical collection test" }, user.UserId);
        var quote = new Quote { TenantId = user.TenantId, CustomerId = customer.Id, ProjectId = project.Id, Title = "Historical quoted work",
            Lines = [new() { Description = "Synthetic work", UnitMaterialCost = 100m, VatPercent = 19 }] };
        CommercialWorkflow.IssueQuote(quote, "Q-" + number); CommercialWorkflow.AcceptQuote(quote, quote.Snapshot!.Digest, DateTimeOffset.UtcNow.AddDays(-31), Today.AddDays(-31));
        await fixture.Save(quote, user.UserId); var order = await fixture.Save(CommercialWorkflow.CreateOrder(quote, "O-" + number), user.UserId);
        var invoice = InvoiceWorkflow.DraftFromOrder(order, customer); invoice.SellerName = "Synthetic contractor"; invoice.SellerAddress = "Synthetic workshop address";
        invoice.ServiceFrom = Today.AddDays(-31); invoice.ServiceUntil = Today.AddDays(-30); invoice.DueDate = Today.AddDays(-15);
        InvoiceWorkflow.IssueForOrder(invoice, order, [], [], number, Today.AddDays(-30)); return await fixture.Save(invoice, user.UserId);
    }
    private async Task<BankTransaction> Bank((ApiTestUser User, Invoice Invoice) g, decimal amount)
    {
        var bank = new BankTransaction { TenantId = g.User.TenantId, AccountReference = "Synthetic read-only bank import", ExternalId = Guid.NewGuid().ToString(), Amount = amount, BookedOn = Today };
        PaymentWorkflow.SealImport(bank); return await fixture.Save(bank, g.User.UserId);
    }
    private static CollectionStepInput Input() => new(CollectionStage.PaymentReminder, Review, "Current invoice and ledger reviewed", "Please confirm receipt.");
    private static Task<CollectionCase> Open(HttpClient client, (ApiTestUser User, Invoice Invoice) g) => Success<CollectionCase>(Send(client, HttpMethod.Post,
        "/api/v1/collection-cases/from-invoice", new OpenCollectionRequest(g.Invoice.Id, g.User.UserId, Review, "Invoice and ledger reviewed")));
    private static Task<CollectionPreview> Preview(HttpClient client, CollectionCase value) => Success<CollectionPreview>(Send(client, HttpMethod.Post,
        $"/api/v1/collection-cases/{value.Id}/step-preview", new CollectionPreviewRequest(Input()), value.Revision));
    private static Task<CollectionCase> Action<T>(HttpClient client, CollectionCase value, string action, T body) => Success<CollectionCase>(Send(client, HttpMethod.Post,
        $"/api/v1/collection-cases/{value.Id}/{action}", body, value.Revision));
    private static async Task<T> Success<T>(Task<HttpResponseMessage> pending) => await Success<T>(await pending);
    private static async Task<T> Success<T>(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode, json); return AppJson.Read<T>(json);
    }
    private static async Task Error(HttpResponseMessage response, string code, HttpStatusCode expected = HttpStatusCode.BadRequest)
    {
        var body = await response.Content.ReadAsStringAsync(); Assert.Equal(expected, response.StatusCode);
        Assert.Equal(code, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
    }
}
