namespace FirstBlick.Core.Business;

/// <summary>
/// An immutable, explicitly reviewed cost forecast. It is a historical planning statement,
/// not an accounting posting or a claim that the underlying sources are complete.
/// </summary>
public sealed class CostForecast : TenantEntity
{
    public Guid OrderId { get; set; }
    public Guid ProjectId { get; set; }
    public int Version { get; set; }
    public Guid? PreviousForecastId { get; set; }
    public Guid CapturedByUserId { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
    public string Reason { get; set; } = "";
    public CostAnalysisResult Report { get; set; } = null!;
    public string Digest { get; set; } = "";

    public string CalculateDigest() => ContentDigest.Of(new
    {
        TenantId, Id, OrderId, ProjectId, Version, PreviousForecastId, CapturedByUserId,
        CapturedAt, Reason, Report
    });

    public override void Validate()
    {
        ValidateCommon(); Guard.Id(OrderId, nameof(OrderId)); Guard.Id(ProjectId, nameof(ProjectId));
        Guard.Id(CapturedByUserId, nameof(CapturedByUserId)); Guard.Utc(CapturedAt, nameof(CapturedAt));
        Guard.Require(Version is >= 1 and <= 500 && (Version == 1) == !PreviousForecastId.HasValue,
            "invalid_forecast_version", "A cost forecast version requires exactly the preceding version except for version one.");
        Guard.OptionalId(PreviousForecastId, nameof(PreviousForecastId));
        Guard.Text(Reason, nameof(Reason), 2000);
        Guard.Require(Report is not null && Report.Sources is not null && Report.ContractSources is not null
            && Report.Missing is not null && Report.ReviewedInputs is not null,
            "invalid_forecast_report", "The frozen cost report is missing.");
        Guard.Require(Report.OrderId == OrderId && Report.ProjectId == ProjectId
            && Report.MethodVersion == ProjectCosting.Version
            && Report.ContractSources.Any(x => x.Kind == "orders" && x.Id == OrderId),
            "invalid_forecast_report", "The cost report does not match the retained contract and supported method.");
        Guard.Require(Report.Digest == ContentDigest.Of(Report with { Digest = "" })
            && Digest == CalculateDigest(), "forecast_changed",
            "The retained cost forecast, its inputs or its source evidence has changed.");
    }

    public override IEnumerable<EntityReference> References()
    {
        yield return BusinessRegistry.Reference<Order>(OrderId);
        yield return BusinessRegistry.Reference<Project>(ProjectId);
        if (PreviousForecastId is { } preceding) yield return BusinessRegistry.Reference<CostForecast>(preceding);
        if (Report is not null)
        {
            foreach (var source in Report.ContractSources) yield return new(source.Kind, source.Id);
            foreach (var source in Report.Sources) yield return new(source.Kind, source.Id);
        }
    }
}
