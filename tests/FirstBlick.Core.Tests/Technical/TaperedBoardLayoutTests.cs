using FirstBlick.Core.Technical;
using FirstBlick.Core.Business;
using Xunit;

namespace FirstBlick.Core.Tests.Technical;

public sealed class TaperedBoardLayoutTests
{
    // Entirely synthetic supplier catalogue: no asserted real manufacturer/product data.
    private static TaperedBoardLayoutInput Input() => new(
        2.5, 1, .08, 2, TaperedFlowAxis.AlongLength, true,
        "Independently measured synthetic rectangular roof",
        "Drainage designer has checked the single, continuous low edge",
        new TaperedBoardCatalog("Synthetic manufacturer", "Test wedge family", "v1", "Fixture data — NOT a manufacturer's approval",
            1, .5, [
                new("C", .12, .14, 5, 30),
                new("A", .08, .10, 4, 10),
                new("B", .10, .12, 2, 20)
            ]));

    [Fact]
    public void Stock_wedges_exactly_cover_a_rectangular_roof_and_schedule_full_pack_purchases()
    {
        var result = TaperedBoardLayout.Calculate(Input());
        Assert.Equal(6, result.InstalledBoards);
        Assert.Equal(2.5, result.RoofAreaM2, 9);
        Assert.Equal(.13, result.HighEdgeM, 9);
        Assert.Equal(.2625, result.InstalledVolumeM3, 9);
        Assert.Equal(result.RequiredContinuousVolumeM3, result.InstalledVolumeM3, 9);
        Assert.Equal(.0675, result.CutWasteVolumeM3, 9);
        Assert.Equal(230, result.TotalEstimatedPackCostEur);
        Assert.Equal(new[] { "A", "B", "C" }, result.Orders.Select(x => x.Sku));
        Assert.Equal(new[] { 4, 2, 5 }, result.Orders.Select(x => x.BoardsOrdered));
        Assert.Equal(new[] { 2, 0, 3 }, result.Orders.Select(x => x.AdditionalBoardsFromPacks));
        Assert.Equal(2, result.Placements.Count(x => x.RequiresCut));
        Assert.All(result.Placements, x =>
        {
            Assert.InRange(x.X, 0, Input().RoofLengthM);
            Assert.InRange(x.Y, 0, Input().RoofWidthM);
            Assert.True(x.X + x.LengthM <= Input().RoofLengthM + 1e-9);
            Assert.True(x.Y + x.WidthM <= Input().RoofWidthM + 1e-9);
            Assert.True(x.InstalledVolumeM3 > 0);
        });
        Assert.False(result.CatalogIndependentlyVerified);
        Assert.False(result.DrainageOrStructureApproved);
        Assert.False(result.Provenance.IsNormative);
        Assert.Equal("manufacturer-catalog-tapered-board-layout", result.Provenance.MethodId);
        Assert.Equal(64, result.Provenance.InputSha256.Length);
    }

    [Fact]
    public void Rotated_and_reversed_roof_axes_retain_exact_volume_and_low_edge_location()
    {
        var original = Input();
        var rotated = original with { RoofLengthM = 1, RoofWidthM = 2.5,
            FlowAxis = TaperedFlowAxis.AlongWidth, LowEdgeAtCoordinateZero = false };
        var result = TaperedBoardLayout.Calculate(rotated);
        Assert.Equal(.2625, result.InstalledVolumeM3, 9);
        Assert.All(result.Placements, x => Assert.Equal(270, x.RotationDegrees));
        Assert.All(result.Placements.Where(x => x.Column == 1), x =>
        {
            Assert.Equal(1.5, x.Y, 9);
            Assert.Equal(.08, x.ThinEdgeM, 9);
        });
    }

    [Fact]
    public void Source_digest_changes_when_manufacturer_revision_or_product_values_change()
    {
        var first = TaperedBoardLayout.Calculate(Input());
        var revised = TaperedBoardLayout.Calculate(Input() with { Catalog = Input().Catalog with { CatalogRevision = "v2" } });
        Assert.NotEqual(first.Provenance.InputSha256, revised.Provenance.InputSha256);
    }

    [Fact]
    public void An_unpriced_supplier_board_makes_the_estimate_unknown_instead_of_zero()
    {
        var input = Input();
        var catalog = input.Catalog with { Boards = input.Catalog.Boards.Select(x => x.Sku == "B"
            ? x with { PricePerBoardEur = null } : x).ToArray() };
        var result = TaperedBoardLayout.Calculate(input with { Catalog = catalog });
        Assert.Null(result.TotalEstimatedPackCostEur);
        Assert.Null(result.Orders.Single(x => x.Sku == "B").EstimatedPackCostEur);
    }

    [Fact]
    public void Missing_exact_manufacturer_sku_prevents_any_silent_substitution()
    {
        var input = Input();
        var missing = input.Catalog with { Boards = input.Catalog.Boards.Where(x => x.Sku != "B").ToArray() };
        Assert.Throws<DomainException>(() => TaperedBoardLayout.Calculate(input with { Catalog = missing }));
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(0)]
    [InlineData(12)]
    [InlineData(double.NaN)]
    public void Slope_is_bounded(double slope) =>
        Assert.ThrowsAny<ArgumentException>(() => TaperedBoardLayout.Calculate(Input() with { SlopePercent = slope }));

    [Fact]
    public void A_differently_sloped_or_duplicate_catalog_does_not_yield_a_synthetic_usable_panel()
    {
        var input = Input();
        var changed = input.Catalog with { Boards = input.Catalog.Boards.Select(x =>
            x.Sku == "A" ? x with { ThickEdgeM = .105 } : x).ToArray() };
        Assert.Throws<DomainException>(() => TaperedBoardLayout.Calculate(input with { Catalog = changed }));
        var duplicate = input.Catalog with { Boards = [input.Catalog.Boards[0], input.Catalog.Boards[0]] };
        Assert.Throws<DomainException>(() => TaperedBoardLayout.Calculate(input with { Catalog = duplicate }));
    }

    [Fact]
    public void Oversized_roof_and_unsupported_total_thickness_fail_closed()
    {
        Assert.Throws<DomainException>(() => TaperedBoardLayout.Calculate(Input() with { RoofWidthM = 1000 }));
        Assert.Throws<DomainException>(() => TaperedBoardLayout.Calculate(Input() with { RoofLengthM = 100 }));
    }
}
