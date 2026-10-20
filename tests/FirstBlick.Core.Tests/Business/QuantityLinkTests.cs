using FirstBlick.Core.Business;
using FirstBlick.Core.Technical;

namespace FirstBlick.Core.Tests.Business;

public sealed class QuantityLinkTests
{
    [Fact]
    public void Conversion_uses_frozen_net_quantity_explicit_factor_and_one_final_six_place_rounding()
    {
        var measurement = Frozen();
        var evidence = QuantityLinks.From(measurement, measurement.Lines[0].Id, measurement.Snapshot!.Digest, "m²", 2, 10, "Two layers, with explicitly reviewed 10% cutting allowance");
        Assert.Equal(13.5m, evidence.BaseQuantity); Assert.Equal(29.7m, evidence.Quantity); Assert.Equal("m²", evidence.BaseUnit);
        Assert.Equal(QuantityLinks.MethodVersion, evidence.MethodVersion); Assert.Equal(64, evidence.Digest.Length); evidence.Validate();
        Assert.Equal(0.000002m, QuantityLinks.Calculate(.000001m, 1.5m, 0));
        Assert.Equal(0.333333m, QuantityLinks.Calculate(1m, .333333m, 0));
    }

    [Theory]
    [InlineData("quantity")]
    [InlineData("factor")]
    [InlineData("allowance")]
    [InlineData("unit")]
    [InlineData("rule")]
    [InlineData("method")]
    [InlineData("digest")]
    public void The_evidence_rejects_changes_to_every_basis_and_result(string fault)
    {
        var m = Frozen(); var evidence = QuantityLinks.From(m, m.Lines[0].Id, m.Snapshot!.Digest, "m²", 1, 0, "Direct geometric net quantity, no normative deduction");
        evidence = fault switch
        {
            "quantity" => evidence with { Quantity = evidence.Quantity + 1 }, "factor" => evidence with { ConversionFactor = 2 },
            "allowance" => evidence with { WastePercent = 5 }, "unit" => evidence with { TargetUnit = "Stk" },
            "rule" => evidence with { RuleSource = "Altered rule" }, "method" => evidence with { MethodVersion = "0.0.0" },
            _ => evidence with { Digest = new string('0', 64) }
        };
        Assert.Throws<DomainException>(evidence.Validate);
    }

    [Fact]
    public void Frozen_basis_identity_project_and_source_values_are_rechecked()
    {
        var m = Frozen(); var evidence = QuantityLinks.From(m, m.Lines[0].Id, m.Snapshot!.Digest, "m²", 1, 0, "Direct measurement");
        QuantityLinks.EnsureSource(evidence, m, m.TenantId, m.ProjectId);
        Assert.Equal("wrong_quantity_project", Assert.Throws<DomainException>(() => QuantityLinks.EnsureSource(evidence, m, Guid.NewGuid(), m.ProjectId)).Code);
        Assert.Equal("wrong_quantity_project", Assert.Throws<DomainException>(() => QuantityLinks.EnsureSource(evidence, m, m.TenantId, Guid.NewGuid())).Code);
        Assert.Equal("stale_quantity_basis", Assert.Throws<DomainException>(() => QuantityLinks.From(m, m.Lines[0].Id, new string('0', 64), "m²", 1, 0, "Direct measurement")).Code);
        Assert.Equal("quantity_line_missing", Assert.Throws<DomainException>(() => QuantityLinks.From(m, Guid.NewGuid(), m.Snapshot.Digest, "m²", 1, 0, "Direct measurement")).Code);
        Assert.Equal("required", Assert.Throws<DomainException>(() => QuantityLinks.From(m, m.Lines[0].Id, m.Snapshot.Digest, "Stk", .5m, 0, "")).Code);
        m.Status = MeasurementStatus.Draft; m.Snapshot = null; m.FrozenAt = null;
        Assert.Equal("measurement_not_frozen", Assert.Throws<DomainException>(() => QuantityLinks.From(m, m.Lines[0].Id, evidence.MeasurementDigest, "m²", 1, 0, "Direct measurement")).Code);
    }

    [Fact]
    public void Linked_sales_and_purchase_quantities_cannot_silently_change_unit_or_quantity()
    {
        var m = Frozen(); var evidence = QuantityLinks.From(m, m.Lines[0].Id, m.Snapshot!.Digest, "m²", 1, 0, "Direct measurement");
        var line = new QuoteLine { Description = "Work", Quantity = evidence.Quantity, Unit = evidence.TargetUnit, QuantityEvidence = evidence, UnitMaterialCost = 10 };
        var frozen = Pricing.Freeze([line]); frozen.Validate();
        Assert.Equal("quantity_target_changed", Assert.Throws<DomainException>(() => (line with { Quantity = 14 }).Validate()).Code);
        Assert.Equal("quantity_target_changed", Assert.Throws<DomainException>(() => (line with { Unit = "m" }).Validate()).Code);
        var purchase = new PurchaseLine { MaterialId = Guid.NewGuid(), MaterialDemandId = Guid.NewGuid(), QuantityEvidence = evidence, Description = "Material", Quantity = evidence.Quantity, UnitNet = 8 };
        purchase.Validate();
        Assert.Equal("quantity_target_changed", Assert.Throws<DomainException>(() => (purchase with { Quantity = 14 }).Validate()).Code);
        Assert.Equal("invalid_demand_reference", Assert.Throws<DomainException>(() => (purchase with { MaterialDemandId = null }).Validate()).Code);
    }

    [Fact]
    public void An_absent_quantity_link_preserves_the_exact_legacy_pricing_digest_shape()
    {
        var line = new QuoteLine { Description = "Legacy position", Quantity = 2, UnitMaterialCost = 10, VatPercent = 19 };
        var legacyInput = new { catalogItemId = (Guid?)null, description = "Legacy position", unit = "m²", quantity = 2m, unitMaterialCost = 10m,
            unitLaborHours = 0m, hourlyRate = 0m, markupPercent = 0m, discountPercent = 0m, vatPercent = 19m };
        var expected = ContentDigest.Of(new { version = 1, currency = "EUR", lines = new[] { new { inputs = legacyInput, unitCost = 10m, unitNet = 10m, net = 20m, tax = 3.8m, gross = 23.8m } },
            net = 20m, tax = 3.8m, gross = 23.8m });
        Assert.Equal(expected, Pricing.Freeze([line]).Digest);
    }

    [Fact]
    public void Partial_and_final_invoices_and_partial_corrections_preserve_the_complete_measurement_basis()
    {
        var order = MeasuredOrder(); var evidence = order.Contract!.Lines[0].Inputs.QuantityEvidence!;
        var originalContract = order.Contract.Digest; var day = new DateOnly(2026, 10, 20);
        var progress = InvoiceDraft(order, InvoiceKind.Progress); progress.Lines[0].Quantity = 5m;
        InvoiceWorkflow.IssueForOrder(progress, order, [], [], "P-1", day); progress.Validate();
        Assert.Equal(59.5m, progress.Snapshot!.Gross); Assert.Equal(evidence, progress.Snapshot.Lines[0].Inputs.QuantityEvidence);
        var final = InvoiceDraft(order, InvoiceKind.Final); final.Lines[0].Quantity = 8.5m;
        InvoiceWorkflow.IssueForOrder(final, order, [progress], [], "F-1", day); final.Validate();
        Assert.Equal(160.65m, progress.Snapshot.Gross + final.Snapshot!.Gross);
        var credit = InvoiceWorkflow.DraftCreditNote(progress, "Correct one measured unit"); credit.DueDate = day; credit.Lines[0].Quantity = 1m;
        InvoiceWorkflow.IssueCreditNote(credit, progress, [], "C-1", day); credit.Validate();
        Assert.Equal(11.9m, credit.Snapshot!.Gross); Assert.Equal(evidence, credit.Snapshot.Lines[0].Inputs.QuantityEvidence);
        var replacement = InvoiceDraft(order, InvoiceKind.Final); replacement.Lines[0].Quantity = 1m;
        InvoiceWorkflow.IssueForOrder(replacement, order, [progress, final, credit], [], "F-2", day); replacement.Validate();
        Assert.Equal(originalContract, order.Contract.Digest); Assert.Equal(13.5m, evidence.Quantity); Assert.Equal(13.5m, order.Contract.Lines[0].Inputs.Quantity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Partial_invoice_quantities_cannot_replace_or_detach_their_accepted_measurement_evidence(bool detach)
    {
        var order = MeasuredOrder(); var invoice = InvoiceDraft(order, InvoiceKind.Progress); invoice.Lines[0].Quantity = 5m;
        var forged = invoice.Lines[0].QuantityEvidence! with { ConversionFactor = 2m, Quantity = 27m, Digest = "" };
        forged = forged with { Digest = ContentDigest.Of(forged) }; forged.Validate();
        invoice.Lines[0].QuantityEvidence = detach ? null : forged;
        Assert.Equal("uncontracted_line", Assert.Throws<DomainException>(() => InvoiceWorkflow.IssueForOrder(invoice, order, [], [], "P-1", new(2026, 10, 20))).Code);
        Assert.Equal(InvoiceStatus.Draft, invoice.Status);
    }

    [Fact]
    public void Partial_measurement_validation_is_confined_to_invoices_and_cannot_expand_the_source_quantity()
    {
        var order = MeasuredOrder(); var line = order.Contract!.Lines[0].Inputs with { Quantity = 5m };
        Assert.Equal("quantity_target_changed", Assert.Throws<DomainException>(line.Validate).Code);
        var partial = Pricing.Freeze([line], allowPartialMeasurement: true); partial.Validate(allowPartialMeasurement: true);
        Assert.Equal("quantity_target_changed", Assert.Throws<DomainException>(partial.Validate).Code);
        order.Contract = partial;
        Assert.Equal("quantity_target_changed", Assert.Throws<DomainException>(order.Validate).Code);
        Assert.Equal("quantity_target_changed", Assert.Throws<DomainException>(() => (line with { Quantity = 14m }).Validate(allowPartialMeasurement: true)).Code);
        Assert.Equal("quantity_target_changed", Assert.Throws<DomainException>(() => (line with { Unit = "Stk" }).Validate(allowPartialMeasurement: true)).Code);
    }

    private static Order MeasuredOrder()
    {
        var m = Frozen(); var evidence = QuantityLinks.From(m, m.Lines[0].Id, m.Snapshot!.Digest, "m²", 1m, 0m, "Reviewed net survey quantity");
        var quote = new Quote { TenantId = m.TenantId, ProjectId = m.ProjectId, CustomerId = Guid.NewGuid(), Title = "Measured invoice contract",
            Lines = [new() { Description = "Measured work", Quantity = evidence.Quantity, Unit = "m²", UnitMaterialCost = 10m, VatPercent = 19m, QuantityEvidence = evidence }] };
        CommercialWorkflow.IssueQuote(quote, "Q-1"); CommercialWorkflow.AcceptQuote(quote, quote.Snapshot!.Digest, DateTimeOffset.UtcNow, new(2026, 10, 20));
        return CommercialWorkflow.CreateOrder(quote, "O-1");
    }

    private static Invoice InvoiceDraft(Order order, InvoiceKind kind)
    {
        var invoice = InvoiceWorkflow.DraftFromOrder(order, new Customer { TenantId = order.TenantId, Id = order.CustomerId, Name = "Synthetic measured customer", Address = "Synthetic address" }, kind);
        invoice.SellerName = "Synthetic contractor"; invoice.SellerAddress = "Synthetic workshop"; invoice.DueDate = new(2026, 11, 1);
        invoice.ServiceFrom = new(2026, 10, 1); invoice.ServiceUntil = new(2026, 10, 20); return invoice;
    }

    private static Measurement Frozen()
    {
        var now = DateTimeOffset.UtcNow; var tenantId = Guid.NewGuid(); var projectId = Guid.NewGuid();
        var roof = new RoofRevision { TenantId = tenantId, BuildingId = Guid.NewGuid(), ProjectId = projectId, Title = "Independent synthetic roof", CreatedAt = now, UpdatedAt = now,
            Surfaces = [new RoofSurface { Name = "Surveyed plane", Geometry = new PolygonRoofInput([new(0, 0), new(10, 0), new(10, 10), new(0, 10)], [], "Synthetic survey") }] };
        RoofWorkflow.Publish(roof, null, now.AddSeconds(1));
        var measurement = new Measurement { TenantId = tenantId, ProjectId = projectId, RoofRevisionId = roof.Id, Title = "Independent synthetic net area", CreatedAt = now, UpdatedAt = now,
            Lines = [new MeasurementLine { Description = "Surface", MeasurementSource = "Measured 4.2 by 3.25 m, minus explicit 0.15 m² opening", Input = new RectangleArea(4.2m, 3.25m), Deductions = [new("Opening", .15m)] }] };
        MeasurementWorkflow.Freeze(measurement, roof, now.AddSeconds(2)); return measurement;
    }
}
