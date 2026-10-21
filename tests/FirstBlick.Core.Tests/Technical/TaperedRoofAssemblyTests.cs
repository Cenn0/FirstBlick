using FirstBlick.Core.Business;
using FirstBlick.Core.Technical;
using Xunit;

namespace FirstBlick.Core.Tests.Technical;

public sealed class TaperedRoofAssemblyTests
{
    private static TaperedBoardCatalog Catalog(string version = "v1") => new(
        "Synthetic manufacturer", "Sustainable roof wedge", version, "Synthetic test data, not a manufacturer approval",
        1, .5,
        [new("A", .08, .10, 4, 10), new("B", .10, .12, 2, 20)]);

    private static TaperedBoardLayoutInput Field(bool lowAtZero, TaperedBoardCatalog? catalog = null) => new(
        2, 1, .08, 2, TaperedFlowAxis.AlongLength, lowAtZero,
        "Synthetic independent roof survey", "External review of high line and both outer low edges",
        catalog ?? Catalog());

    private static TaperedRoofAssemblyInput Plan() => new(
        4, 1, "Plan revision A", "Synthetic roof coordinates and field boundaries", "External rainwater and structure review required",
        [new("West", 0, 0, Field(true)), new("East", 2, 0, Field(false))]);

    [Fact]
    public void Two_opposing_fields_form_a_continuous_ridge_and_share_only_identical_pack_stock()
    {
        var result = TaperedRoofAssembly.Calculate(Plan());
        Assert.Equal(4, result.RoofAreaM2, 9);
        Assert.Equal(.4, result.InstalledVolumeM3, 9);
        Assert.Equal(8, result.InstalledBoards);
        Assert.Equal(8, result.Placements.Count);
        Assert.Equal(new[] { "A", "B" }, result.Orders.Select(x => x.Quantity.Sku));
        Assert.Equal(new[] { 4, 4 }, result.Orders.Select(x => x.Quantity.UsedBoards));
        Assert.Equal(new[] { 1, 2 }, result.Orders.Select(x => x.Quantity.PacksToOrder));
        Assert.Equal(120m, result.TotalEstimatedPackCostEur); // Independent per-field order would cost 160.
        Assert.All(result.Placements, x =>
        {
            Assert.True(x.GlobalX >= 0 && x.GlobalX + x.Panel.LengthM <= 4);
            Assert.True(x.GlobalY >= 0 && x.GlobalY + x.Panel.WidthM <= 1);
        });
        Assert.False(result.CatalogIndependentlyVerified);
        Assert.False(result.DrainageOrStructureApproved);
        Assert.False(result.Provenance.IsNormative);
        Assert.Equal("manufacturer-catalog-multifield-tapered-board-layout", result.Provenance.MethodId);
    }

    [Fact]
    public void Field_submission_order_is_not_a_different_plan_but_revision_and_stock_evidence_are()
    {
        var original = Plan();
        var first = TaperedRoofAssembly.Calculate(original);
        var reversed = TaperedRoofAssembly.Calculate(original with { Fields = original.Fields.Reverse().ToArray() });
        Assert.Equal(first.Provenance.InputSha256, reversed.Provenance.InputSha256);
        var revised = TaperedRoofAssembly.Calculate(original with { PlanRevision = "Plan revision B" });
        Assert.NotEqual(first.Provenance.InputSha256, revised.Provenance.InputSha256);
        var differentSource = TaperedRoofAssembly.Calculate(original with
        {
            Fields = [original.Fields[0], original.Fields[1] with { Layout = Field(false, Catalog("v2")) }]
        });
        Assert.Equal(4, differentSource.Orders.Count);
        Assert.Equal(160m, differentSource.TotalEstimatedPackCostEur);
    }

    [Fact]
    public void Gaps_overlaps_and_step_jumps_prevent_any_composed_purchase_plan()
    {
        var plan = Plan();
        Assert.Throws<DomainException>(() => TaperedRoofAssembly.Calculate(plan with
        {
            Fields = [plan.Fields[0], plan.Fields[1] with { OriginX = 1.99 }]
        }));
        Assert.Throws<DomainException>(() => TaperedRoofAssembly.Calculate(plan with
        {
            Fields = [plan.Fields[0] with { Layout = Field(true) with { RoofLengthM = 1.99 } }, plan.Fields[1]]
        }));
        Assert.Throws<DomainException>(() => TaperedRoofAssembly.Calculate(plan with
        {
            Fields = [plan.Fields[0], plan.Fields[1] with { Layout = Field(true) }]
        }));
    }

    [Fact]
    public void Unknown_price_is_unknown_and_duplicate_field_ids_are_rejected()
    {
        var plan = Plan();
        var unknown = Catalog() with { Boards = Catalog().Boards.Select(x =>
            x.Sku == "B" ? x with { PricePerBoardEur = null } : x).ToArray() };
        var withoutPrice = TaperedRoofAssembly.Calculate(plan with
        {
            Fields = [plan.Fields[0] with { Layout = Field(true, unknown) },
                plan.Fields[1] with { Layout = Field(false, unknown) }]
        });
        Assert.Null(withoutPrice.TotalEstimatedPackCostEur);
        Assert.Throws<DomainException>(() => TaperedRoofAssembly.Calculate(plan with
        {
            Fields = [plan.Fields[0], plan.Fields[1] with { FieldId = "West" }]
        }));
    }
}
