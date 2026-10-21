using System.Text.Json.Serialization;

namespace FirstBlick.Core.Business;

public sealed record QuoteLine
{
    public Guid? CatalogItemId { get; set; }
    public string Description { get; set; } = "";
    public string Unit { get; set; } = "m²";
    public decimal Quantity { get; set; } = 1m;
    public decimal UnitMaterialCost { get; set; }
    public decimal UnitLaborHours { get; set; }
    public decimal HourlyRate { get; set; }
    public decimal MarkupPercent { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal VatPercent { get; set; }
    // Omitting absent evidence preserves the digests of all pre-existing frozen quotes.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QuantityEvidence? QuantityEvidence { get; set; }

    public void Validate() => Validate(allowPartialMeasurement: false);

    public void Validate(bool allowPartialMeasurement)
    {
        Guard.OptionalId(CatalogItemId, nameof(CatalogItemId)); Guard.Text(Description, nameof(Description), 4000); Guard.Text(Unit, nameof(Unit), 32);
        Guard.Range(Quantity, 0.000001m, 1_000_000m, nameof(Quantity));
        Guard.Range(UnitMaterialCost, 0, 1_000_000m, nameof(UnitMaterialCost));
        Guard.Range(UnitLaborHours, 0, 10_000m, nameof(UnitLaborHours)); Guard.Range(HourlyRate, 0, 100_000m, nameof(HourlyRate));
        Guard.Range(MarkupPercent, 0, 1000, nameof(MarkupPercent)); Guard.Range(DiscountPercent, 0, 100, nameof(DiscountPercent));
        Guard.Range(VatPercent, 0, 100, nameof(VatPercent));
        QuantityLinks.EnsureTarget(QuantityEvidence, Quantity, Unit, allowPartialMeasurement);
    }
}

public sealed record PricedLine(QuoteLine Inputs, decimal UnitCost, decimal UnitNet, decimal Net, decimal Tax, decimal Gross);
public sealed record QuoteSnapshot(int Version, string Currency, IReadOnlyList<PricedLine> Lines, decimal Net, decimal Tax, decimal Gross, string Digest)
{
    public void Validate() => Validate(allowPartialMeasurement: false);

    public void Validate(bool allowPartialMeasurement)
    {
        Guard.Require(Version > 0, "invalid_version", "Snapshot version must be positive."); Guard.Currency(Currency);
        Guard.Items(Lines, nameof(Lines), 1000, 1);
        var recalculated = Pricing.Freeze(Lines.Select(x => x.Inputs).ToList(), Version, Currency, allowPartialMeasurement);
        Guard.Require(recalculated.Digest == Digest && recalculated.Net == Net && recalculated.Tax == Tax && recalculated.Gross == Gross,
            "snapshot_changed", "The calculation snapshot does not match its frozen input values.");
        Guard.Require(recalculated.Lines.SequenceEqual(Lines), "snapshot_changed", "A frozen line was modified.");
    }
}

/// <summary>EUR commercial arithmetic: round effective unit price to cents, then line net, then line tax. Tax rates are explicit inputs, never inferred.</summary>
public static class Pricing
{
    public static QuoteSnapshot Freeze(IReadOnlyList<QuoteLine> lines, int version = 1, string currency = "EUR", bool allowPartialMeasurement = false)
    {
        Guard.Items(lines, nameof(lines), 1000, 1); Guard.Currency(currency);
        Guard.Require(version > 0, "invalid_version", "Calculation version must be positive.");
        var priced = lines.Select(x => Price(x, allowPartialMeasurement)).ToList().AsReadOnly();
        var net = priced.Sum(x => x.Net); var tax = priced.Sum(x => x.Tax); var gross = net + tax;
        Money.Amount(net, "Net"); Money.Amount(tax, "Tax"); Money.Amount(gross, "Gross");
        var digest = ContentDigest.Of(new { Version = version, Currency = currency, Lines = priced, Net = net, Tax = tax, Gross = gross });
        return new(version, currency, priced, net, tax, gross, digest);
    }

    public static PricedLine Price(QuoteLine input, bool allowPartialMeasurement = false)
    {
        Guard.Require(input is not null, "required", "Calculation line is required."); input!.Validate(allowPartialMeasurement);
        var frozen = input with { };
        var cost = input.UnitMaterialCost + input.UnitLaborHours * input.HourlyRate;
        var unit = Money.Round(cost * (1m + input.MarkupPercent / 100m) * (1m - input.DiscountPercent / 100m));
        var net = Money.Round(unit * input.Quantity); var tax = Money.Round(net * input.VatPercent / 100m);
        Money.Amount(net, "Line net"); Money.Amount(tax, "Line tax"); Money.Amount(net + tax, "Line gross");
        return new(frozen, cost, unit, net, tax, net + tax);
    }
}

public enum QuoteStatus { Draft, Issued, Accepted, Rejected, Cancelled }
public enum DeliveryState { Queued, Processing, Sent, Failed, Uncertain }
public enum CommunicationChannel { Email, Push, WhatsApp }
public sealed record QuotePresentationSnapshot(Guid QuoteId, Guid CustomerId, string SellerName, string BuyerName, string BuyerAddress,
    string Number, string Title, DateOnly? ValidUntil, DateTimeOffset IssuedAt, string PricingDigest, string RendererContractVersion, string Digest);
public sealed record DeliveryAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public CommunicationChannel Channel { get; set; }
    public string Recipient { get; set; } = "";
    public DeliveryState State { get; set; }
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAt { get; set; }
    public string ProviderReference { get; set; } = "";
    public string ResolutionNote { get; set; } = "";
}

public sealed class Quote : TenantEntity
{
    public Guid CustomerId { get; set; }
    public Guid ProjectId { get; set; }
    public string Number { get; set; } = "";
    public string Title { get; set; } = "";
    public string Currency { get; set; } = "EUR";
    public int Version { get; set; } = 1;
    public QuoteStatus Status { get; set; }
    public List<QuoteLine> Lines { get; set; } = [];
    public QuoteSnapshot? Snapshot { get; set; }
    public QuotePresentationSnapshot? Presentation { get; set; }
    public string IssuedDigest => Presentation?.Digest ?? "";
    public DateOnly? ValidUntil { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public List<DeliveryAttempt> Deliveries { get; set; } = [];
    public override void Validate()
    {
        ValidateCommon(); Guard.Id(CustomerId, nameof(CustomerId)); Guard.Id(ProjectId, nameof(ProjectId));
        Guard.Text(Number, nameof(Number), 80, Status != QuoteStatus.Draft); Guard.Text(Title, nameof(Title)); Guard.Currency(Currency);
        Guard.Require(Version > 0 && Enum.IsDefined(Status), "invalid_status", "Quote version or status is invalid.");
        Guard.Items(Lines, nameof(Lines)); foreach (var line in Lines) line.Validate();
        Guard.Items(Deliveries, nameof(Deliveries));
        Guard.Require(Deliveries.Select(x => x.Id).Distinct().Count() == Deliveries.Count, "duplicate_delivery", "Delivery attempts must have unique identifiers.");
        foreach (var d in Deliveries)
        {
            Guard.Id(d.Id, "Delivery Id"); Guard.Text(d.Recipient, "Recipient", 254); Guard.Utc(d.RequestedAt, "RequestedAt");
            Guard.Require(Enum.IsDefined(d.State) && Enum.IsDefined(d.Channel), "invalid_delivery", "Delivery state or channel is invalid.");
            Guard.Text(d.ProviderReference, "Provider reference", 512, false); Guard.Text(d.ResolutionNote, "Resolution note", 2000, false);
            if (d.ResolvedAt is { } resolved) Guard.Utc(resolved, "ResolvedAt");
        }
        if (Status != QuoteStatus.Draft)
        {
            Guard.Require(Snapshot is not null, "missing_snapshot", "An issued quote requires a frozen calculation."); Snapshot!.Validate();
            Guard.Require(Pricing.Freeze(Lines, Version, Currency).Digest == Snapshot.Digest, "snapshot_changed", "Issued quote values are immutable; create a new draft version.");
        }
        if (Presentation is { } presentation)
        {
            Guard.Text(presentation.SellerName, "Seller name"); Guard.Text(presentation.BuyerName, "Buyer name"); Guard.Text(presentation.BuyerAddress, "Buyer address", 2000, false);
            Guard.Utc(presentation.IssuedAt, "Issue time");
            Guard.Require(Status != QuoteStatus.Draft && presentation.QuoteId == Id && presentation.CustomerId == CustomerId && presentation.Number == Number && presentation.Title == Title
                && presentation.ValidUntil == ValidUntil && presentation.PricingDigest == Snapshot?.Digest && presentation.RendererContractVersion == "1.0.0"
                && presentation.Digest == ContentDigest.Of(presentation with { Digest = "" }), "quote_presentation_changed", "The frozen quote presentation has changed.");
        }
        if (Status == QuoteStatus.Accepted)
        {
            Guard.Require(AcceptedAt.HasValue, "required", "AcceptedAt is required for accepted quotes."); Guard.Utc(AcceptedAt!.Value, "AcceptedAt");
        }
    }
    public override IEnumerable<EntityReference> References()
    {
        yield return BusinessRegistry.Reference<Customer>(CustomerId); yield return BusinessRegistry.Reference<Project>(ProjectId);
        foreach (var id in Lines.Where(x => x.CatalogItemId.HasValue).Select(x => x.CatalogItemId!.Value).Distinct())
            yield return BusinessRegistry.Reference<CatalogItem>(id);
        foreach (var id in Lines.Where(x => x.QuantityEvidence is not null).Select(x => x.QuantityEvidence!.MeasurementId).Distinct())
            yield return BusinessRegistry.Reference<Measurement>(id);
    }
}

public enum OrderStatus { Open, InProgress, Completed, Cancelled }
public sealed class Order : TenantEntity
{
    public Guid QuoteId { get; set; }
    public Guid CustomerId { get; set; }
    public Guid ProjectId { get; set; }
    public string Number { get; set; } = "";
    public string Title { get; set; } = "";
    public OrderStatus Status { get; set; }
    public QuoteSnapshot? Contract { get; set; }
    public QuotePresentationSnapshot? QuotePresentation { get; set; }
    public DateTimeOffset AcceptedAt { get; set; }
    public override void Validate()
    {
        ValidateCommon(); Guard.Id(QuoteId, nameof(QuoteId)); Guard.Id(CustomerId, nameof(CustomerId)); Guard.Id(ProjectId, nameof(ProjectId));
        Guard.Text(Number, nameof(Number), 80); Guard.Text(Title, nameof(Title)); Guard.Utc(AcceptedAt, nameof(AcceptedAt));
        Guard.Require(Enum.IsDefined(Status), "invalid_status", "Order status is invalid.");
        Guard.Require(Contract is not null, "missing_snapshot", "Order requires its accepted quote snapshot."); Contract!.Validate();
        if (QuotePresentation is { } presentation)
            Guard.Require(presentation.QuoteId == QuoteId && presentation.CustomerId == CustomerId && presentation.Title == Title
                && presentation.PricingDigest == Contract.Digest && presentation.RendererContractVersion == "1.0.0"
                && presentation.Digest == ContentDigest.Of(presentation with { Digest = "" }),
                "order_presentation_changed", "The order must preserve its accepted quote's complete presentation.");
    }
    public override IEnumerable<EntityReference> References() => [BusinessRegistry.Reference<Quote>(QuoteId), BusinessRegistry.Reference<Customer>(CustomerId), BusinessRegistry.Reference<Project>(ProjectId)];
}

public enum ChangeOrderStatus { Draft, Accepted, Rejected }
public sealed class ChangeOrder : TenantEntity
{
    public Guid OrderId { get; set; }
    public string Title { get; set; } = "";
    public string Reason { get; set; } = "";
    public ChangeOrderStatus Status { get; set; }
    public List<QuoteLine> Lines { get; set; } = [];
    public QuoteSnapshot? Snapshot { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public override void Validate()
    {
        ValidateCommon(); Guard.Id(OrderId, nameof(OrderId)); Guard.Text(Title, nameof(Title)); Guard.Text(Reason, nameof(Reason), 4000);
        Guard.Require(Enum.IsDefined(Status), "invalid_status", "Change order status is invalid."); Guard.Items(Lines, nameof(Lines), 1000, 1);
        foreach (var line in Lines) line.Validate();
        if (Status == ChangeOrderStatus.Accepted)
        {
            Guard.Require(Snapshot is not null && AcceptedAt.HasValue, "missing_snapshot", "Accepted change requires a timestamp and snapshot.");
            Snapshot!.Validate(); Guard.Utc(AcceptedAt!.Value, nameof(AcceptedAt));
            Guard.Require(Pricing.Freeze(Lines).Digest == Snapshot.Digest, "snapshot_changed", "Accepted change values are immutable.");
        }
    }
    public override IEnumerable<EntityReference> References()
    {
        yield return BusinessRegistry.Reference<Order>(OrderId);
        foreach (var id in Lines.Where(x => x.QuantityEvidence is not null).Select(x => x.QuantityEvidence!.MeasurementId).Distinct())
            yield return BusinessRegistry.Reference<Measurement>(id);
        foreach (var id in Lines.Where(x => x.CatalogItemId.HasValue).Select(x => x.CatalogItemId!.Value).Distinct())
            yield return BusinessRegistry.Reference<CatalogItem>(id);
    }
}

public static class CommercialWorkflow
{
    public static void IssueQuote(Quote quote, string number)
    {
        quote.Validate(); Guard.Require(quote.Status == QuoteStatus.Draft, "invalid_transition", "Only a draft quote can be issued.");
        Guard.Text(number, "Number", 80); var snapshot = Pricing.Freeze(quote.Lines, quote.Version, quote.Currency);
        quote.Number = number; quote.Snapshot = snapshot; quote.Status = QuoteStatus.Issued;
    }

    public static void FreezePresentation(Quote quote, string sellerName, Customer customer, DateTimeOffset now)
    {
        quote.Validate(); customer.Validate(); Guard.Utc(now, nameof(now)); Guard.Text(sellerName, nameof(sellerName));
        Guard.Require(quote.Status == QuoteStatus.Issued && quote.Presentation is null && quote.Snapshot is not null && customer.TenantId == quote.TenantId && customer.Id == quote.CustomerId,
            "presentation_mismatch", "Freeze presentation once from the issued quote and its current business/customer context.");
        var presentation = new QuotePresentationSnapshot(quote.Id, quote.CustomerId, sellerName, customer.Name, customer.Address, quote.Number, quote.Title, quote.ValidUntil,
            now, quote.Snapshot.Digest, "1.0.0", "");
        quote.Presentation = presentation with { Digest = ContentDigest.Of(presentation) }; quote.Validate();
    }

    public static void AcceptQuote(Quote quote, string expectedDigest, DateTimeOffset acceptedAt, DateOnly businessDate)
    {
        quote.Validate(); Guard.Utc(acceptedAt, nameof(acceptedAt));
        Guard.Require(quote.Status == QuoteStatus.Issued, "invalid_transition", "Only an issued quote can be accepted.");
        Guard.Require((quote.Presentation?.Digest ?? quote.Snapshot!.Digest) == expectedDigest, "stale_content", "The accepted content differs from the current issued version.");
        Guard.Require(!quote.ValidUntil.HasValue || businessDate <= quote.ValidUntil.Value, "quote_expired", "The quote validity date has passed.");
        quote.Status = QuoteStatus.Accepted; quote.AcceptedAt = acceptedAt;
    }

    public static void RejectQuote(Quote quote)
    {
        quote.Validate(); Guard.Require(quote.Status == QuoteStatus.Issued, "invalid_transition", "Only an issued quote can be rejected.");
        quote.Status = QuoteStatus.Rejected;
    }

    public static Order CreateOrder(Quote quote, string number)
    {
        quote.Validate(); Guard.Text(number, "Number", 80);
        Guard.Require(quote.Status == QuoteStatus.Accepted, "quote_not_accepted", "An order requires an accepted quote version.");
        return new()
        {
            TenantId = quote.TenantId, QuoteId = quote.Id, CustomerId = quote.CustomerId, ProjectId = quote.ProjectId,
            Number = number, Title = quote.Title, Contract = CloneSnapshot(quote.Snapshot!), QuotePresentation = quote.Presentation is null ? null : quote.Presentation with { }, AcceptedAt = quote.AcceptedAt!.Value
        };
    }

    public static DeliveryAttempt RequestDelivery(Quote quote, CommunicationChannel channel, string recipient, DateTimeOffset now)
    {
        quote.Validate(); Guard.Utc(now, nameof(now)); Guard.Text(recipient, nameof(recipient), 254);
        Guard.Require(Enum.IsDefined(channel), "invalid_channel", "Communication channel is invalid.");
        Guard.Require(quote.Status is QuoteStatus.Issued or QuoteStatus.Accepted, "invalid_transition", "Only issued or accepted quotes can be sent.");
        Guard.Require(!quote.Deliveries.Any(x => x.Recipient == recipient && x.Channel == channel && x.State is DeliveryState.Queued or DeliveryState.Processing or DeliveryState.Uncertain),
            "unresolved_delivery", "Resolve the existing pending or uncertain delivery before resending.");
        Guard.Require(quote.Deliveries.Count < 1000, "delivery_budget", "Delivery history budget is exhausted.");
        var attempt = new DeliveryAttempt { Channel = channel, Recipient = recipient, RequestedAt = now };
        quote.Deliveries.Add(attempt); return attempt;
    }

    public static void BeginDelivery(DeliveryAttempt attempt)
    {
        Guard.Require(attempt.State == DeliveryState.Queued, "invalid_transition", "Only queued deliveries can start."); attempt.State = DeliveryState.Processing;
    }

    public static void MarkDeliveryUncertain(DeliveryAttempt attempt)
    {
        Guard.Require(attempt.State == DeliveryState.Processing, "invalid_transition", "Only an in-flight delivery can become uncertain."); attempt.State = DeliveryState.Uncertain;
    }

    public static void ResolveDelivery(DeliveryAttempt attempt, bool delivered, string evidence, string providerReference, DateTimeOffset now)
    {
        Guard.Require(attempt.State is DeliveryState.Processing or DeliveryState.Uncertain, "invalid_transition", "Only an in-flight or uncertain delivery can be resolved.");
        Guard.Text(evidence, nameof(evidence), 2000); Guard.Text(providerReference, nameof(providerReference), 512, false); Guard.Utc(now, nameof(now));
        Guard.Require(now >= attempt.RequestedAt, "invalid_time", "Resolution cannot precede the request.");
        attempt.State = delivered ? DeliveryState.Sent : DeliveryState.Failed; attempt.ResolutionNote = evidence;
        attempt.ProviderReference = providerReference; attempt.ResolvedAt = now;
    }

    public static void AcceptChange(ChangeOrder change, DateTimeOffset now)
    {
        change.Validate(); Guard.Utc(now, nameof(now)); Guard.Require(change.Status == ChangeOrderStatus.Draft, "invalid_transition", "Only a draft change can be accepted.");
        var snapshot = Pricing.Freeze(change.Lines); change.Snapshot = snapshot; change.AcceptedAt = now; change.Status = ChangeOrderStatus.Accepted;
    }

    public static QuoteSnapshot CloneSnapshot(QuoteSnapshot snapshot) => Pricing.Freeze(snapshot.Lines.Select(x => x.Inputs with { }).ToList(), snapshot.Version, snapshot.Currency);

    public static CostComparison CompareCosts(Order order, IEnumerable<ChangeOrder> changes, decimal actualCost, decimal remainingForecastCost)
    {
        order.Validate(); Money.Amount(actualCost, nameof(actualCost)); Money.Amount(remainingForecastCost, nameof(remainingForecastCost));
        var accepted = changes.Where(x => x.OrderId == order.Id && x.TenantId == order.TenantId && x.Status == ChangeOrderStatus.Accepted).ToList();
        foreach (var change in accepted) change.Validate();
        var revenue = order.Contract!.Net + accepted.Sum(x => x.Snapshot!.Net);
        var plannedCost = order.Contract.Lines.Sum(x => Money.Round(x.Inputs.Quantity * x.UnitCost)) + accepted.Sum(x => x.Snapshot!.Lines.Sum(y => Money.Round(y.Inputs.Quantity * y.UnitCost)));
        var forecast = actualCost + remainingForecastCost;
        return new(revenue, plannedCost, actualCost, forecast, revenue - forecast);
    }
}

public sealed record CostComparison(decimal ContractNet, decimal PlannedCost, decimal ActualCost, decimal ForecastCost, decimal ForecastMargin);
