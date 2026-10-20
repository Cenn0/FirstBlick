using System.Net;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FirstBlick.Api.Tests.PostgresApiFixture;

namespace FirstBlick.Api.Tests;

public sealed class CommercialApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    [PostgresApiFact]
    public async Task Quote_delivery_acceptance_invoice_discount_retention_credit_and_refund_keep_distinct_auditable_states()
    {
        var (person, client) = await Session(); var graph = await ProjectGraph(client);
        var draft = await DraftQuote(client, graph);
        var key = Guid.NewGuid().ToString("N");
        var frozen = await Ok<Quote>(Send(client, HttpMethod.Post, $"/api/v1/quotes/{draft.Id}/freeze", new { }, draft.Revision, key));
        var replay = await Send(client, HttpMethod.Post, $"/api/v1/quotes/{draft.Id}/freeze", new { }, draft.Revision, key);
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(frozen.Snapshot!.Digest, (await Ok<Quote>(Task.FromResult(replay))).Snapshot!.Digest);
        Assert.Equal(119m, frozen.Snapshot.Gross); Assert.Equal(QuoteStatus.Issued, frozen.Status);
        var queued = await Ok<Quote>(Send(client, HttpMethod.Post, $"/api/v1/quotes/{frozen.Id}/delivery", new { channel = "Email", recipient = "customer@example.test" }, frozen.Revision));
        Assert.Equal(QuoteStatus.Issued, queued.Status); Assert.Equal(DeliveryState.Queued, Assert.Single(queued.Deliveries).State);
        await Error(Send(client, HttpMethod.Post, $"/api/v1/quotes/{frozen.Id}/accept", new { digest = frozen.Snapshot.Digest }, frozen.Revision), HttpStatusCode.Conflict, "revision_conflict");
        var accepted = await Ok<Quote>(Send(client, HttpMethod.Post, $"/api/v1/quotes/{frozen.Id}/accept", new { digest = frozen.Snapshot.Digest }, queued.Revision));
        var order = await Ok<Order>(Send(client, HttpMethod.Post, $"/api/v1/orders/from-quote/{accepted.Id}", new { }, accepted.Revision));
        Assert.Equal(frozen.Snapshot.Digest, order.Contract!.Digest);
        var invoice = await IssueInvoice(client, order);
        await AssertOpenCounts(client, 1);
        var originalDigest = invoice.IssuedDigest;
        invoice.SellerAddress = "FORGED ADDRESS";
        await Error(Send(client, HttpMethod.Put, $"/api/v1/invoices/{invoice.Id}", invoice, invoice.Revision), HttpStatusCode.BadRequest, "invoice_immutable");
        invoice = await fixture.Read<Invoice>(person.TenantId, invoice.Id); Assert.Equal(originalDigest, invoice.IssuedDigest);
        var bank = await Import(client, 100m);
        var payment = await Ok<PaymentAllocation>(Send(client, HttpMethod.Post, "/api/v1/payments/allocate", new
        { invoiceId = invoice.Id, bankTransactionId = bank.Id, amount = 100m, discountAmount = 9m, retainedAmount = 10m, reason = "Agreed synthetic deduction and retention" }, bank.Revision));
        var balance = await Ok<OpenInvoice>(client.GetAsync($"/api/v1/invoices/{invoice.Id}/balance"));
        Assert.Equal(10m, balance.Outstanding); Assert.Equal(0m, balance.Collectible); Assert.Equal(9m, balance.Discount);
        await AssertOpenCounts(client, 1);
        await Ok<PaymentAllocation>(Send(client, HttpMethod.Post, $"/api/v1/payments/{payment.Id}/release-retention", new { amount = 4m, reason = "Partial completion accepted" }, payment.Revision));
        balance = await Ok<OpenInvoice>(client.GetAsync($"/api/v1/invoices/{invoice.Id}/balance"));
        Assert.Equal(10m, balance.Outstanding); Assert.Equal(4m, balance.Collectible); Assert.Equal(6m, balance.Retained);
        var credit = await Ok<Invoice>(Send(client, HttpMethod.Post, $"/api/v1/invoices/{invoice.Id}/correction", new { reason = "Full cancellation of synthetic invoice" }, invoice.Revision));
        credit = await Ok<Invoice>(Send(client, HttpMethod.Post, $"/api/v1/invoices/{credit.Id}/issue", new { }, credit.Revision));
        Assert.Equal(InvoiceKind.CreditNote, credit.Kind); Assert.Equal(originalDigest, (await fixture.Read<Invoice>(person.TenantId, invoice.Id)).IssuedDigest);
        balance = await Ok<OpenInvoice>(client.GetAsync($"/api/v1/invoices/{invoice.Id}/balance")); Assert.Equal(100m, balance.CustomerCredit);
        await AssertOpenCounts(client, 0);
        var debit = await Import(client, -100m);
        var refund = await Ok<PaymentAllocation>(Send(client, HttpMethod.Post, "/api/v1/payments/refund", new
        { invoiceId = invoice.Id, bankTransactionId = debit.Id, amount = 100m, reason = "Reconcile refund already present in bank data" }, debit.Revision));
        Assert.Equal(PaymentDirection.Refund, refund.Direction);
        balance = await Ok<OpenInvoice>(client.GetAsync($"/api/v1/invoices/{invoice.Id}/balance"));
        Assert.Equal(0m, balance.CustomerCredit); Assert.Equal(0m, balance.Outstanding);
        await AssertOpenCounts(client, 0);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            var job = await db.Jobs.IgnoreQueryFilters().SingleAsync(x => x.TenantId == person.TenantId && x.Type == "quote.delivery");
            Assert.Equal(JobState.Pending, job.State); Assert.Equal(0, job.Attempts);
            Assert.True(await db.Audit.IgnoreQueryFilters().AnyAsync(x => x.TenantId == person.TenantId && x.Action == "payment.refund_reconcile"));
        });
    }

    [PostgresApiFact]
    public async Task Concurrent_order_creation_is_unique_and_stale_or_reused_request_keys_cannot_change_content()
    {
        var (person, client) = await Session(); var graph = await ProjectGraph(client);
        var quote = await AcceptedQuote(client, graph);
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Send(client, HttpMethod.Post, $"/api/v1/orders/from-quote/{quote.Id}", new { }, quote.Revision)));
        Assert.Single(responses, x => x.IsSuccessStatusCode); Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        var order = await Ok<Order>(Task.FromResult(responses.Single(x => x.IsSuccessStatusCode)));
        Assert.Equal(quote.Snapshot!.Digest, order.Contract!.Digest);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            Assert.Equal(1, await db.Entities.IgnoreQueryFilters().CountAsync(x => x.TenantId == person.TenantId && x.Kind == "orders"));
            Assert.Equal(1, await db.Audit.IgnoreQueryFilters().CountAsync(x => x.TenantId == person.TenantId && x.Action == "order.from_quote"));
        });
        var draft = await DraftQuote(client, graph); var key = Guid.NewGuid().ToString("N");
        await Ok<Quote>(Send(client, HttpMethod.Post, $"/api/v1/quotes/{draft.Id}/freeze", new { }, draft.Revision, key));
        await Error(Send(client, HttpMethod.Post, $"/api/v1/quotes/{draft.Id}/freeze", new { }, draft.Revision + 1, key), HttpStatusCode.Conflict, "idempotency_conflict");
        await Error(Send(client, HttpMethod.Post, $"/api/v1/quotes/{draft.Id}/reject", new { }), (HttpStatusCode)428, "revision_required");
    }

    [PostgresApiFact]
    public async Task Concurrent_partial_allocations_use_current_bank_capacity_and_real_returns_never_free_the_original_bank_row()
    {
        var (_, client) = await Session(); var graph = await ProjectGraph(client);
        var firstInvoice = await InvoiceFor(client, graph); var secondInvoice = await InvoiceFor(client, graph); var bank = await Import(client, 100m);
        var invoices = new[] { firstInvoice, secondInvoice };
        var attempts = await Task.WhenAll(invoices.Select(invoice => Send(client, HttpMethod.Post, "/api/v1/payments/allocate", new
        { invoiceId = invoice.Id, bankTransactionId = bank.Id, amount = 80m }, bank.Revision)));
        Assert.Single(attempts, x => x.IsSuccessStatusCode); Assert.Single(attempts, x => x.StatusCode == HttpStatusCode.BadRequest);
        var first = await Ok<PaymentAllocation>(Task.FromResult(attempts.Single(x => x.IsSuccessStatusCode)));
        await Ok<PaymentAllocation>(Send(client, HttpMethod.Post, $"/api/v1/payments/{first.Id}/reverse", new { reason = "Manual wrong-invoice reconciliation correction" }, first.Revision));
        var replacement = await Ok<PaymentAllocation>(Send(client, HttpMethod.Post, "/api/v1/payments/allocate", new
        { invoiceId = invoices.Single(x => x.Id != first.InvoiceId).Id, bankTransactionId = bank.Id, amount = 80m }, bank.Revision));
        var returned = await Import(client, -80m);
        await Ok<PaymentAllocation>(Send(client, HttpMethod.Post, $"/api/v1/payments/{replacement.Id}/reverse", new
        { reason = "Bank return present in imported ledger", returnBankTransactionId = returned.Id }, replacement.Revision));
        var originalBalance = await Ok<JsonElement>(client.GetAsync($"/api/v1/bank-transactions/{bank.Id}/balance"));
        var returnBalance = await Ok<JsonElement>(client.GetAsync($"/api/v1/bank-transactions/{returned.Id}/balance"));
        Assert.Equal(20m, originalBalance.GetProperty("unallocated").GetDecimal()); Assert.Equal(0m, returnBalance.GetProperty("unallocated").GetDecimal());
        await Error(Send(client, HttpMethod.Post, "/api/v1/payments/allocate", new { invoiceId = firstInvoice.Id, bankTransactionId = bank.Id, amount = 80m }, bank.Revision), HttpStatusCode.BadRequest, "bank_overallocated");
        bank.Amount = 1_000_000m;
        await Error(Send(client, HttpMethod.Put, $"/api/v1/bank-transactions/{bank.Id}", bank, bank.Revision), HttpStatusCode.BadRequest, "command_required");
    }

    [PostgresApiFact]
    public async Task Supplier_and_customer_reconciliations_share_the_same_debit_capacity()
    {
        var (_, client) = await Session(); var graph = await ProjectGraph(client); var invoice = await InvoiceFor(client, graph);
        var receipt = await Import(client, 119m);
        await Ok<PaymentAllocation>(Send(client, HttpMethod.Post, "/api/v1/payments/allocate", new { invoiceId = invoice.Id, bankTransactionId = receipt.Id, amount = 119m }, receipt.Revision));
        await AssertOpenCounts(client, 0);
        var credit = await Ok<Invoice>(Send(client, HttpMethod.Post, $"/api/v1/invoices/{invoice.Id}/correction", new { reason = "Cancellation" }, invoice.Revision));
        await Ok<Invoice>(Send(client, HttpMethod.Post, $"/api/v1/invoices/{credit.Id}/issue", new { }, credit.Revision));
        var supplier = await Create<Supplier>(client, "suppliers", new() { Name = "Synthetic supplier" });
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var payable = await Create<SupplierInvoice>(client, "supplier-invoices", new() { SupplierId = supplier.Id, Number = "SI-001", Title = "Materials received",
            SupplierName = supplier.Name, SupplierAddress = "Supplier test address", InvoiceDate = today, DueDate = today.AddDays(14), Net = 100m, Tax = 0, Gross = 100m });
        payable = await Ok<SupplierInvoice>(Send(client, HttpMethod.Post, $"/api/v1/supplier-invoices/{payable.Id}/approve", new { }, payable.Revision));
        var debit = await Import(client, -100m);
        var responses = await Task.WhenAll(
            Send(client, HttpMethod.Post, "/api/v1/payments/refund", new { invoiceId = invoice.Id, bankTransactionId = debit.Id, amount = 80m, reason = "Customer refund portion" }, debit.Revision),
            Send(client, HttpMethod.Post, "/api/v1/supplier-payments/allocate", new { supplierInvoiceId = payable.Id, bankTransactionId = debit.Id, amount = 80m }, debit.Revision));
        Assert.Single(responses, x => x.IsSuccessStatusCode); Assert.Single(responses, x => x.StatusCode == HttpStatusCode.BadRequest);
        var balance = await Ok<JsonElement>(client.GetAsync($"/api/v1/bank-transactions/{debit.Id}/balance"));
        Assert.Equal(80m, balance.GetProperty("allocated").GetDecimal()); Assert.Equal(20m, balance.GetProperty("unallocated").GetDecimal());
        Assert.Equal("read-only", balance.GetProperty("mode").GetString());
    }

    [PostgresApiFact]
    public async Task Purchase_receipts_reservations_and_consumption_are_atomic_and_concurrent_reservations_cannot_overdraw()
    {
        var (_, client) = await Session(); var graph = await ProjectGraph(client);
        var supplier = await Create<Supplier>(client, "suppliers", new() { Name = "Synthetic material supplier" });
        var material = await Create<Material>(client, "materials", new() { Name = "Synthetic tile", Unit = "Stk" });
        var purchase = await Create<PurchaseOrder>(client, "purchase-orders", new() { SupplierId = supplier.Id, ProjectId = graph.Project.Id, Title = "Ten tiles",
            Lines = [new() { MaterialId = material.Id, Description = "Tile", Quantity = 10m, UnitNet = 10m, VatPercent = 19m }] });
        purchase = await Ok<PurchaseOrder>(Send(client, HttpMethod.Post, $"/api/v1/purchase-orders/{purchase.Id}/issue", new { }, purchase.Revision));
        var received = await Ok<JsonElement>(Send(client, HttpMethod.Post, $"/api/v1/purchase-orders/{purchase.Id}/receive", new
        { lineId = purchase.Lines.Single().Id, quantity = 10m, location = "Hauptlager" }, purchase.Revision));
        Assert.Equal("Received", received.GetProperty("order").GetProperty("status").GetString());
        var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Send(client, HttpMethod.Post, $"/api/v1/materials/{material.Id}/reserve", new
        { projectId = graph.Project.Id, quantity = 8m, location = "Hauptlager" }, material.Revision)));
        Assert.Single(attempts, x => x.IsSuccessStatusCode); Assert.Single(attempts, x => x.StatusCode == HttpStatusCode.BadRequest);
        var reservation = await Ok<MaterialReservation>(Task.FromResult(attempts.Single(x => x.IsSuccessStatusCode)));
        var stock = await Ok<StockBalance>(client.GetAsync($"/api/v1/materials/{material.Id}/stock"));
        Assert.Equal(new StockBalance(10m, 8m, 2m), stock);
        var consumed = await Ok<JsonElement>(Send(client, HttpMethod.Post, $"/api/v1/material-reservations/{reservation.Id}/consume", new { quantity = 3m, unitCost = 10m }, reservation.Revision));
        await Error(Send(client, HttpMethod.Post, $"/api/v1/material-reservations/{reservation.Id}/release", new { quantity = 5m }, reservation.Revision), HttpStatusCode.Conflict, "revision_conflict");
        await Ok<MaterialReservation>(Send(client, HttpMethod.Post, $"/api/v1/material-reservations/{reservation.Id}/release", new { quantity = 5m }, consumed.GetProperty("reservation").GetProperty("revision").GetInt64()));
        stock = await Ok<StockBalance>(client.GetAsync($"/api/v1/materials/{material.Id}/stock"));
        Assert.Equal(new StockBalance(7m, 0m, 7m), stock);
        await Error(Send(client, HttpMethod.Post, "/api/v1/stock-movements", new StockMovement()), HttpStatusCode.BadRequest, "command_required");
    }

    [PostgresApiFact]
    public async Task Published_form_submissions_bind_the_schema_and_worker_project_authority()
    {
        var (director, manager) = await Session(); var ownGraph = await ProjectGraph(manager); var otherGraph = await ProjectGraph(manager);
        var worker = await fixture.CreateUser(director.TenantId, BusinessRole.Worker); var client = await fixture.Login(worker);
        var employee = await Create<Employee>(manager, "employees", new() { Name = "Assigned worker", UserId = worker.UserId });
        var today = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        await Create<ScheduleAssignment>(manager, "schedule-assignments", new() { EmployeeId = employee.Id, ProjectId = ownGraph.Project.Id, Title = "Assigned field work", Start = today.AddHours(8), End = today.AddHours(16) });
        var form = await Create<FormDefinition>(manager, "form-definitions", new() { Name = "Synthetic inspection", Fields = [new() { Key = "checked", Label = "Inspected", Kind = FormFieldKind.YesNo, Required = true }] });
        form = await Ok<FormDefinition>(Send(manager, HttpMethod.Post, $"/api/v1/form-definitions/{form.Id}/publish", new { }, form.Revision));
        await Error(Send(client, HttpMethod.Post, $"/api/v1/form-definitions/{form.Id}/submit", new { projectId = otherGraph.Project.Id, values = new { @checked = "true" } }, form.Revision), HttpStatusCode.NotFound, "not_found");
        await Error(Send(client, HttpMethod.Post, $"/api/v1/form-definitions/{form.Id}/submit", new { projectId = ownGraph.Project.Id, values = new { @checked = "true", injected = "extra" } }, form.Revision), HttpStatusCode.BadRequest, "unknown_field");
        var submission = await Ok<FormSubmission>(Send(client, HttpMethod.Post, $"/api/v1/form-definitions/{form.Id}/submit", new { projectId = ownGraph.Project.Id, values = new { @checked = "true" } }, form.Revision));
        Assert.Equal(worker.UserId, submission.SubmittedBy); Assert.Equal(form.PublishedDigest, submission.DefinitionDigest); Assert.NotEmpty(submission.SubmittedDigest);
        form.Name = "Alter frozen schema";
        await Error(Send(manager, HttpMethod.Put, $"/api/v1/form-definitions/{form.Id}", form, form.Revision), HttpStatusCode.BadRequest, "form_immutable");
        Assert.Equal("Inspected", Assert.Single((await fixture.Read<FormSubmission>(director.TenantId, submission.Id)).Fields).Label);
    }

    [PostgresApiFact]
    public async Task Training_submission_requires_the_apprentice_and_review_requires_the_assigned_mentor()
    {
        var (director, manager) = await Session();
        var apprenticeUser = await fixture.CreateUser(director.TenantId, BusinessRole.Worker); var mentorUser = await fixture.CreateUser(director.TenantId, BusinessRole.Trainer);
        var apprenticeClient = await fixture.Login(apprenticeUser); var mentorClient = await fixture.Login(mentorUser);
        var mentor = await Create<Employee>(manager, "employees", new() { Name = "Assigned mentor", UserId = mentorUser.UserId });
        var apprentice = await Create<Employee>(manager, "employees", new() { Name = "Apprentice", UserId = apprenticeUser.UserId, IsApprentice = true, MentorEmployeeId = mentor.Id });
        var record = await Create<TrainingRecord>(apprenticeClient, "training-records", new() { ApprenticeEmployeeId = apprentice.Id, MentorEmployeeId = mentor.Id,
            WeekStart = new DateOnly(2026, 10, 19), Activities = "Roof measurement practice", LearningGoals = "Record measurements clearly" });
        record = await Ok<TrainingRecord>(Send(apprenticeClient, HttpMethod.Post, $"/api/v1/training-records/{record.Id}/submit", new { }, record.Revision));
        await Error(Send(apprenticeClient, HttpMethod.Post, $"/api/v1/training-records/{record.Id}/review", new { mentorFeedback = "Self approval attempt" }, record.Revision), HttpStatusCode.Forbidden, "forbidden");
        record = await Ok<TrainingRecord>(Send(mentorClient, HttpMethod.Post, $"/api/v1/training-records/{record.Id}/review", new { mentorFeedback = "Measurements are reproducible; continue checking units." }, record.Revision));
        Assert.Equal(TrainingRecordStatus.Reviewed, record.Status); Assert.NotNull(record.ReviewedAt);
        record.Activities = "Changed after review";
        await Error(Send(mentorClient, HttpMethod.Put, $"/api/v1/training-records/{record.Id}", record, record.Revision), HttpStatusCode.BadRequest, "training_immutable");
    }

    [PostgresApiFact]
    public async Task Maintenance_completion_advances_the_month_anchor_once_and_never_reuses_completed_project_evidence()
    {
        var (person, client) = await Session(); var graph = await ProjectGraph(client, ProjectType.Maintenance, ProjectStatus.Completed);
        var due = new DateOnly(2026, 1, 31);
        var contract = await Create<MaintenanceContract>(client, "maintenance-contracts", new() { CustomerId = graph.Customer.Id, BuildingId = graph.Building.Id,
            Title = "Monthly inspection", Scope = "Synthetic inspection scope", StartDate = due, NextDueDate = due, IntervalMonths = 1 });
        var key = Guid.NewGuid().ToString("N"); var body = new { expectedDueDate = due, projectId = graph.Project.Id };
        var completed = await Ok<JsonElement>(Send(client, HttpMethod.Post, $"/api/v1/maintenance-contracts/{contract.Id}/complete", body, contract.Revision, key));
        var replay = await Send(client, HttpMethod.Post, $"/api/v1/maintenance-contracts/{contract.Id}/complete", body, contract.Revision, key);
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal("2026-02-28", completed.GetProperty("contract").GetProperty("nextDueDate").GetString());
        contract = await fixture.Read<MaintenanceContract>(person.TenantId, contract.Id); Assert.Equal(1, contract.CompletedOccurrences);
        await Error(Send(client, HttpMethod.Post, $"/api/v1/maintenance-contracts/{contract.Id}/complete", new
        { expectedDueDate = contract.NextDueDate, projectId = graph.Project.Id }, contract.Revision), HttpStatusCode.BadRequest, "maintenance_project_reused");
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            Assert.Equal(1, await db.Receipts.IgnoreQueryFilters().CountAsync(x => x.TenantId == person.TenantId && x.ActorId == Guid.Empty && x.Key.StartsWith("maintenance-completion:")));
        });
    }

    private async Task<(ApiTestUser Person, HttpClient Client)> Session()
    {
        var tenant = await fixture.CreateTenant(); var person = await fixture.CreateUser(tenant.Id); return (person, await fixture.Login(person));
    }
    private sealed record Graph(Customer Customer, Building Building, Project Project);
    private static async Task<Graph> ProjectGraph(HttpClient client, ProjectType type = ProjectType.Renovation, ProjectStatus status = ProjectStatus.Active)
    {
        var customer = await Create<Customer>(client, "customers", new() { Name = "Synthetic customer", Address = "Customer test address" });
        var building = await Create<Building>(client, "buildings", new() { CustomerId = customer.Id, Name = "Synthetic building", Address = customer.Address });
        var project = await Create<Project>(client, "projects", new() { CustomerId = customer.Id, BuildingId = building.Id, Name = "Synthetic roof work", Type = type, Status = status });
        return new(customer, building, project);
    }
    private static Task<Quote> DraftQuote(HttpClient client, Graph graph) => Create<Quote>(client, "quotes", new()
    {
        CustomerId = graph.Customer.Id, ProjectId = graph.Project.Id, Title = "Synthetic roof work",
        Lines = [new() { Description = "Contracted position", Quantity = 1m, UnitMaterialCost = 100m, VatPercent = 19m }]
    });
    private static async Task<Quote> AcceptedQuote(HttpClient client, Graph graph)
    {
        var quote = await DraftQuote(client, graph);
        quote = await Ok<Quote>(Send(client, HttpMethod.Post, $"/api/v1/quotes/{quote.Id}/freeze", new { }, quote.Revision));
        return await Ok<Quote>(Send(client, HttpMethod.Post, $"/api/v1/quotes/{quote.Id}/accept", new { digest = quote.Snapshot!.Digest }, quote.Revision));
    }
    private static async Task<Invoice> InvoiceFor(HttpClient client, Graph graph)
    {
        var quote = await AcceptedQuote(client, graph);
        var order = await Ok<Order>(Send(client, HttpMethod.Post, $"/api/v1/orders/from-quote/{quote.Id}", new { }, quote.Revision));
        return await IssueInvoice(client, order);
    }
    private static async Task<Invoice> IssueInvoice(HttpClient client, Order order)
    {
        var invoice = await Ok<Invoice>(Send(client, HttpMethod.Post, $"/api/v1/invoices/from-order/{order.Id}", new { }, order.Revision));
        invoice.SellerAddress = "Business test address";
        invoice = await Ok<Invoice>(Send(client, HttpMethod.Put, $"/api/v1/invoices/{invoice.Id}", invoice, invoice.Revision));
        return await Ok<Invoice>(Send(client, HttpMethod.Post, $"/api/v1/invoices/{invoice.Id}/issue", new { }, invoice.Revision));
    }
    private static async Task<BankTransaction> Import(HttpClient client, decimal amount)
    {
        var imported = await Ok<JsonElement>(Send(client, HttpMethod.Post, "/api/v1/bank-transactions/import", new
        { accountReference = "synthetic-readonly-bank", transactions = new[] { new { externalId = Guid.NewGuid().ToString("N"), bookedOn = DateOnly.FromDateTime(DateTime.UtcNow), amount } } }));
        return AppJson.Read<BankTransaction>(imported.GetProperty("items")[0].GetRawText());
    }
    private static Task<T> Create<T>(HttpClient client, string kind, T body) => Ok<T>(Send(client, HttpMethod.Post, "/api/v1/" + kind, body));
    private static async Task AssertOpenCounts(HttpClient client, int expected)
    {
        var dashboard = await Ok<JsonElement>(client.GetAsync("/api/v1/dashboard"));
        var linkedList = await Ok<JsonElement>(client.GetAsync("/api/v1/invoices?openOnly=true"));
        Assert.Equal(expected, dashboard.GetProperty("openInvoices").GetInt32());
        Assert.Equal(expected, linkedList.GetProperty("total").GetInt32());
        Assert.Equal(expected, linkedList.GetProperty("items").GetArrayLength());
    }
    private static async Task<T> Ok<T>(Task<HttpResponseMessage> pending)
    {
        using var response = await pending; var json = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {json}"); return AppJson.Read<T>(json);
    }
    private static async Task Error(Task<HttpResponseMessage> pending, HttpStatusCode expected, string code)
    {
        using var response = await pending; var json = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected HTTP {(int)expected}, received {(int)response.StatusCode}: {json}");
        using var document = JsonDocument.Parse(json); Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
    }
}
