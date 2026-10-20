using System.Net;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Core.Technical;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FirstBlick.Api.Tests.PostgresApiFixture;

namespace FirstBlick.Api.Tests;

public sealed class QuantityApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    [PostgresApiFact]
    public async Task Quote_preview_binds_catalog_revisions_and_applies_frozen_quantities_once_without_altering_issued_quotes()
    {
        var g = await Setup(); var body = QuoteRequest(g); var route = $"/api/v1/quotes/{g.Quote.Id}/measurement-lines";
        var preview = await Ok<QuoteQuantityPreview>(Send(g.Client, HttpMethod.Post, route + "/preview", body, g.Quote.Revision));
        Assert.Equal(13.5m, Assert.Single(preview.Lines).Quantity); Assert.Equal(135m, preview.AddedNet); Assert.Equal(0m, preview.BeforeNet);
        g.Catalog.UnitMaterialCost = 12;
        var changed = await Ok<CatalogItem>(Send(g.Client, HttpMethod.Put, $"/api/v1/catalog-items/{g.Catalog.Id}", g.Catalog, g.Catalog.Revision));
        await Error(Send(g.Client, HttpMethod.Post, route + "/apply", body with { PreviewDigest = preview.Digest }, g.Quote.Revision), HttpStatusCode.BadRequest, "quantity_preview_changed");
        Assert.Empty((await fixture.Read<Quote>(g.Tenant.Id, g.Quote.Id)).Lines);
        preview = await Ok<QuoteQuantityPreview>(Send(g.Client, HttpMethod.Post, route + "/preview", body, g.Quote.Revision));
        Assert.Equal(162m, preview.AddedNet); Assert.Equal(changed.Revision, Assert.Single(preview.Catalog).Revision);
        var reviewed = body with { PreviewDigest = preview.Digest }; var key = Guid.NewGuid().ToString("N");
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Send(g.Client, HttpMethod.Post, route + "/apply", reviewed, g.Quote.Revision, key)));
        foreach (var response in responses) Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Equal(3, responses.Count(x => x.Headers.Contains("Idempotency-Replayed")));
        var quote = await fixture.Read<Quote>(g.Tenant.Id, g.Quote.Id); Assert.Single(quote.Lines); Assert.Equal(g.Measurement.Id, quote.Lines[0].QuantityEvidence!.MeasurementId);
        await Error(Send(g.Client, HttpMethod.Post, route + "/preview", body, quote.Revision), HttpStatusCode.BadRequest, "duplicate_quantity_mapping");
        quote = await Ok<Quote>(Send(g.Client, HttpMethod.Post, $"/api/v1/quotes/{quote.Id}/freeze", new { }, quote.Revision)); var digest = quote.IssuedDigest;
        await Error(Send(g.Client, HttpMethod.Post, route + "/apply", reviewed, quote.Revision), HttpStatusCode.BadRequest, "quote_immutable");
        changed.UnitMaterialCost = 999; _ = await Ok<CatalogItem>(Send(g.Client, HttpMethod.Put, $"/api/v1/catalog-items/{changed.Id}", changed, changed.Revision));
        var issued = await fixture.Read<Quote>(g.Tenant.Id, quote.Id); issued.Validate(); Assert.Equal(digest, issued.IssuedDigest); Assert.Equal(162m, issued.Snapshot!.Net);
    }

    [PostgresApiFact]
    public async Task Cross_tenant_project_and_self_consistent_forged_quantity_references_are_rejected()
    {
        var g = await Setup(); var other = await Setup(); var route = $"/api/v1/quotes/{g.Quote.Id}/measurement-lines";
        await Error(Send(g.Client, HttpMethod.Post, route + "/preview", QuoteRequest(g) with { MeasurementId = other.Measurement.Id }, 1), HttpStatusCode.NotFound, "not_found");
        var project = await fixture.Save(new Project { TenantId = g.Tenant.Id, CustomerId = g.Quote.CustomerId, BuildingId = g.Project.BuildingId, Name = "Other project" }, g.User.UserId);
        var wrongProject = await fixture.Save(new Quote { TenantId = g.Tenant.Id, ProjectId = project.Id, CustomerId = g.Quote.CustomerId, Title = "Another project offer" }, g.User.UserId);
        await Error(Send(g.Client, HttpMethod.Post, $"/api/v1/quotes/{wrongProject.Id}/measurement-lines/preview", QuoteRequest(g), 1), HttpStatusCode.BadRequest, "wrong_quantity_project");
        var preview = await Ok<QuoteQuantityPreview>(Send(g.Client, HttpMethod.Post, route + "/preview", QuoteRequest(g), 1));
        var original = preview.Lines[0].QuantityEvidence!;
        var forged = original with { BaseQuantity = 20m, Quantity = 20m, Digest = "" }; forged = forged with { Digest = ContentDigest.Of(forged) };
        g.Quote.Lines = [preview.Lines[0] with { Quantity = 20m, QuantityEvidence = forged }];
        await Error(Send(g.Client, HttpMethod.Put, $"/api/v1/quotes/{g.Quote.Id}", g.Quote, 1), HttpStatusCode.BadRequest, "quantity_evidence_changed");
        Assert.Empty((await fixture.Read<Quote>(g.Tenant.Id, g.Quote.Id)).Lines);
    }

    [PostgresApiFact]
    public async Task Reviewed_material_demand_becomes_one_unissued_purchase_draft_and_retains_its_quantity_links()
    {
        var g = await Setup(); var demand = await Demand(g); var secondMaterial = await fixture.Save(new Material { TenantId = g.Tenant.Id, Name = "Second layer", Unit = "m²" }, g.User.UserId);
        var second = await Demand(g, secondMaterial.Id); var body = new DemandPurchaseRequest(g.Supplier.Id, "Reviewed two-layer purchase", new DateOnly(2026, 11, 1),
            [new(demand.Id, demand.Revision, 8, 19), new(second.Id, second.Revision, 4, 19)]);
        var preview = await Ok<DemandPurchasePreview>(Send(g.Client, HttpMethod.Post, "/api/v1/material-demands/purchase-preview", body));
        Assert.Equal(2, preview.Lines.Count); Assert.Equal(162m, preview.Net); Assert.Equal(30.78m, preview.Tax);
        var reviewed = body with { PreviewDigest = preview.Digest }; var key = Guid.NewGuid().ToString("N");
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Send(g.Client, HttpMethod.Post, "/api/v1/material-demands/purchase-draft", reviewed, key: key)));
        foreach (var response in responses) Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var purchase = AppJson.Read<PurchaseOrder>(await responses[0].Content.ReadAsStringAsync()); Assert.Equal(PurchaseOrderStatus.Draft, purchase.Status); Assert.Equal("", purchase.Number);
        Assert.Equal(3, responses.Count(x => x.Headers.Contains("Idempotency-Replayed")));
        var converted = await fixture.Read<MaterialDemand>(g.Tenant.Id, demand.Id); Assert.Equal(MaterialDemandStatus.Converted, converted.Status); Assert.Equal(purchase.Id, converted.PurchaseOrderId);
        Assert.Equal(demand.Evidence, purchase.Lines[0].QuantityEvidence); Assert.Equal(demand.Id, purchase.Lines[0].MaterialDemandId);
        _ = await fixture.CreateUser(g.Tenant.Id); // Two active directors make the configured four-eyes policy applicable.
        await Error(Send(g.Client, HttpMethod.Post, $"/api/v1/purchase-orders/{purchase.Id}/issue", new { }, purchase.Revision), HttpStatusCode.Conflict, "approval_required");
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            Assert.Equal(1, await db.Entities.IgnoreQueryFilters().CountAsync(x => x.TenantId == g.Tenant.Id && x.Kind == "purchase-orders"));
            Assert.Equal(0, await db.Entities.IgnoreQueryFilters().CountAsync(x => x.TenantId == g.Tenant.Id && x.Kind == "stock-movements"));
        });
        purchase.Lines[0].UnitNet = 9; purchase = await Ok<PurchaseOrder>(Send(g.Client, HttpMethod.Put, $"/api/v1/purchase-orders/{purchase.Id}", purchase, purchase.Revision));
        var retained = purchase.Lines[0]; purchase.Lines.RemoveAt(0);
        await Error(Send(g.Client, HttpMethod.Put, $"/api/v1/purchase-orders/{purchase.Id}", purchase, purchase.Revision), HttpStatusCode.BadRequest, "purchase_demand_changed");
        purchase.Lines.Insert(0, retained with { MaterialDemandId = null, QuantityEvidence = null });
        await Error(Send(g.Client, HttpMethod.Put, $"/api/v1/purchase-orders/{purchase.Id}", purchase, purchase.Revision), HttpStatusCode.BadRequest, "purchase_demand_changed");
        await Error(Send(g.Client, HttpMethod.Post, $"/api/v1/material-demands/{demand.Id}/cancel", new { reason = "Cannot erase converted link" }, converted.Revision), HttpStatusCode.BadRequest, "invalid_transition");
    }

    [PostgresApiFact]
    public async Task Demand_uniqueness_cancellation_and_material_unit_changes_have_explicit_boundaries()
    {
        var g = await Setup(); var demand = await Demand(g); var request = DemandRequest(g);
        var preview = await Ok<MaterialDemandPreview>(Send(g.Client, HttpMethod.Post, "/api/v1/material-demands/preview", request));
        await Error(Send(g.Client, HttpMethod.Post, "/api/v1/material-demands/from-measurement", request with { PreviewDigest = preview.Digest }), HttpStatusCode.Conflict, "duplicate");
        g.Material.Unit = "Stk";
        await Error(Send(g.Client, HttpMethod.Put, $"/api/v1/materials/{g.Material.Id}", g.Material, g.Material.Revision), HttpStatusCode.BadRequest, "material_unit_in_use");
        await Error(Send(g.Client, HttpMethod.Post, "/api/v1/material-demands", demand), HttpStatusCode.BadRequest, "command_required");
        await Error(Send(g.Client, HttpMethod.Put, $"/api/v1/material-demands/{demand.Id}", demand, demand.Revision), HttpStatusCode.BadRequest, "command_required");
        var cancelled = await Ok<MaterialDemand>(Send(g.Client, HttpMethod.Post, $"/api/v1/material-demands/{demand.Id}/cancel", new { reason = "Survey scope changed; retained for history" }, demand.Revision));
        Assert.Equal(MaterialDemandStatus.Cancelled, cancelled.Status); Assert.NotEmpty(cancelled.CancellationReason);
        var replacement = await Demand(g); Assert.NotEqual(demand.Id, replacement.Id);
        var stale = new DemandPurchaseRequest(g.Supplier.Id, "Stale selection", null, [new(replacement.Id, 99, 8, 19)]);
        await Error(Send(g.Client, HttpMethod.Post, "/api/v1/material-demands/purchase-preview", stale), HttpStatusCode.Conflict, "revision_conflict");
    }

    [PostgresApiFact]
    public async Task Quantity_commands_require_current_roles_and_modules_and_deny_revoked_receipt_replay()
    {
        var g = await Setup(); var worker = await fixture.Login(await fixture.CreateUser(g.Tenant.Id, BusinessRole.Worker));
        await Error(Send(worker, HttpMethod.Post, "/api/v1/material-demands/preview", DemandRequest(g)), HttpStatusCode.Forbidden, "forbidden");
        var request = DemandRequest(g); var preview = await Ok<MaterialDemandPreview>(Send(g.Client, HttpMethod.Post, "/api/v1/material-demands/preview", request));
        var body = request with { PreviewDigest = preview.Digest }; var key = Guid.NewGuid().ToString("N");
        _ = await Ok<MaterialDemand>(Send(g.Client, HttpMethod.Post, "/api/v1/material-demands/from-measurement", body, key: key));
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>(); var tenant = await db.Tenants.SingleAsync(x => x.Id == g.Tenant.Id);
            tenant.Modules = tenant.Modules.Where(x => x != "purchasing").ToArray(); await db.SaveChangesAsync();
        });
        await Error(Send(g.Client, HttpMethod.Post, "/api/v1/material-demands/from-measurement", body, key: key), HttpStatusCode.Forbidden, "module_disabled");
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>(); (await db.Memberships.SingleAsync(x => x.UserId == g.User.UserId && x.TenantId == g.Tenant.Id)).Active = false; await db.SaveChangesAsync();
        });
        await Error(Send(g.Client, HttpMethod.Post, "/api/v1/material-demands/from-measurement", body, key: key), HttpStatusCode.Forbidden, "forbidden");
    }

    [PostgresApiFact]
    public async Task Reviewed_measurement_lines_support_partial_final_and_correcting_invoices_without_losing_the_accepted_basis()
    {
        var g = await Setup(); var body = QuoteRequest(g); var route = $"/api/v1/quotes/{g.Quote.Id}/measurement-lines";
        var preview = await Ok<QuoteQuantityPreview>(Send(g.Client, HttpMethod.Post, route + "/preview", body, g.Quote.Revision));
        var quote = await Ok<Quote>(Send(g.Client, HttpMethod.Post, route + "/apply", body with { PreviewDigest = preview.Digest }, g.Quote.Revision));
        quote = await Ok<Quote>(Send(g.Client, HttpMethod.Post, $"/api/v1/quotes/{quote.Id}/freeze", new { }, quote.Revision));
        quote = await Ok<Quote>(Send(g.Client, HttpMethod.Post, $"/api/v1/quotes/{quote.Id}/accept", new { digest = quote.IssuedDigest }, quote.Revision));
        var order = await Ok<Order>(Send(g.Client, HttpMethod.Post, $"/api/v1/orders/from-quote/{quote.Id}", new { }, quote.Revision));
        var evidence = order.Contract!.Lines[0].Inputs.QuantityEvidence!; var basisDigest = order.Contract.Digest;
        async Task<Invoice> Draft(InvoiceKind kind, decimal quantity)
        {
            var invoice = await Ok<Invoice>(Send(g.Client, HttpMethod.Post, $"/api/v1/invoices/from-order/{order.Id}", new { kind }, order.Revision));
            invoice.Lines[0].Quantity = quantity; invoice.SellerAddress = "Synthetic workshop"; invoice.BuyerAddress = "Synthetic customer address";
            return await Ok<Invoice>(Send(g.Client, HttpMethod.Put, $"/api/v1/invoices/{invoice.Id}", invoice, invoice.Revision));
        }
        var progress = await Draft(InvoiceKind.Progress, 5m);
        progress = await Ok<Invoice>(Send(g.Client, HttpMethod.Post, $"/api/v1/invoices/{progress.Id}/issue", new { }, progress.Revision));
        var final = await Draft(InvoiceKind.Final, 8.5m);
        final = await Ok<Invoice>(Send(g.Client, HttpMethod.Post, $"/api/v1/invoices/{final.Id}/issue", new { }, final.Revision));
        Assert.Equal(160.65m, progress.Snapshot!.Gross + final.Snapshot!.Gross);
        Assert.Equal(evidence, progress.Snapshot.Lines[0].Inputs.QuantityEvidence); Assert.Equal(evidence, final.Snapshot.Lines[0].Inputs.QuantityEvidence);
        var credit = await Ok<Invoice>(Send(g.Client, HttpMethod.Post, $"/api/v1/invoices/{progress.Id}/correction", new { reason = "Correct one measured unit" }, progress.Revision));
        credit.Lines[0].Quantity = 1m;
        credit = await Ok<Invoice>(Send(g.Client, HttpMethod.Put, $"/api/v1/invoices/{credit.Id}", credit, credit.Revision));
        credit = await Ok<Invoice>(Send(g.Client, HttpMethod.Post, $"/api/v1/invoices/{credit.Id}/issue", new { }, credit.Revision));
        Assert.Equal(11.9m, credit.Snapshot!.Gross); Assert.Equal(evidence, credit.Snapshot.Lines[0].Inputs.QuantityEvidence);
        var invalid = await Draft(InvoiceKind.Final, 1m); invalid.Lines[0].QuantityEvidence = null;
        invalid = await Ok<Invoice>(Send(g.Client, HttpMethod.Put, $"/api/v1/invoices/{invalid.Id}", invalid, invalid.Revision));
        await Error(Send(g.Client, HttpMethod.Post, $"/api/v1/invoices/{invalid.Id}/issue", new { }, invalid.Revision), HttpStatusCode.BadRequest, "uncontracted_line");
        invalid.Lines[0].QuantityEvidence = evidence;
        invalid = await Ok<Invoice>(Send(g.Client, HttpMethod.Put, $"/api/v1/invoices/{invalid.Id}", invalid, invalid.Revision));
        _ = await Ok<Invoice>(Send(g.Client, HttpMethod.Post, $"/api/v1/invoices/{invalid.Id}/issue", new { }, invalid.Revision));
        Assert.Equal(basisDigest, (await fixture.Read<Order>(g.Tenant.Id, order.Id)).Contract!.Digest);
        Assert.Equal(13.5m, (await fixture.Read<Measurement>(g.Tenant.Id, g.Measurement.Id)).Snapshot!.Lines[0].Quantity);
    }

    private static QuoteQuantityRequest QuoteRequest(Graph g) => new(g.Measurement.Id, g.Measurement.Snapshot!.Digest, [new(g.Measurement.Lines[0].Id, g.Catalog.Id, 1, 0, "Direct explicit net survey quantity")]);
    private static MaterialDemandRequest DemandRequest(Graph g, Guid? material = null) => new(g.Measurement.Id, g.Measurement.Snapshot!.Digest, g.Measurement.Lines[0].Id,
        material ?? g.Material.Id, "Material from reviewed measurement", null, 1, 0, "Direct explicit net survey quantity");
    private static async Task<MaterialDemand> Demand(Graph g, Guid? material = null)
    {
        var request = DemandRequest(g, material); var preview = await Ok<MaterialDemandPreview>(Send(g.Client, HttpMethod.Post, "/api/v1/material-demands/preview", request));
        return await Ok<MaterialDemand>(Send(g.Client, HttpMethod.Post, "/api/v1/material-demands/from-measurement", request with { PreviewDigest = preview.Digest }));
    }
    private async Task<Graph> Setup()
    {
        var tenant = await fixture.CreateTenant(); var user = await fixture.CreateUser(tenant.Id); var client = await fixture.Login(user);
        var customer = await fixture.Save(new Customer { TenantId = tenant.Id, Name = "Synthetic quantity customer" }, user.UserId);
        var building = await fixture.Save(new Building { TenantId = tenant.Id, CustomerId = customer.Id, Name = "Quantity building", Address = "Synthetic address" }, user.UserId);
        var project = await fixture.Save(new Project { TenantId = tenant.Id, CustomerId = customer.Id, BuildingId = building.Id, Name = "Quantity project" }, user.UserId);
        var roof = new RoofRevision { TenantId = tenant.Id, BuildingId = building.Id, ProjectId = project.Id, Title = "Quantity plane",
            Surfaces = [new RoofSurface { Name = "Independent plane", Geometry = new PolygonRoofInput([new(0, 0), new(10, 0), new(10, 10), new(0, 10)], [], "Synthetic survey") }] };
        RoofWorkflow.Publish(roof, null, DateTimeOffset.UtcNow); await fixture.Save(roof, user.UserId);
        var measurement = new Measurement { TenantId = tenant.Id, ProjectId = project.Id, RoofRevisionId = roof.Id, Title = "Quantity survey",
            Lines = [new MeasurementLine { Description = "Net surface", MeasurementSource = "Synthetic 4.2 by 3.25 m minus 0.15 m²", Input = new RectangleArea(4.2m, 3.25m), Deductions = [new("Opening", .15m)] }] };
        MeasurementWorkflow.Freeze(measurement, roof, DateTimeOffset.UtcNow); await fixture.Save(measurement, user.UserId);
        var quote = await fixture.Save(new Quote { TenantId = tenant.Id, ProjectId = project.Id, CustomerId = customer.Id, Title = "Quantity offer" }, user.UserId);
        var catalog = await fixture.Save(new CatalogItem { TenantId = tenant.Id, Name = "Measured roof work", Unit = "m²", UnitMaterialCost = 10, VatPercent = 19 }, user.UserId);
        var material = await fixture.Save(new Material { TenantId = tenant.Id, Name = "Measured roofing sheet", Unit = "m²" }, user.UserId);
        var supplier = await fixture.Save(new Supplier { TenantId = tenant.Id, Name = "Synthetic material supplier" }, user.UserId);
        return new(tenant, user, client, project, measurement, quote, catalog, material, supplier);
    }
    private sealed record Graph(BusinessTenant Tenant, ApiTestUser User, HttpClient Client, Project Project, Measurement Measurement, Quote Quote, CatalogItem Catalog, Material Material, Supplier Supplier);
    private static async Task<T> Ok<T>(Task<HttpResponseMessage> pending)
    { var response = await pending; var json = await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode, json); return AppJson.Read<T>(json); }
    private static async Task Error(Task<HttpResponseMessage> pending, HttpStatusCode status, string code)
    {
        var response = await pending; var json = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"Expected {(int)status}, got {(int)response.StatusCode}: {json}"); Assert.Equal(code, JsonDocument.Parse(json).RootElement.GetProperty("code").GetString());
    }
}
