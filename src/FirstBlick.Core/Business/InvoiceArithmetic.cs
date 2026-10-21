namespace FirstBlick.Core.Business;

public enum InvoiceCalculationBasis { Contract, OriginalInvoice }
public sealed record InvoiceCalculationSource(string Kind, Guid Id, string Label, string DocumentDigest, QuoteSnapshot Snapshot);
public sealed record InvoicePreviousDocument(Guid Id, string Number, InvoiceKind Kind, Guid? OriginalInvoiceId, string Digest);
public sealed record InvoicePreviousBalance(string LineKey, decimal Quantity, decimal Net, decimal Tax);
public sealed record InvoicePositionCalculation(int SourceIndex, int PositionIndex, decimal BeforeQuantity, decimal AfterQuantity,
    decimal BeforeNet, decimal AfterNet, decimal BeforeTax, decimal AfterTax);
public sealed record InvoiceCalculatedLine(int LineIndex, string LineKey, decimal BeforeQuantity, decimal AfterQuantity,
    decimal BeforeNet, decimal BeforeTax, decimal ExpectedBeforeNet, decimal ExpectedBeforeTax, decimal AfterNet, decimal AfterTax,
    IReadOnlyList<InvoicePositionCalculation> Positions);
public sealed record InvoiceCalculation(string MethodVersion, InvoiceCalculationBasis Basis, Guid BasisId,
    IReadOnlyList<InvoiceCalculationSource> Sources, IReadOnlyList<InvoicePreviousDocument> PreviousInvoices,
    IReadOnlyList<InvoicePreviousBalance> PreviousBalances, IReadOnlyList<InvoiceCalculatedLine> Lines, string Digest);

/// <summary>
/// Cumulative rounding within each frozen source position. An invoice is the new cumulative amount less
/// the actual previously issued amount. Corrections allocate the original's actual cents proportionally.
/// This is an explicit arithmetic contract, not a claim about a statutory invoice or tax profile.
/// </summary>
public static class InvoiceArithmetic
{
    public const string MethodVersion = "firstblick.invoice-cumulative/1.0.0";
    private const int MaximumSources = 1000;
    private const int MaximumSourcePositions = 10_000;
    private const int MaximumPriorDocuments = 10_000;
    private const int MaximumPriorPositions = 100_000;

    public static string LineKey(QuoteLine line) => ContentDigest.Of(line with { Quantity = 1m });

    internal static InvoiceCalculation ForOrder(Invoice invoice, Order order, IReadOnlyList<Invoice> issued, IReadOnlyList<ChangeOrder> changes)
    {
        Guard.Items(issued, nameof(issued), MaximumPriorDocuments);
        Guard.Items(changes, nameof(changes), MaximumSources - 1);
        Guard.Require(order.Contract!.Lines.Count + changes.Sum(x => x.Snapshot!.Lines.Count) <= MaximumSourcePositions
            && issued.Sum(x => x.Snapshot!.Lines.Count) <= MaximumPriorPositions,
            "invoice_evidence_budget", "The invoice source or history-position budget is exhausted.");
        Guard.Require(issued.Select(x => x.Id).Distinct().Count() == issued.Count, "duplicate_invoice", "Each preceding invoice may occur only once.");
        Guard.Require(changes.Select(x => x.Id).Distinct().Count() == changes.Count, "duplicate_change", "Each accepted change may occur only once.");
        var byId = issued.ToDictionary(x => x.Id);
        foreach (var prior in issued)
        {
            Guard.Require(prior.CustomerId == invoice.CustomerId && prior.Currency == invoice.Currency,
                "invoice_order_mismatch", "Prior documents must retain the order customer and currency.");
            if (prior.Kind == InvoiceKind.CreditNote)
                Guard.Require(byId.TryGetValue(prior.OriginalInvoiceId!.Value, out var original) && original.Kind != InvoiceKind.CreditNote,
                    "missing_original_invoice", "An order's billed balance must include the original of every correction.");
        }
        var sources = new List<InvoiceCalculationSource>
        {
            new("orders", order.Id, order.Number, order.Contract!.Digest, Clone(order.Contract))
        };
        sources.AddRange(changes.OrderBy(x => x.AcceptedAt).ThenBy(x => x.Id).Select(x =>
            new InvoiceCalculationSource("change-orders", x.Id, x.Title, x.Snapshot!.Digest, Clone(x.Snapshot))));
        return Complete(invoice, new(MethodVersion, InvoiceCalculationBasis.Contract, order.Id, sources.AsReadOnly(),
            PreviousDocuments(issued), PreviousBalances(issued, subtractCorrections: true), [], ""));
    }

    internal static InvoiceCalculation ForCorrection(Invoice invoice, Invoice original, IReadOnlyList<Invoice> corrections)
    {
        Guard.Items(corrections, nameof(corrections), MaximumPriorDocuments);
        Guard.Require(corrections.Sum(x => x.Snapshot!.Lines.Count) <= MaximumPriorPositions,
            "invoice_evidence_budget", "The correction history-position budget is exhausted.");
        Guard.Require(corrections.Select(x => x.Id).Distinct().Count() == corrections.Count,
            "duplicate_invoice", "Each preceding correction may occur only once.");
        Guard.Require(corrections.All(x => x.Kind == InvoiceKind.CreditNote && x.Currency == original.Currency
            && x.OrderId == original.OrderId && x.CustomerId == original.CustomerId),
            "invalid_correction", "All preceding corrections must match the original document.");
        return Complete(invoice, new(MethodVersion, InvoiceCalculationBasis.OriginalInvoice, original.Id,
            [new("invoices", original.Id, original.Number, original.IssuedDigest, Clone(original.Snapshot!))],
            PreviousDocuments(corrections), PreviousBalances(corrections, subtractCorrections: false), [], ""));
    }

    private static IReadOnlyList<InvoicePreviousDocument> PreviousDocuments(IEnumerable<Invoice> invoices) => invoices
        .OrderBy(x => x.IssueDate).ThenBy(x => x.Number, StringComparer.Ordinal).ThenBy(x => x.Id)
        .Select(x => new InvoicePreviousDocument(x.Id, x.Number, x.Kind, x.OriginalInvoiceId, x.IssuedDigest)).ToList().AsReadOnly();

    private static IReadOnlyList<InvoicePreviousBalance> PreviousBalances(IEnumerable<Invoice> invoices, bool subtractCorrections)
        => invoices.SelectMany(invoice => invoice.Snapshot!.Lines.Select(line => new
            {
                Key = LineKey(line.Inputs), Sign = subtractCorrections && invoice.Kind == InvoiceKind.CreditNote ? -1m : 1m, Line = line
            }))
            .GroupBy(x => x.Key, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => new InvoicePreviousBalance(x.Key, x.Sum(y => y.Sign * y.Line.Inputs.Quantity),
                x.Sum(y => y.Sign * y.Line.Net), x.Sum(y => y.Sign * y.Line.Tax))).ToList().AsReadOnly();

    private static QuoteSnapshot Clone(QuoteSnapshot source) => source with
    {
        Lines = source.Lines.Select(x => x with { Inputs = x.Inputs with { } }).ToList().AsReadOnly()
    };

    private static InvoiceCalculation Complete(Invoice invoice, InvoiceCalculation calculation)
    {
        var (_, lines) = Rebuild(invoice, calculation);
        var complete = calculation with { Lines = lines, Digest = "" };
        return complete with { Digest = ContentDigest.Of(complete) };
    }

    internal static QuoteSnapshot Snapshot(Invoice invoice, InvoiceCalculation calculation) => Rebuild(invoice, calculation).Snapshot;

    public static void Validate(Invoice invoice)
    {
        Guard.Require(invoice.Calculation is not null && invoice.Snapshot is not null,
            "missing_snapshot", "The cumulative invoice needs its frozen calculation and snapshot.");
        var calculation = invoice.Calculation;
        Guard.Items(calculation.Lines, nameof(calculation.Lines), 1000, 1);
        Guard.Require(IsDigest(calculation.Digest) && calculation.Digest == ContentDigest.Of(calculation with { Digest = "" }),
            "invoice_calculation_changed", "Frozen invoice calculation evidence has changed.");
        var (snapshot, lines) = Rebuild(invoice, calculation);
        Guard.Require(snapshot.Gross > 0m || IsZeroCentFinalSettlement(invoice, calculation, snapshot),
            "zero_invoice", "A zero-cent issued document is allowed only for a fully evidenced final quantity settlement.");
        Guard.Require(ContentDigest.Of(lines) == ContentDigest.Of(calculation.Lines)
            && ContentDigest.Of(snapshot) == ContentDigest.Of(invoice.Snapshot),
            "invoice_calculation_changed", "Invoice amounts no longer follow the frozen cumulative calculation.");
    }

    /// <summary>
    /// A final invoice may settle the last physical quantities without another cent being owed.
    /// This is not permission to issue a stand-alone zero-value invoice, a zero-value advance,
    /// or a zero-value correction. All accepted quantities are checked by Rebuild first.
    /// </summary>
    internal static bool IsZeroCentFinalSettlement(Invoice invoice, InvoiceCalculation calculation, QuoteSnapshot snapshot)
        => invoice.Kind == InvoiceKind.Final
            && calculation.Basis == InvoiceCalculationBasis.Contract
            && snapshot.Net == 0m && snapshot.Tax == 0m && snapshot.Gross == 0m
            && calculation.PreviousInvoices.Count > 0
            && calculation.PreviousBalances.Any(x => x.Net > 0m || x.Tax > 0m)
            && calculation.Lines.Count == invoice.Lines.Count
            && calculation.Lines.All(x => x.AfterQuantity > x.BeforeQuantity
                && x.AfterNet == x.BeforeNet && x.AfterTax == x.BeforeTax);

    private sealed record Position(int SourceIndex, int PositionIndex, PricedLine Line);

    private static (QuoteSnapshot Snapshot, IReadOnlyList<InvoiceCalculatedLine> Lines) Rebuild(Invoice invoice, InvoiceCalculation calculation)
    {
        ValidateBasis(invoice, calculation);
        var positions = calculation.Sources.SelectMany((source, sourceIndex) => source.Snapshot.Lines.Select((line, positionIndex) =>
            new Position(sourceIndex, positionIndex, line))).GroupBy(x => LineKey(x.Line.Inputs), StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.Ordinal);
        var before = calculation.PreviousBalances.ToDictionary(x => x.LineKey, StringComparer.Ordinal);
        Guard.Require(invoice.Lines.Sum(x => positions.GetValueOrDefault(LineKey(x))?.Count ?? 0) <= MaximumSourcePositions,
            "invoice_evidence_budget", "The expanded source-position calculation budget is exhausted.");
        foreach (var balance in before.Values)
        {
            Guard.Require(positions.TryGetValue(balance.LineKey, out var group), "uncontracted_line", "A previous billed position has no frozen source.");
            Guard.Range(balance.Quantity, 0, MaximumSourcePositions * 1_000_000m, "Previously billed quantity");
            Money.Amount(balance.Net, "Previously billed net"); Money.Amount(balance.Tax, "Previously billed tax");
            Guard.Require(balance.Quantity <= group.Sum(x => x.Line.Inputs.Quantity)
                && balance.Net <= group.Sum(x => x.Line.Net) && balance.Tax <= group.Sum(x => x.Line.Tax),
                "invoice_rounding_conflict", "Historical amounts or quantities exceed their source. Correct the source documents before further billing.");
        }
        var priced = new List<PricedLine>(); var evidence = new List<InvoiceCalculatedLine>();
        for (var lineIndex = 0; lineIndex < invoice.Lines.Count; lineIndex++)
        {
            var input = invoice.Lines[lineIndex]; input.Validate(allowPartialMeasurement: true); var key = LineKey(input);
            Guard.Require(positions.TryGetValue(key, out var group),
                calculation.Basis == InvoiceCalculationBasis.Contract ? "uncontracted_line" : "invalid_credit_lines",
                "Invoice positions must retain their frozen source inputs, unit prices, tax rates and measurement evidence.");
            var prior = before.GetValueOrDefault(key) ?? new(key, 0, 0, 0);
            var afterQuantity = prior.Quantity + input.Quantity;
            Guard.Require(afterQuantity <= group.Sum(x => x.Line.Inputs.Quantity),
                calculation.Basis == InvoiceCalculationBasis.Contract ? "order_overbilled" : "excess_credit",
                "Cumulative quantities cannot exceed the frozen source positions.");
            var positionEvidence = Allocate(group, prior.Quantity, afterQuantity, calculation.Basis);
            var afterNet = positionEvidence.Sum(x => x.AfterNet); var afterTax = positionEvidence.Sum(x => x.AfterTax);
            var net = afterNet - prior.Net; var tax = afterTax - prior.Tax;
            Guard.Require(net >= 0 && tax >= 0, "invoice_rounding_conflict",
                "The requested quantity would require a negative line adjustment. Use a reviewed correction of preceding documents.");
            Money.Amount(net, "Invoice line net"); Money.Amount(tax, "Invoice line tax"); Money.Amount(net + tax, "Invoice line gross");
            var unit = Pricing.Price(input, allowPartialMeasurement: true);
            priced.Add(unit with { Net = net, Tax = tax, Gross = net + tax });
            evidence.Add(new(lineIndex, key, prior.Quantity, afterQuantity, prior.Net, prior.Tax,
                positionEvidence.Sum(x => x.BeforeNet), positionEvidence.Sum(x => x.BeforeTax), afterNet, afterTax, positionEvidence));
            before[key] = new(key, afterQuantity, afterNet, afterTax);
        }
        if (invoice.Kind == InvoiceKind.Final)
            Guard.Require(positions.All(x => before.GetValueOrDefault(x.Key)?.Quantity == x.Value.Sum(y => y.Line.Inputs.Quantity)),
                "incomplete_final_invoice", "Final invoice quantities must settle every accepted source position.");
        var netTotal = priced.Sum(x => x.Net); var taxTotal = priced.Sum(x => x.Tax);
        Money.Amount(netTotal, "Invoice net"); Money.Amount(taxTotal, "Invoice tax"); Money.Amount(netTotal + taxTotal, "Invoice gross");
        var snapshot = new QuoteSnapshot(1, invoice.Currency, priced.AsReadOnly(), netTotal, taxTotal, netTotal + taxTotal, "");
        return (snapshot with { Digest = SnapshotDigest(snapshot) }, evidence.AsReadOnly());
    }

    private static IReadOnlyList<InvoicePositionCalculation> Allocate(IReadOnlyList<Position> positions, decimal before, decimal after,
        InvoiceCalculationBasis basis)
    {
        var result = new List<InvoicePositionCalculation>(); var consumed = 0m;
        foreach (var position in positions)
        {
            var line = position.Line;
            var previousQuantity = Math.Clamp(before - consumed, 0, line.Inputs.Quantity);
            var nextQuantity = Math.Clamp(after - consumed, 0, line.Inputs.Quantity);
            var (previousNet, previousTax) = Cumulative(line, previousQuantity, basis);
            var (nextNet, nextTax) = Cumulative(line, nextQuantity, basis);
            result.Add(new(position.SourceIndex, position.PositionIndex, previousQuantity, nextQuantity,
                previousNet, nextNet, previousTax, nextTax));
            consumed += line.Inputs.Quantity;
        }
        return result.AsReadOnly();
    }

    private static (decimal Net, decimal Tax) Cumulative(PricedLine source, decimal quantity, InvoiceCalculationBasis basis)
    {
        if (quantity == 0) return (0, 0);
        // Full source quantities always consume exactly the original cents, including any carried rounding cent.
        if (quantity == source.Inputs.Quantity) return (source.Net, source.Tax);
        if (basis == InvoiceCalculationBasis.OriginalInvoice)
            return (Money.Round(source.Net * quantity / source.Inputs.Quantity), Money.Round(source.Tax * quantity / source.Inputs.Quantity));
        var net = Money.Round(source.UnitNet * quantity);
        return (net, Money.Round(net * source.Inputs.VatPercent / 100m));
    }

    private static void ValidateBasis(Invoice invoice, InvoiceCalculation calculation)
    {
        Guard.Require(calculation.MethodVersion == MethodVersion && Enum.IsDefined(calculation.Basis),
            "unsupported_invoice_method", "The frozen invoice arithmetic method is not supported.");
        Guard.Require((invoice.Kind == InvoiceKind.CreditNote) == (calculation.Basis == InvoiceCalculationBasis.OriginalInvoice)
            && calculation.BasisId == (invoice.Kind == InvoiceKind.CreditNote ? invoice.OriginalInvoiceId : invoice.OrderId),
            "invoice_calculation_changed", "Invoice calculation basis does not match the document.");
        Guard.Items(invoice.Lines, nameof(invoice.Lines), 1000, 1); Guard.Currency(invoice.Currency);
        Guard.Items(calculation.Sources, nameof(calculation.Sources), MaximumSources, 1);
        Guard.Require(calculation.Sources.Sum(x => x.Snapshot?.Lines?.Count ?? 0) <= MaximumSourcePositions,
            "invoice_evidence_budget", "The cumulative source-position budget is exhausted.");
        Guard.Require(calculation.Sources.Select(x => (x.Kind, x.Id)).Distinct().Count() == calculation.Sources.Count,
            "duplicate_source", "Each source document may appear only once.");
        for (var index = 0; index < calculation.Sources.Count; index++)
        {
            var source = calculation.Sources[index]; Guard.Id(source.Id, "Source Id"); Guard.Text(source.Label, "Source label", 240);
            Guard.Require(source.Snapshot is not null && IsDigest(source.DocumentDigest), "invoice_calculation_changed", "The frozen source is incomplete.");
            if (calculation.Basis == InvoiceCalculationBasis.Contract)
            {
                Guard.Require(index == 0 ? source.Kind == "orders" && source.Id == calculation.BasisId : source.Kind == "change-orders",
                    "invoice_calculation_changed", "The contract basis requires one order followed by accepted changes.");
                source.Snapshot.Validate();
                Guard.Require(source.DocumentDigest == source.Snapshot.Digest, "invoice_calculation_changed", "The accepted source digest has changed.");
            }
            else
            {
                Guard.Require(calculation.Sources.Count == 1 && source.Kind == "invoices" && source.Id == calculation.BasisId,
                    "invoice_calculation_changed", "Corrections use only their original invoice's actual snapshot.");
                ValidateActualSnapshot(source.Snapshot);
            }
            Guard.Require(source.Snapshot.Currency == invoice.Currency, "invoice_order_mismatch", "The source currency must match the invoice.");
        }
        Guard.Items(calculation.PreviousInvoices, nameof(calculation.PreviousInvoices), MaximumPriorDocuments);
        Guard.Require(calculation.PreviousInvoices.Select(x => x.Id).Distinct().Count() == calculation.PreviousInvoices.Count,
            "duplicate_invoice", "Each preceding document may appear only once.");
        foreach (var previous in calculation.PreviousInvoices)
        {
            Guard.Id(previous.Id, "Previous invoice Id"); Guard.Text(previous.Number, "Previous invoice number", 80);
            Guard.Require(previous.Id != invoice.Id && Enum.IsDefined(previous.Kind) && IsDigest(previous.Digest)
                && (previous.Kind == InvoiceKind.CreditNote) == previous.OriginalInvoiceId.HasValue,
                "invoice_calculation_changed", "A frozen preceding-document reference is invalid.");
            if (calculation.Basis == InvoiceCalculationBasis.OriginalInvoice)
                Guard.Require(previous.Kind == InvoiceKind.CreditNote && previous.OriginalInvoiceId == calculation.BasisId,
                    "invalid_correction", "A correction can consider only earlier corrections of the same original.");
        }
        Guard.Items(calculation.PreviousBalances, nameof(calculation.PreviousBalances), MaximumSourcePositions);
        Guard.Require(calculation.PreviousBalances.Select(x => x.LineKey).Distinct().Count() == calculation.PreviousBalances.Count
            && calculation.PreviousBalances.All(x => IsDigest(x.LineKey))
            && (calculation.PreviousInvoices.Count > 0 || calculation.PreviousBalances.Count == 0),
            "invoice_calculation_changed", "Frozen preceding balances are inconsistent.");
    }

    private static void ValidateActualSnapshot(QuoteSnapshot snapshot)
    {
        Guard.Currency(snapshot.Currency); Guard.Require(snapshot.Version == 1, "invalid_version", "An invoice snapshot must use version one.");
        Guard.Items(snapshot.Lines, nameof(snapshot.Lines), 1000, 1);
        foreach (var line in snapshot.Lines)
        {
            var unit = Pricing.Price(line.Inputs, allowPartialMeasurement: true);
            Money.Amount(line.Net, "Frozen net"); Money.Amount(line.Tax, "Frozen tax"); Money.Amount(line.Gross, "Frozen gross");
            Guard.Require(line.UnitCost == unit.UnitCost && line.UnitNet == unit.UnitNet && line.Gross == line.Net + line.Tax,
                "snapshot_changed", "The original invoice position has changed.");
        }
        Guard.Require(snapshot.Net == snapshot.Lines.Sum(x => x.Net) && snapshot.Tax == snapshot.Lines.Sum(x => x.Tax)
            && snapshot.Gross == snapshot.Net + snapshot.Tax && snapshot.Digest == SnapshotDigest(snapshot),
            "snapshot_changed", "The original invoice's actual amounts have changed.");
    }

    private static string SnapshotDigest(QuoteSnapshot snapshot) => ContentDigest.Of(new
    {
        snapshot.Version, snapshot.Currency, snapshot.Lines, snapshot.Net, snapshot.Tax, snapshot.Gross
    });

    private static bool IsDigest(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
