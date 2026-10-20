using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Technical;
using FirstBlick.Infrastructure.Documents;
using FirstBlick.Infrastructure.Persistence;
using FirstBlick.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace FirstBlick.Api;

/// <summary>Explicitly bounded calculations. No normative assessment is inferred from a successful request.</summary>
public static class TechnicalEndpoints
{
    public sealed record RevisionRequest(string Reason);
    public sealed record CalculationRequest(Guid ProjectId, Guid? RoofRevisionId, string Title, string Method, JsonElement Input);
    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/technical/thermal", async (ThermalInput input, CommandExecutor commands, CancellationToken ct) =>
        { await commands.Authorize("technical-results", true, ct); return Results.Ok(ThermalCalculator.Calculate(input)); }).WithTags("technical");
        api.MapPost("/technical/moisture/screening", async (MoistureScreeningInput input, CommandExecutor commands, CancellationToken ct) =>
        { await commands.Authorize("technical-results", true, ct); return Results.Ok(MoistureScreening.Calculate(input)); }).WithTags("technical");
        api.MapPost("/technical/moisture/monthly-screening", async (MonthlyMoistureScreeningInput input, CommandExecutor commands, CancellationToken ct) =>
        { await commands.Authorize("technical-results", true, ct); return Results.Ok(MonthlyMoistureScreening.Calculate(input)); }).WithTags("technical");
        api.MapPost("/technical/thermal/parallel", async (ParallelThermalInput input, CommandExecutor commands, CancellationToken ct) =>
        { await commands.Authorize("technical-results", true, ct); return Results.Ok(ThermalCalculator.CalculateParallelPaths(input)); }).WithTags("technical");
        api.MapPost("/technical/roof/rectangle", async (RectangularRoofInput input, CommandExecutor commands, CancellationToken ct) =>
        { await commands.Authorize("technical-results", true, ct); return Results.Ok(RoofGeometry.Rectangle(input)); }).WithTags("technical");
        api.MapPost("/technical/roof/polygon", async (PolygonRoofInput input, CommandExecutor commands, CancellationToken ct) =>
        { await commands.Authorize("technical-results", true, ct); return Results.Ok(RoofGeometry.Calculate(input)); }).WithTags("technical");
        api.MapPost("/technical/thermal-bridges", async (ThermalBridgeInput input, CommandExecutor commands, CancellationToken ct) =>
        { await commands.Authorize("technical-results", true, ct); return Results.Ok(ThermalBridgeCalculator.Aggregate(input)); }).WithTags("technical");

        api.MapPost("/roof-revisions/{id:guid}/publish", (Guid id, HttpContext http, EntityStore store, FirstBlickDbContext db, TenantScope scope, CommandExecutor commands) =>
            commands.Execute(http, "roof-revisions", "roof.publish", new { id }, async ct =>
            {
                var roof = await store.Get<RoofRevision>(id, ct); CommandExecutor.Match(http, roof.Revision);
                await GraphRules.Validate(roof, store, db, scope, ct);
                var previous = roof.PreviousRevisionId is { } predecessor ? await store.Get<RoofRevision>(predecessor, ct) : null;
                RoofWorkflow.Publish(roof, previous, DateTimeOffset.UtcNow); await store.Save(roof, "roof.publish", false, ct); return roof;
            })).WithTags("technical");
        api.MapPost("/roof-revisions/{id:guid}/new-version", (Guid id, RevisionRequest body, HttpContext http, EntityStore store, CommandExecutor commands) =>
            commands.Execute(http, "roof-revisions", "roof.revise", new { id, body }, async ct =>
            {
                var source = await store.Get<RoofRevision>(id, ct); CommandExecutor.Match(http, source.Revision);
                var draft = RoofWorkflow.CreateRevision(source, body.Reason, DateTimeOffset.UtcNow); await store.Save(draft, "roof.revise", true, ct); return draft;
            })).WithTags("technical");
        api.MapGet("/measurements/{id:guid}/preview", async (Guid id, EntityStore store, CommandExecutor commands, CancellationToken ct) =>
        {
            await commands.Authorize("measurements", false, ct); var measurement = await store.Get<Measurement>(id, ct);
            return Results.Ok(MeasurementWorkflow.Preview(measurement, await store.Get<RoofRevision>(measurement.RoofRevisionId, ct)));
        }).WithTags("technical");
        api.MapPost("/measurements/{id:guid}/freeze", (Guid id, HttpContext http, EntityStore store, FirstBlickDbContext db, TenantScope scope, CommandExecutor commands) =>
            commands.Execute(http, "measurements", "measurement.freeze", new { id }, async ct =>
            {
                var measurement = await store.Get<Measurement>(id, ct); CommandExecutor.Match(http, measurement.Revision);
                await GraphRules.Validate(measurement, store, db, scope, ct);
                MeasurementWorkflow.Freeze(measurement, await store.Get<RoofRevision>(measurement.RoofRevisionId, ct), DateTimeOffset.UtcNow);
                await store.Save(measurement, "measurement.freeze", false, ct); return measurement;
            })).WithTags("technical");
        api.MapPost("/measurements/{id:guid}/pdf", (Guid id, HttpContext http, FirstBlickDbContext db, TenantScope scope, EntityStore store, IObjectStore objects, CommandExecutor commands) =>
            commands.Execute(http, "measurements", "measurement.pdf", new { id }, async ct =>
            {
                var measurement = await store.Get<Measurement>(id, ct); CommandExecutor.Match(http, measurement.Revision);
                Guard.Require(measurement.Status == MeasurementStatus.Frozen && measurement.Snapshot is not null, "missing_snapshot", "Freeze the measurement before producing its evidence PDF.");
                measurement.Validate();
                var snapshot = measurement.Snapshot;
                var lines = new List<string> { $"Dachrevision: {snapshot.RoofRevisionId}, Version {snapshot.RoofVersion}", $"Dach-Digest: {snapshot.RoofDigest}", "Mengenverfahren 1.0.0; Einheiten und Abzüge sind positionsbezogen." };
                foreach (var line in snapshot.Lines)
                {
                    lines.Add($"{line.Description}: {line.Quantity} {line.Unit}"); lines.Add($"Quelle: {line.MeasurementSource}");
                    lines.Add($"Eingabe: {AppJson.Serialize(line.Input)} · Wiederholungen: {line.Repetitions}");
                    foreach (var deduction in line.Deductions) lines.Add($"Abzug: {deduction.Quantity} {line.Unit} – {deduction.Reason}");
                }
                lines.Add("Summen nach Einheit:"); foreach (var total in snapshot.Totals) lines.Add($"{total.Quantity} {total.Unit}");
                return await DocumentEndpoints.SaveGenerated(db, scope, objects, BusinessPdf.Technical(measurement.Title, snapshot.Digest, lines), $"Aufmass-{measurement.Id:N}.pdf", measurement.ProjectId, "measurements", id, snapshot.Digest, ct);
            })).WithTags("technical");
        api.MapPost("/technical/runs", (CalculationRequest body, HttpContext http, TenantScope scope, EntityStore store, FirstBlickDbContext db, CommandExecutor commands) =>
            commands.Execute(http, "technical-results", "technical.calculate", body, async ct =>
            {
                var project = await store.Get<Project>(body.ProjectId, ct);
                var calculation = TechnicalRecords.Create(scope.TenantId, body.ProjectId, project.BuildingId, body.RoofRevisionId, body.Title, body.Method, body.Input, scope.UserId, DateTimeOffset.UtcNow);
                await GraphRules.Validate(calculation, store, db, scope, ct);
                await store.Save(calculation, "technical.calculate", true, ct); return calculation;
            })).WithTags("technical");
        api.MapPost("/technical-results/{id:guid}/pdf", (Guid id, HttpContext http, FirstBlickDbContext db, TenantScope scope, EntityStore store, IObjectStore objects, CommandExecutor commands) =>
            commands.Execute(http, "technical-results", "technical.pdf", new { id }, async ct =>
            {
                var result = await store.Get<TechnicalRecord>(id, ct); CommandExecutor.Match(http, result.Revision); result.Validate();
                var lines = new List<string> { $"Methode: {result.Method}; Version {result.MethodVersion}", $"Berechnet am: {result.CalculatedAt:O}", $"Person: {result.CalculatedBy}", "Eingaben und Ergebnis:" };
                lines.AddRange(Flatten(result.Result));
                return await DocumentEndpoints.SaveGenerated(db, scope, objects, BusinessPdf.Technical(result.Title, result.Digest, lines), $"Berechnung-{result.Id:N}.pdf", result.ProjectId, "technical-results", id, result.Digest, ct);
            })).WithTags("technical");
    }

    private static IEnumerable<string> Flatten(JsonElement value, string path = "")
    {
        if (value.ValueKind == JsonValueKind.Object)
            return value.EnumerateObject().SelectMany(x => Flatten(x.Value, path.Length == 0 ? x.Name : path + "." + x.Name));
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray().SelectMany((x, i) => Flatten(x, $"{path}[{i + 1}]"));
        return [$"{path}: {(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText())}"];
    }
}
