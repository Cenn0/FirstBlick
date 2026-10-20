using FirstBlick.Core.Business;

namespace FirstBlick.Core.Tests.Business;

internal static class BusinessFixture
{
    internal static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    internal static readonly Guid Actor = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    internal static readonly DateTimeOffset Now = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);
    internal static Quote Quote(decimal unitNet = 100m, decimal quantity = 1m, decimal vat = 19m) => new()
    {
        TenantId = Tenant, CustomerId = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Title = "Roof renovation",
        Lines = [new() { Description = "Roofing work", Quantity = quantity, UnitMaterialCost = unitNet, VatPercent = vat }]
    };

    internal static Order Order(decimal unitNet = 100m, decimal quantity = 1m, decimal vat = 19m)
    {
        var quote = Quote(unitNet, quantity, vat); CommercialWorkflow.IssueQuote(quote, "Q-2026-1");
        CommercialWorkflow.AcceptQuote(quote, quote.Snapshot!.Digest, Now, new(2026, 10, 20));
        return CommercialWorkflow.CreateOrder(quote, "O-2026-1");
    }

    internal static Invoice Draft(Order? order = null, InvoiceKind kind = InvoiceKind.Final)
    {
        order ??= Order();
        var customer = new Customer { Id = order.CustomerId, TenantId = Tenant, Name = "Example Customer", Address = "Example Road 1" };
        var invoice = InvoiceWorkflow.DraftFromOrder(order, customer, kind);
        invoice.SellerName = "Example Contractor"; invoice.SellerAddress = "Workshop Road 2";
        invoice.ServiceFrom = new(2026, 10, 1); invoice.ServiceUntil = new(2026, 10, 20); invoice.DueDate = new(2026, 11, 3);
        return invoice;
    }

    internal static Invoice Invoice(decimal amount = 100m, decimal vat = 19m)
    {
        var order = Order(amount, vat: vat); var invoice = Draft(order);
        InvoiceWorkflow.IssueForOrder(invoice, order, [], [], "I-2026-1", new(2026, 10, 20)); return invoice;
    }

    internal static BankTransaction Bank(decimal amount, string externalId = "bank-1")
    {
        var bank = new BankTransaction
        {
            TenantId = Tenant, AccountReference = "import-account-1", ExternalId = externalId, Counterparty = "Example Customer",
            Reference = "I-2026-1", Amount = amount, BookedOn = new(2026, 10, 20)
        };
        PaymentWorkflow.SealImport(bank); return bank;
    }

    internal static ScheduleAssignment Assignment(Guid? employee = null, int startHour = 8, int endHour = 12) => new()
    {
        TenantId = Tenant, EmployeeId = employee ?? Guid.NewGuid(), ProjectId = Guid.NewGuid(), Title = "Roof work",
        Start = Now.AddHours(startHour - 8), End = Now.AddHours(endHour - 8)
    };
}
