namespace FirstBlick.Core.Business;

/// <summary>Explicit commercial quantity derivation; never a normative deduction or product suitability rule.</summary>
public sealed record QuantityEvidence(Guid MeasurementId, string MeasurementDigest, Guid MeasurementLineId, decimal BaseQuantity,
    string BaseUnit, string TargetUnit, decimal ConversionFactor, decimal WastePercent, string RuleSource, string MethodVersion, decimal Quantity, string Digest)
{
    public void Validate()
    {
        Guard.Id(MeasurementId, nameof(MeasurementId)); Guard.Id(MeasurementLineId, nameof(MeasurementLineId));
        Guard.Require(MeasurementDigest is { Length: 64 } && MeasurementDigest.All(char.IsAsciiHexDigit), "invalid_quantity_evidence", "A frozen measurement digest is required.");
        Guard.Text(BaseUnit, nameof(BaseUnit), 32); Guard.Text(TargetUnit, nameof(TargetUnit), 32); Guard.Text(RuleSource, nameof(RuleSource), 1000);
        Guard.Range(BaseQuantity, 0.000001m, MeasurementWorkflow.MaximumQuantity, nameof(BaseQuantity));
        Guard.Range(ConversionFactor, 0.000001m, 1_000_000m, nameof(ConversionFactor)); Guard.Range(WastePercent, 0, 100, nameof(WastePercent));
        Guard.Range(Quantity, 0.000001m, 1_000_000m, nameof(Quantity));
        Guard.Require(MethodVersion == QuantityLinks.MethodVersion && Quantity == QuantityLinks.Calculate(BaseQuantity, ConversionFactor, WastePercent)
            && Digest == ContentDigest.Of(this with { Digest = "" }), "quantity_evidence_changed", "The recorded conversion, allowance or resulting quantity has changed.");
    }
}

public static class QuantityLinks
{
    public const string MethodVersion = "1.0.0";
    public static decimal Calculate(decimal baseQuantity, decimal factor, decimal wastePercent) =>
        decimal.Round(baseQuantity * factor * (1m + wastePercent / 100m), 6, MidpointRounding.AwayFromZero);

    public static QuantityEvidence From(Measurement measurement, Guid lineId, string expectedDigest, string targetUnit, decimal factor, decimal wastePercent, string ruleSource)
    {
        measurement.Validate();
        Guard.Require(!measurement.Archived && measurement.Status == MeasurementStatus.Frozen && measurement.Snapshot is not null,
            "measurement_not_frozen", "Quantity transfer requires a retained, frozen measurement.");
        Guard.Require(measurement.Snapshot.Digest == expectedDigest, "stale_quantity_basis", "The selected measurement digest differs from its frozen evidence.");
        var line = measurement.Snapshot.Lines.SingleOrDefault(x => x.Id == lineId);
        Guard.Require(line is not null, "quantity_line_missing", "The selected measurement line is unavailable.");
        Guard.Range(factor, 0.000001m, 1_000_000m, nameof(factor)); Guard.Range(wastePercent, 0, 100, nameof(wastePercent));
        var evidence = new QuantityEvidence(measurement.Id, expectedDigest, lineId, line.Quantity, line.Unit, targetUnit, factor, wastePercent,
            ruleSource, MethodVersion, Calculate(line.Quantity, factor, wastePercent), "");
        evidence = evidence with { Digest = ContentDigest.Of(evidence) }; evidence.Validate(); return evidence;
    }

    public static void EnsureSource(QuantityEvidence evidence, Measurement measurement, Guid tenantId, Guid projectId)
    {
        evidence.Validate(); measurement.Validate();
        Guard.Require(measurement.Id == evidence.MeasurementId && measurement.TenantId == tenantId && measurement.ProjectId == projectId,
            "wrong_quantity_project", "The quantity basis must belong to this business and project.");
        var expected = From(measurement, evidence.MeasurementLineId, evidence.MeasurementDigest, evidence.TargetUnit,
            evidence.ConversionFactor, evidence.WastePercent, evidence.RuleSource);
        Guard.Require(expected == evidence, "quantity_evidence_changed", "The quantity reference differs from the retained measurement evidence.");
    }

    public static void EnsureTarget(QuantityEvidence? evidence, decimal quantity, string? unit = null, bool allowPartialMeasurement = false)
    {
        if (evidence is null) return;
        evidence.Validate();
        Guard.Require((allowPartialMeasurement ? quantity > 0 && quantity <= evidence.Quantity : evidence.Quantity == quantity)
            && (unit is null || evidence.TargetUnit == unit), "quantity_target_changed",
            allowPartialMeasurement
                ? "An invoiced quantity must be a positive part of its unchanged measurement basis and retain its unit."
                : "A linked quantity and its unit must match the reviewed measurement conversion.");
    }
}

public enum MaterialDemandStatus { Pending, Converted, Cancelled }
public sealed class MaterialDemand : TenantEntity
{
    public Guid ProjectId { get; set; }
    public Guid MaterialId { get; set; }
    public string Title { get; set; } = "";
    public DateOnly? RequiredOn { get; set; }
    public QuantityEvidence Evidence { get; set; } = null!;
    public MaterialDemandStatus Status { get; set; }
    public Guid? PurchaseOrderId { get; set; }
    public Guid? PurchaseLineId { get; set; }
    public string CancellationReason { get; set; } = "";
    public override void Validate()
    {
        ValidateCommon(); Guard.Id(ProjectId, nameof(ProjectId)); Guard.Id(MaterialId, nameof(MaterialId)); Guard.Text(Title, nameof(Title));
        Guard.Require(Evidence is not null, "missing_quantity_evidence", "A material demand requires its reviewed measurement evidence."); Evidence.Validate();
        Guard.Require(Enum.IsDefined(Status), "invalid_status", "The material demand status is invalid.");
        Guard.OptionalId(PurchaseOrderId, nameof(PurchaseOrderId)); Guard.OptionalId(PurchaseLineId, nameof(PurchaseLineId));
        Guard.Require(Status == MaterialDemandStatus.Converted ? PurchaseOrderId.HasValue && PurchaseLineId.HasValue : PurchaseOrderId is null && PurchaseLineId is null,
            "invalid_demand_state", "Only a converted demand may reference its purchase draft and line.");
        Guard.Text(CancellationReason, nameof(CancellationReason), 2000, Status == MaterialDemandStatus.Cancelled);
        Guard.Require(Status == MaterialDemandStatus.Cancelled || CancellationReason == "", "invalid_demand_state", "Only cancelled demand records contain a cancellation reason.");
    }
    public override IEnumerable<EntityReference> References()
    {
        yield return BusinessRegistry.Reference<Project>(ProjectId); yield return BusinessRegistry.Reference<Material>(MaterialId);
        yield return BusinessRegistry.Reference<Measurement>(Evidence.MeasurementId);
        if (PurchaseOrderId is { } purchaseId) yield return BusinessRegistry.Reference<PurchaseOrder>(purchaseId);
    }
}
