using FirstBlick.Core.Technical;
using FirstBlick.Infrastructure.Persistence;

namespace FirstBlick.Api;

/// <summary>Explicitly bounded calculations. No normative assessment is inferred from a successful request.</summary>
public static class TechnicalEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/technical/thermal", async (ThermalInput input, CommandExecutor commands, CancellationToken ct) =>
        { await commands.Authorize("technical-results", true, ct); return Results.Ok(ThermalCalculator.Calculate(input)); }).WithTags("technical");
        api.MapPost("/technical/thermal/parallel", async (ParallelThermalInput input, CommandExecutor commands, CancellationToken ct) =>
        { await commands.Authorize("technical-results", true, ct); return Results.Ok(ThermalCalculator.CalculateParallelPaths(input)); }).WithTags("technical");
        api.MapPost("/technical/roof/rectangle", async (RectangularRoofInput input, CommandExecutor commands, CancellationToken ct) =>
        { await commands.Authorize("technical-results", true, ct); return Results.Ok(RoofGeometry.Rectangle(input)); }).WithTags("technical");
        api.MapPost("/technical/roof/polygon", async (PolygonRoofInput input, CommandExecutor commands, CancellationToken ct) =>
        { await commands.Authorize("technical-results", true, ct); return Results.Ok(RoofGeometry.Calculate(input)); }).WithTags("technical");
        api.MapPost("/technical/thermal-bridges", async (ThermalBridgeInput input, CommandExecutor commands, CancellationToken ct) =>
        { await commands.Authorize("technical-results", true, ct); return Results.Ok(ThermalBridgeCalculator.Aggregate(input)); }).WithTags("technical");
    }
}
