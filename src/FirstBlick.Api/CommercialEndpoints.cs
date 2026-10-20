using FirstBlick.Core.Business;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FirstBlick.Api;

public sealed record EmptyBusinessCommand;
public sealed record AcceptQuoteRequest(string Digest);
public sealed record QuoteDeliveryRequest(CommunicationChannel Channel, string Recipient);
public sealed record CorrectionRequest(string Reason);
public sealed record PaymentAllocationRequest(Guid InvoiceId, Guid BankTransactionId, decimal Amount, decimal DiscountAmount = 0, decimal RetainedAmount = 0, string Reason = "");
public sealed record RefundRequest(Guid InvoiceId, Guid BankTransactionId, decimal Amount, string Reason);
public sealed record ReversePaymentRequest(string Reason, Guid? ReturnBankTransactionId = null);
public sealed record ReleaseRetentionRequest(decimal Amount, string Reason);
public sealed record BankImportLine(string ExternalId, DateOnly BookedOn, decimal Amount, string Counterparty = "", string Reference = "", string Currency = "EUR");
public sealed record BankImportRequest(string AccountReference, List<BankImportLine> Transactions);
public sealed record SupplierPaymentRequest(Guid SupplierInvoiceId, Guid BankTransactionId, decimal Amount,
    decimal DiscountAmount = 0, string Reason = "", SupplierPaymentDirection Direction = SupplierPaymentDirection.Payment);

/// <summary>Shared scoped services for explicit business commands; the endpoint binder resolves each service from DI.</summary>
public sealed class BusinessCommandServices
{
    public HttpContext Http { get; set; } = null!;
    [FromServices] public EntityStore Store { get; set; } = null!;
    [FromServices] public TenantScope Scope { get; set; } = null!;
    [FromServices] public FirstBlickDbContext Db { get; set; } = null!;
    [FromServices] public CommandExecutor Commands { get; set; } = null!;
    [FromServices] public ApprovalService Approvals { get; set; } = null!;
    public Task<IResult> Execute(string kind, string action, object? input, Func<CancellationToken, Task<object>> mutation) => Commands.Execute(Http, kind, action, input, mutation);
    public async Task<T> Current<T>(Guid id, CancellationToken ct, bool matchRevision = true) where T : TenantEntity
    {
        await RecordPolicy.EnsureVisible(BusinessRegistry.KindOf<T>(), id, Scope, Store, ct);
        var entity = await Store.Get<T>(id, ct);
        Guard.Require(!entity.Archived, "not_found", "The source record is archived.");
        if (matchRevision) CommandExecutor.Match(Http, entity.Revision);
        return entity;
    }
    public async Task<DateOnly> BusinessDate(CancellationToken ct)
    {
        var zone = await Db.Tenants.Where(x => x.Id == Scope.TenantId).Select(x => x.TimeZone).SingleAsync(ct);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(zone)).DateTime);
    }
    public async Task<T> SaveNew<T>(T entity, string action, CancellationToken ct) where T : TenantEntity
    {
        // Preserve the server-created identifier: financial digests already bind that identifier.
        entity.TenantId = Scope.TenantId; entity.Revision = 1; entity.CreatedAt = entity.UpdatedAt = DateTimeOffset.UtcNow; entity.Archived = false;
        await Store.Save(entity, action, true, ct); return entity;
    }
}

public static class CommercialEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        MapQuotes(api); MapInvoices(api); MapPayments(api); MapSupplierPayments(api);
    }

    private static void MapQuotes(RouteGroupBuilder api)
    {
        api.MapPost("/quotes/{id:guid}/freeze", (Guid id, EmptyBusinessCommand body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("quotes", "quote.issue", body, async ct =>
            {
                var quote = await c.Current<Quote>(id, ct);
                CommercialWorkflow.IssueQuote(quote, await c.Store.NextNumber("AN", await c.BusinessDate(ct), ct));
                await c.Store.Save(quote, "quote.issue", false, ct); return quote;
            })).Produces<Quote>();
        api.MapPost("/quotes/{id:guid}/accept", (Guid id, AcceptQuoteRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("quotes", "quote.accept", body, async ct =>
            {
                var quote = await c.Current<Quote>(id, ct);
                CommercialWorkflow.AcceptQuote(quote, body.Digest, DateTimeOffset.UtcNow, await c.BusinessDate(ct));
                await c.Store.Save(quote, "quote.accept", false, ct); return quote;
            })).Produces<Quote>();
        api.MapPost("/quotes/{id:guid}/reject", (Guid id, EmptyBusinessCommand body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("quotes", "quote.reject", body, async ct =>
            {
                var quote = await c.Current<Quote>(id, ct); CommercialWorkflow.RejectQuote(quote);
                await c.Store.Save(quote, "quote.reject", false, ct); return quote;
            })).Produces<Quote>();
        api.MapPost("/quotes/{id:guid}/new-version", (Guid id, EmptyBusinessCommand body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("quotes", "quote.new_version", body, async ct =>
            {
                var previous = await c.Current<Quote>(id, ct); previous.Validate();
                Guard.Require(previous.Status is QuoteStatus.Issued or QuoteStatus.Rejected, "invalid_transition", "Revise issued or rejected quotes. Accepted work requires a change order.");
                Guard.Require(previous.Version < int.MaxValue, "version_exhausted", "The quote version counter is exhausted.");
                var revisionAction = "quote.revision_from:" + previous.Id;
                Guard.Require(!await c.Db.Audit.AnyAsync(x => x.Kind == "quotes" && x.Action == revisionAction, ct), "duplicate", "A successor draft already exists for this quote version.");
                var draft = new Quote { TenantId = c.Scope.TenantId, CustomerId = previous.CustomerId, ProjectId = previous.ProjectId,
                    Title = previous.Title, Currency = previous.Currency, Version = checked(previous.Version + 1),
                    ValidUntil = previous.ValidUntil, Lines = previous.Lines.Select(x => x with { }).ToList() };
                await c.SaveNew(draft, revisionAction, ct); return new { quote = draft, previousQuoteId = previous.Id };
            }));
        api.MapPost("/quotes/{id:guid}/delivery", (Guid id, QuoteDeliveryRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("quotes", "quote.delivery.request", body, async ct =>
            {
                var quote = await c.Current<Quote>(id, ct);
                if (body.Channel == CommunicationChannel.Email) Guard.Email(body.Recipient, true);
                var delivery = CommercialWorkflow.RequestDelivery(quote, body.Channel, body.Recipient, DateTimeOffset.UtcNow);
                await c.Store.Save(quote, "quote.delivery.request", false, ct);
                c.Db.Jobs.Add(new OutboxJob { TenantId = c.Scope.TenantId, ActorId = c.Scope.UserId, Type = "quote.delivery", Kind = "quotes", EntityId = quote.Id,
                    EntityRevision = quote.Revision, Payload = AppJson.Serialize(new { deliveryId = delivery.Id, snapshotDigest = quote.Snapshot!.Digest, channel = body.Channel, recipient = body.Recipient }) });
                return quote;
            })).Produces<Quote>();
        api.MapPost("/orders/from-quote/{id:guid}", (Guid id, EmptyBusinessCommand body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("orders", "order.from_quote", body, async ct =>
            {
                var quote = await c.Current<Quote>(id, ct);
                var order = CommercialWorkflow.CreateOrder(quote, await c.Store.NextNumber("AU", await c.BusinessDate(ct), ct));
                return await c.SaveNew(order, "order.from_quote", ct);
            })).Produces<Order>();
        api.MapPost("/change-orders/{id:guid}/accept", (Guid id, EmptyBusinessCommand body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("change-orders", "change.accept", body, async ct =>
            {
                var change = await c.Current<ChangeOrder>(id, ct); var order = await c.Store.Get<Order>(change.OrderId, ct);
                Guard.Require(!order.Archived && order.Status != OrderStatus.Cancelled, "invalid_transition", "Changes require an active order.");
                CommercialWorkflow.AcceptChange(change, DateTimeOffset.UtcNow); await c.Store.Save(change, "change.accept", false, ct); return change;
            })).Produces<ChangeOrder>();
        api.MapGet("/orders/{id:guid}/cost-comparison", async (Guid id, decimal actualCost, decimal remainingForecastCost, [AsParameters] BusinessCommandServices c, CancellationToken ct) =>
        {
            await c.Commands.Authorize("orders", false, ct); var order = await c.Current<Order>(id, ct, false);
            return Results.Ok(CommercialWorkflow.CompareCosts(order, await c.Store.All<ChangeOrder>(ct), actualCost, remainingForecastCost));
        });
    }

    private static void MapInvoices(RouteGroupBuilder api)
    {
        api.MapPost("/invoices/from-order/{id:guid}", (Guid id, EmptyBusinessCommand body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("invoices", "invoice.from_order", body, async ct =>
            {
                var order = await c.Current<Order>(id, ct); var customer = await c.Store.Get<Customer>(order.CustomerId, ct);
                var invoice = InvoiceWorkflow.DraftFromOrder(order, customer); var day = await c.BusinessDate(ct);
                invoice.SellerName = await c.Db.Tenants.Where(x => x.Id == c.Scope.TenantId).Select(x => x.Name).SingleAsync(ct);
                invoice.ServiceFrom = day; invoice.ServiceUntil = day; invoice.DueDate = day.AddDays(14);
                return await c.SaveNew(invoice, "invoice.from_order", ct);
            })).Produces<Invoice>();
        api.MapPost("/invoices/{id:guid}/issue", (Guid id, EmptyBusinessCommand body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("invoices", "invoice.issue", body, async ct =>
            {
                var invoice = await c.Current<Invoice>(id, ct);
                await c.Approvals.Require(c.Http, "invoice.issue", "invoices", id, invoice.Revision, "{}", ct);
                var day = await c.BusinessDate(ct); var number = await c.Store.NextNumber(invoice.Kind == InvoiceKind.CreditNote ? "KO" : "RE", day, ct);
                var existing = await c.Store.All<Invoice>(ct);
                if (invoice.Kind == InvoiceKind.CreditNote)
                    InvoiceWorkflow.IssueCreditNote(invoice, await c.Store.Get<Invoice>(invoice.OriginalInvoiceId!.Value, ct), existing, number, day);
                else InvoiceWorkflow.IssueForOrder(invoice, await c.Store.Get<Order>(invoice.OrderId, ct), existing, await c.Store.All<ChangeOrder>(ct), number, day);
                await c.Store.Save(invoice, "invoice.issue", false, ct); return invoice;
            })).Produces<Invoice>();
        api.MapPost("/invoices/{id:guid}/correction", (Guid id, CorrectionRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("invoices", "invoice.correction_draft", body, async ct =>
            {
                var original = await c.Current<Invoice>(id, ct); var correction = InvoiceWorkflow.DraftCreditNote(original, body.Reason);
                correction.DueDate = await c.BusinessDate(ct); return await c.SaveNew(correction, "invoice.correction_draft", ct);
            })).Produces<Invoice>();
        api.MapGet("/invoices/{id:guid}/balance", async (Guid id, [AsParameters] BusinessCommandServices c, CancellationToken ct) =>
        {
            await c.Commands.Authorize("invoices", false, ct); var invoice = await c.Current<Invoice>(id, ct, false);
            return Results.Ok(PaymentWorkflow.Balance(invoice, await c.Store.All<PaymentAllocation>(ct), await c.Store.All<Invoice>(ct), await c.BusinessDate(ct)));
        });
    }

    private static void MapPayments(RouteGroupBuilder api)
    {
        api.MapPost("/bank-transactions/import", (BankImportRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("bank-transactions", "bank.readonly_import", body, async ct =>
            {
                Guard.Text(body.AccountReference, "AccountReference", 120); Guard.Items(body.Transactions, "Transactions", 500, 1);
                var existing = (await c.Store.All<BankTransaction>(ct)).Where(x => x.AccountReference == body.AccountReference).ToDictionary(x => x.ExternalId, StringComparer.Ordinal);
                var imported = new List<BankTransaction>(); var reused = 0;
                foreach (var line in body.Transactions)
                {
                    var bank = new BankTransaction { TenantId = c.Scope.TenantId, AccountReference = body.AccountReference, ExternalId = line.ExternalId,
                        BookedOn = line.BookedOn, Amount = line.Amount, Counterparty = line.Counterparty, Reference = line.Reference, Currency = line.Currency };
                    PaymentWorkflow.SealImport(bank);
                    if (existing.TryGetValue(bank.ExternalId, out var prior))
                    {
                        prior.Validate(); Guard.Require(prior.ImportDigest == bank.ImportDigest, "idempotency_conflict", "An imported external bank identifier has different contents. Reconcile the source instead of replacing it.");
                        imported.Add(prior); reused++; continue;
                    }
                    await c.SaveNew(bank, "bank.readonly_import", ct); existing.Add(bank.ExternalId, bank); imported.Add(bank);
                }
                return new { items = imported, created = imported.Count - reused, reused };
            }));
        api.MapGet("/bank-transactions/{id:guid}/balance", async (Guid id, [AsParameters] BusinessCommandServices c, CancellationToken ct) =>
        {
            await c.Commands.Authorize("bank-transactions", false, ct); var bank = await c.Current<BankTransaction>(id, ct, false); bank.Validate();
            var used = PaymentWorkflow.UsedBankAmount(bank, await c.Store.All<PaymentAllocation>(ct), await c.Store.All<SupplierPaymentAllocation>(ct));
            return Results.Ok(new { bank.Id, bank.Amount, bank.Currency, allocated = used, unallocated = Math.Abs(bank.Amount) - used, mode = "read-only" });
        });
        api.MapPost("/payments/allocate", (PaymentAllocationRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("payment-allocations", "payment.allocate", body, async ct =>
            {
                var bank = await c.Current<BankTransaction>(body.BankTransactionId, ct); var invoice = await c.Store.Get<Invoice>(body.InvoiceId, ct);
                var result = PaymentWorkflow.Allocate(invoice, bank, await c.Store.All<PaymentAllocation>(ct), await c.Store.All<Invoice>(ct),
                    body.Amount, body.DiscountAmount, body.RetainedAmount, body.Reason, c.Scope.UserId, DateTimeOffset.UtcNow, await c.Store.All<SupplierPaymentAllocation>(ct));
                return await c.SaveNew(result, "payment.allocate", ct);
            })).Produces<PaymentAllocation>();
        api.MapPost("/payments/refund", (RefundRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("payment-allocations", "payment.refund_reconcile", body, async ct =>
            {
                var bank = await c.Current<BankTransaction>(body.BankTransactionId, ct);
                var result = PaymentWorkflow.AllocateRefund(await c.Store.Get<Invoice>(body.InvoiceId, ct), bank, await c.Store.All<PaymentAllocation>(ct), await c.Store.All<Invoice>(ct),
                    body.Amount, body.Reason, c.Scope.UserId, DateTimeOffset.UtcNow, await c.Store.All<SupplierPaymentAllocation>(ct));
                return await c.SaveNew(result, "payment.refund_reconcile", ct);
            })).Produces<PaymentAllocation>();
        api.MapPost("/payments/{id:guid}/reverse", (Guid id, ReversePaymentRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("payment-allocations", "payment.reverse", body, async ct =>
            {
                var allocation = await c.Current<PaymentAllocation>(id, ct);
                if (body.ReturnBankTransactionId is { } bankId)
                    PaymentWorkflow.ReverseForReturn(allocation, await c.Store.Get<BankTransaction>(bankId, ct), await c.Store.All<PaymentAllocation>(ct), body.Reason,
                        c.Scope.UserId, DateTimeOffset.UtcNow, await c.Store.All<SupplierPaymentAllocation>(ct));
                else PaymentWorkflow.Reverse(allocation, body.Reason, c.Scope.UserId, DateTimeOffset.UtcNow);
                await c.Store.Save(allocation, "payment.reverse", false, ct); return allocation;
            })).Produces<PaymentAllocation>();
        api.MapPost("/payments/{id:guid}/release-retention", (Guid id, ReleaseRetentionRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("payment-allocations", "payment.release_retention", body, async ct =>
            {
                var allocation = await c.Current<PaymentAllocation>(id, ct);
                PaymentWorkflow.ReleaseRetention(allocation, body.Amount, body.Reason, c.Scope.UserId, DateTimeOffset.UtcNow);
                await c.Store.Save(allocation, "payment.release_retention", false, ct); return allocation;
            })).Produces<PaymentAllocation>();
    }

    private static void MapSupplierPayments(RouteGroupBuilder api)
    {
        api.MapPost("/supplier-invoices/{id:guid}/approve", (Guid id, EmptyBusinessCommand body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("supplier-invoices", "supplier-invoice.approve", body, async ct =>
            {
                var invoice = await c.Current<SupplierInvoice>(id, ct);
                await c.Approvals.Require(c.Http, "supplier-invoice.approve", "supplier-invoices", id, invoice.Revision, "{}", ct);
                var supplier = await c.Store.Get<Supplier>(invoice.SupplierId, ct);
                var creator = await c.Db.Audit.Where(x => x.Kind == "supplier-invoices" && x.EntityId == id).OrderBy(x => x.Id).Select(x => x.ActorId).FirstOrDefaultAsync(ct);
                if (invoice.Kind == SupplierInvoiceKind.CreditNote)
                    PayablesWorkflow.ApproveCredit(invoice, await c.Store.Get<SupplierInvoice>(invoice.OriginalInvoiceId!.Value, ct), supplier,
                        await c.Store.All<SupplierInvoice>(ct), creator, c.Scope.UserId, DateTimeOffset.UtcNow, false);
                else PayablesWorkflow.Approve(invoice, supplier, creator, c.Scope.UserId, DateTimeOffset.UtcNow, false);
                await c.Store.Save(invoice, "supplier-invoice.approve", false, ct); return invoice;
            })).Produces<SupplierInvoice>();
        api.MapPost("/supplier-invoices/{id:guid}/reject", (Guid id, CorrectionRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("supplier-invoices", "supplier-invoice.reject", body, async ct =>
            {
                var invoice = await c.Current<SupplierInvoice>(id, ct); Guard.Require(invoice.Status == SupplierInvoiceStatus.Draft, "invalid_transition", "Only a draft supplier invoice can be rejected.");
                Guard.Text(body.Reason, "Reason", 4000); invoice.Status = SupplierInvoiceStatus.Rejected; invoice.ReviewNote = body.Reason;
                await c.Store.Save(invoice, "supplier-invoice.reject", false, ct); return invoice;
            })).Produces<SupplierInvoice>();
        api.MapGet("/supplier-invoices/{id:guid}/balance", async (Guid id, [AsParameters] BusinessCommandServices c, CancellationToken ct) =>
        {
            await c.Commands.Authorize("supplier-invoices", false, ct);
            return Results.Ok(PayablesWorkflow.Balance(await c.Current<SupplierInvoice>(id, ct, false), await c.Store.All<SupplierPaymentAllocation>(ct),
                await c.Store.All<SupplierInvoice>(ct), await c.BusinessDate(ct)));
        });
        api.MapPost("/supplier-payments/allocate", (SupplierPaymentRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("supplier-payment-allocations", "supplier-payment.reconcile", body, async ct =>
            {
                var bank = await c.Current<BankTransaction>(body.BankTransactionId, ct);
                var result = PayablesWorkflow.Allocate(await c.Store.Get<SupplierInvoice>(body.SupplierInvoiceId, ct), bank, await c.Store.All<SupplierPaymentAllocation>(ct),
                    await c.Store.All<PaymentAllocation>(ct), await c.Store.All<SupplierInvoice>(ct), body.Amount, body.DiscountAmount, body.Reason, body.Direction, c.Scope.UserId, DateTimeOffset.UtcNow);
                return await c.SaveNew(result, "supplier-payment.reconcile", ct);
            })).Produces<SupplierPaymentAllocation>();
        api.MapPost("/supplier-payments/{id:guid}/reverse", (Guid id, ReversePaymentRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("supplier-payment-allocations", "supplier-payment.reverse", body, async ct =>
            {
                var allocation = await c.Current<SupplierPaymentAllocation>(id, ct);
                if (body.ReturnBankTransactionId is { } bankId)
                    PayablesWorkflow.ReverseForReturn(allocation, await c.Store.Get<BankTransaction>(bankId, ct), await c.Store.All<SupplierPaymentAllocation>(ct),
                        await c.Store.All<PaymentAllocation>(ct), body.Reason, c.Scope.UserId, DateTimeOffset.UtcNow);
                else PayablesWorkflow.Reverse(allocation, body.Reason, c.Scope.UserId, DateTimeOffset.UtcNow);
                await c.Store.Save(allocation, "supplier-payment.reverse", false, ct); return allocation;
            })).Produces<SupplierPaymentAllocation>();
    }
}
