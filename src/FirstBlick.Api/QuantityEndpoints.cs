using FirstBlick.Core.Business;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FirstBlick.Api;

public sealed record QuoteQuantitySelection(Guid MeasurementLineId, Guid CatalogItemId, decimal ConversionFactor, decimal WastePercent, string RuleSource);
public sealed record QuoteQuantityRequest(Guid MeasurementId, string MeasurementDigest, IReadOnlyList<QuoteQuantitySelection> Lines, string? PreviewDigest = null);
public sealed record CatalogRevision(Guid Id, long Revision);
public sealed record QuoteQuantityPreview(Guid QuoteId, long QuoteRevision, Guid MeasurementId, string MeasurementDigest,
    IReadOnlyList<CatalogRevision> Catalog, IReadOnlyList<QuoteLine> Lines, decimal BeforeNet, decimal AddedNet, decimal AfterNet, string Digest);
public sealed record MaterialDemandRequest(Guid MeasurementId, string MeasurementDigest, Guid MeasurementLineId, Guid MaterialId,
    string Title, DateOnly? RequiredOn, decimal ConversionFactor, decimal WastePercent, string RuleSource, string? PreviewDigest = null);
public sealed record MaterialDemandPreview(Guid ProjectId, Guid MaterialId, long MaterialRevision, string MaterialName, string Title, DateOnly? RequiredOn, QuantityEvidence Evidence, string Digest);
public sealed record DemandPurchaseSelection(Guid DemandId, long Revision, decimal UnitNet, decimal VatPercent);
public sealed record DemandPurchaseRequest(Guid SupplierId, string Title, DateOnly? ExpectedOn, IReadOnlyList<DemandPurchaseSelection> Lines, string? PreviewDigest = null);
public sealed record DemandPurchasePreview(Guid ProjectId, Guid SupplierId, long SupplierRevision, string Title, DateOnly? ExpectedOn,
    IReadOnlyList<DemandPurchaseSelection> Selection, IReadOnlyList<PurchaseLine> Lines, decimal Net, decimal Tax, decimal Gross, string Digest);

/// <summary>Preview and explicit application bind frozen measurements to reviewed sales and purchasing inputs.</summary>
public static class QuantityEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/quotes/{id:guid}/measurement-lines/preview", async (Guid id, QuoteQuantityRequest body, [AsParameters] BusinessCommandServices c, CancellationToken ct) =>
        {
            await c.Commands.Authorize("quotes", true, ct);
            return Results.Ok(await QuotePreview(id, body, c, ct));
        }).WithTags("quantities");
        api.MapPost("/quotes/{id:guid}/measurement-lines/apply", (Guid id, QuoteQuantityRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("quotes", "quote.measurement_lines", new { id, body }, async ct =>
            {
                var preview = await QuotePreview(id, body, c, ct); Expected(body.PreviewDigest, preview.Digest);
                var quote = await c.Store.Get<Quote>(id, ct); quote.Lines.AddRange(preview.Lines.Select(x => x with { }));
                await GraphRules.Validate(quote, c.Store, c.Db, c.Scope, ct);
                await c.Store.Save(quote, "quote.measurement_lines", false, ct); return quote;
            })).WithTags("quantities");
        api.MapPost("/material-demands/preview", async (MaterialDemandRequest body, [AsParameters] BusinessCommandServices c, CancellationToken ct) =>
        {
            await c.Commands.Authorize("material-demands", true, ct);
            return Results.Ok(await DemandPreview(body, c, ct));
        }).WithTags("quantities");
        api.MapPost("/material-demands/from-measurement", (MaterialDemandRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("material-demands", "material_demand.create", body, async ct =>
            {
                var preview = await DemandPreview(body, c, ct); Expected(body.PreviewDigest, preview.Digest);
                var demand = new MaterialDemand { TenantId = c.Scope.TenantId, ProjectId = preview.ProjectId, MaterialId = preview.MaterialId,
                    Title = preview.Title, RequiredOn = preview.RequiredOn, Evidence = preview.Evidence };
                await GraphRules.Validate(demand, c.Store, c.Db, c.Scope, ct);
                return await c.SaveNew(demand, "material_demand.create", ct);
            })).WithTags("quantities");
        api.MapPost("/material-demands/{id:guid}/cancel", (Guid id, CorrectionRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("material-demands", "material_demand.cancel", new { id, body }, async ct =>
            {
                var demand = await c.Current<MaterialDemand>(id, ct); Guard.Text(body.Reason, "Reason", 2000);
                Guard.Require(demand.Status == MaterialDemandStatus.Pending, "invalid_transition", "Only an unconverted demand can be cancelled. Converted demand retains its reviewed purchase link.");
                demand.Status = MaterialDemandStatus.Cancelled; demand.CancellationReason = body.Reason;
                await c.Store.Save(demand, "material_demand.cancel", false, ct); return demand;
            })).WithTags("quantities");
        api.MapPost("/material-demands/purchase-preview", async (DemandPurchaseRequest body, [AsParameters] BusinessCommandServices c, CancellationToken ct) =>
        {
            await c.Commands.Authorize("purchase-orders", true, ct);
            return Results.Ok(await PurchasePreview(body, c, ct));
        }).WithTags("quantities");
        api.MapPost("/material-demands/purchase-draft", (DemandPurchaseRequest body, [AsParameters] BusinessCommandServices c) =>
            c.Execute("purchase-orders", "purchase.from_demands", body, async ct =>
            {
                var preview = await PurchasePreview(body, c, ct); Expected(body.PreviewDigest, preview.Digest);
                var purchase = new PurchaseOrder { TenantId = c.Scope.TenantId, ProjectId = preview.ProjectId, SupplierId = preview.SupplierId,
                    Title = preview.Title, ExpectedOn = preview.ExpectedOn, Lines = preview.Lines.Select(x => x with { }).ToList() };
                await c.SaveNew(purchase, "purchase.from_demands", ct);
                foreach (var selection in body.Lines)
                {
                    var demand = await c.Store.Get<MaterialDemand>(selection.DemandId, ct);
                    demand.Status = MaterialDemandStatus.Converted; demand.PurchaseOrderId = purchase.Id; demand.PurchaseLineId = demand.Id;
                    await c.Store.Save(demand, "material_demand.convert", false, ct);
                }
                return purchase;
            })).WithTags("quantities");
    }

    private static async Task<QuoteQuantityPreview> QuotePreview(Guid id, QuoteQuantityRequest body, BusinessCommandServices c, CancellationToken ct)
    {
        await c.Commands.Authorize("measurements", false, ct); await c.Commands.Authorize("catalog-items", false, ct);
        var quote = await c.Current<Quote>(id, ct); quote.Validate();
        Guard.Require(quote.Status == QuoteStatus.Draft, "quote_immutable", "Measurement transfer requires a draft. Issued and accepted quantities are retained unchanged.");
        var measurement = await c.Current<Measurement>(body.MeasurementId, ct, false);
        Guard.Require(measurement.ProjectId == quote.ProjectId, "wrong_quantity_project", "The measurement and quote must belong to the same project.");
        Guard.Items(body.Lines, nameof(body.Lines), 200, 1);
        Guard.Require(body.Lines.Select(x => (x.MeasurementLineId, x.CatalogItemId)).Distinct().Count() == body.Lines.Count,
            "duplicate_quantity_mapping", "Each selected measurement/catalog pair may occur only once.");
        var lines = new List<QuoteLine>(); var catalogs = new List<CatalogRevision>();
        foreach (var selection in body.Lines)
        {
            var item = await c.Current<CatalogItem>(selection.CatalogItemId, ct, false);
            Guard.Require(!quote.Lines.Any(x => x.CatalogItemId == item.Id && x.QuantityEvidence is { } e && e.MeasurementId == measurement.Id && e.MeasurementLineId == selection.MeasurementLineId),
                "duplicate_quantity_mapping", "This measurement/catalog pair is already included in the draft.");
            var evidence = QuantityLinks.From(measurement, selection.MeasurementLineId, body.MeasurementDigest, item.Unit, selection.ConversionFactor, selection.WastePercent, selection.RuleSource);
            var line = item.ToLine(evidence.Quantity); line.QuantityEvidence = evidence; line.Validate(); lines.Add(line); catalogs.Add(new(item.Id, item.Revision));
        }
        var before = quote.Lines.Count == 0 ? 0 : Pricing.Freeze(quote.Lines, quote.Version, quote.Currency).Net;
        var added = Pricing.Freeze(lines, quote.Version, quote.Currency).Net;
        var after = Pricing.Freeze(quote.Lines.Concat(lines).ToList(), quote.Version, quote.Currency).Net;
        var preview = new QuoteQuantityPreview(quote.Id, quote.Revision, measurement.Id, body.MeasurementDigest, catalogs, lines, before, added, after, "");
        return preview with { Digest = ContentDigest.Of(preview) };
    }

    private static async Task<MaterialDemandPreview> DemandPreview(MaterialDemandRequest body, BusinessCommandServices c, CancellationToken ct)
    {
        await c.Commands.Authorize("measurements", false, ct); await c.Commands.Authorize("materials", false, ct);
        Guard.Text(body.Title, nameof(body.Title));
        var measurement = await c.Current<Measurement>(body.MeasurementId, ct, false); var material = await c.Current<Material>(body.MaterialId, ct, false);
        var evidence = QuantityLinks.From(measurement, body.MeasurementLineId, body.MeasurementDigest, material.Unit, body.ConversionFactor, body.WastePercent, body.RuleSource);
        var preview = new MaterialDemandPreview(measurement.ProjectId, material.Id, material.Revision, material.Name, body.Title, body.RequiredOn, evidence, "");
        return preview with { Digest = ContentDigest.Of(preview) };
    }

    private static async Task<DemandPurchasePreview> PurchasePreview(DemandPurchaseRequest body, BusinessCommandServices c, CancellationToken ct)
    {
        await c.Commands.Authorize("material-demands", false, ct); Guard.Text(body.Title, nameof(body.Title));
        Guard.Items(body.Lines, nameof(body.Lines), 200, 1);
        Guard.Require(body.Lines.Select(x => x.DemandId).Distinct().Count() == body.Lines.Count, "duplicate_quantity_mapping", "Select every demand only once.");
        var supplier = await c.Current<Supplier>(body.SupplierId, ct, false); Guid? projectId = null; var lines = new List<PurchaseLine>();
        foreach (var selection in body.Lines)
        {
            var demand = await c.Current<MaterialDemand>(selection.DemandId, ct, false); demand.Validate();
            Guard.Require(demand.Revision == selection.Revision, "revision_conflict", "A selected demand changed. Review a fresh preview.");
            Guard.Require(demand.Status == MaterialDemandStatus.Pending, "invalid_transition", "Only unconverted demand can become a new purchase draft.");
            Guard.Require(projectId is null || projectId == demand.ProjectId, "wrong_quantity_project", "One purchase draft combines demand from exactly one project."); projectId = demand.ProjectId;
            await GraphRules.Validate(demand, c.Store, c.Db, c.Scope, ct);
            var line = new PurchaseLine { Id = demand.Id, MaterialId = demand.MaterialId, MaterialDemandId = demand.Id,
                Description = demand.Title, Quantity = demand.Evidence.Quantity, QuantityEvidence = demand.Evidence, UnitNet = selection.UnitNet, VatPercent = selection.VatPercent };
            line.Validate(); lines.Add(line);
        }
        var net = lines.Sum(x => Money.Round(x.Quantity * x.UnitNet)); var tax = lines.Sum(x => Money.Round(Money.Round(x.Quantity * x.UnitNet) * x.VatPercent / 100m));
        Money.Amount(net, "Net"); Money.Amount(tax, "Tax"); Money.Amount(net + tax, "Gross");
        var preview = new DemandPurchasePreview(projectId!.Value, supplier.Id, supplier.Revision, body.Title, body.ExpectedOn, body.Lines, lines, net, tax, net + tax, "");
        return preview with { Digest = ContentDigest.Of(preview) };
    }

    private static void Expected(string? selected, string current) => Guard.Require(selected == current, "quantity_preview_changed",
        "Review the current preview before applying it. Source, catalog, supplier, prices or revisions may have changed.");
}
