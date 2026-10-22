using System.Text.Json;
using System.Text.Json.Serialization;
using FirstBlick.Core.Technical;

namespace FirstBlick.TechnicalFixtures;

public static class Fixtures
{
    public static JsonSerializerOptions JsonOptions => new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() }
    };
    public static object Create()
    {
        var scope = new MoistureApplicability(true, true, true, true, true, true,
            "Synthetic diffusion-only fixture; no real building approval", "Synthetic calendar; no site climate data");
        var climate = Enumerable.Range(1, 12).Select(m => new MoistureDesignMonth(m, 20,
            m is >= 5 and <= 9 ? 20 : -5, 60, 70)).ToArray();
        var balance = new Iso13788BalanceInput(2024, 10, MoistureHeatFlow.Upward,
            MoistureExteriorBoundary.OutsideAir, MoistureClimateTreatment.WallAir, false,
            [new("Inner", .1, .1, MoistureMaterialKind.HomogeneousLayer, "Synthetic inner layer"),
             new("Insulation", 3, 1, MoistureMaterialKind.HomogeneousLayer, "Synthetic insulation"),
             new("Outer", .1, 10, MoistureMaterialKind.HomogeneousLayer, "Synthetic outer layer")], climate, scope);
        var dryAssembly = balance with {
            Layers = [new("Single homogeneous layer", 1, 1, MoistureMaterialKind.HomogeneousLayer, "Synthetic dry layer")],
            Months = climate.Select(m => m with { OutsideAirTemperatureC = 20, OutsideRelativeHumidityPercent = 40,
                InsideRelativeHumidityPercent = 40 }).ToArray()
        };
        var drying = new Iso13788DryingInput(dryAssembly, 0, 2, "Synthetic 1 kg comparison; no measured construction moisture");
        var catalog = new TaperedBoardCatalog("Synthetic manufacturer", "Synthetic wedges", "Test v1",
            "Synthetic catalogue; no supplier approval", 1, .5,
            [new("A", .08, .10, 4, 10), new("B", .10, .12, 2, 20)]);
        var boards = new TaperedBoardLayoutInput(1, 1.5, .08, 2, TaperedFlowAxis.AlongWidth, false,
            "Synthetic survey", "Independent drainage review required", catalog);
        var field = boards with { RoofLengthM = 2, RoofWidthM = 1, FlowAxis = TaperedFlowAxis.AlongLength,
            MeasuredRoofSource = "Synthetic field survey", DrainageDesignReview = "Drainage review required" };
        var assembly = new TaperedRoofAssemblyInput(4, 1, "Synthetic plan v1", "Synthetic field survey", "Drainage review required",
            [new("West", 0, 0, field with { LowEdgeAtCoordinateZero = true }), new("East", 2, 0, field)]);
        var unknown = boards with { Catalog = catalog with { Boards = catalog.Boards.Select(b => b with { PricePerBoardEur = null }).ToArray() } };
        return new Dictionary<string, object> {
            ["balance"] = new { mode = "balance", input = balance, result = Iso13788Balance.Calculate(balance) },
            ["residual"] = new { mode = "balance", input = balance with { Months = climate.Select(m => m with { OutsideAirTemperatureC = -5 }).ToArray() },
                result = Iso13788Balance.Calculate(balance with { Months = climate.Select(m => m with { OutsideAirTemperatureC = -5 }).ToArray() }) },
            ["drying"] = new { mode = "drying", input = drying, result = Iso13788Balance.Dry(drying) },
            ["boards"] = new { mode = "boards", input = boards, result = TaperedBoardLayout.Calculate(boards) },
            ["unknown"] = new { mode = "boards", input = unknown, result = TaperedBoardLayout.Calculate(unknown) },
            ["assembly"] = new { mode = "assembly", input = assembly, result = TaperedRoofAssembly.Calculate(assembly) }
        };
    }
}
