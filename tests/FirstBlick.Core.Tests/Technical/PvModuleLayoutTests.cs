using FirstBlick.Core.Business;
using FirstBlick.Core.Technical;

namespace FirstBlick.Core.Tests.Technical;

public sealed class PvModuleLayoutTests
{
    private static PvLayoutInput Sample() => new(4, 2, 1, 1, 420, 0, 0, 0, 0,
        PvModuleOrientation.Portrait, [], "Synthetic roof-plane survey", "Uncertified module dimensions");

    [Fact]
    public void Exact_roof_plane_tiling_reports_nominal_power_and_traceable_coordinates()
    {
        var result = PvModuleLayout.Calculate(Sample());
        Assert.Equal(8, result.GridCandidateCount);
        Assert.Equal(8, result.InstalledModules);
        Assert.Equal(8, result.InstalledModuleAreaM2, 8);
        Assert.Equal(3360, result.NominalDcPowerWp);
        Assert.Equal(new[] { (1, 1, 0d, 0d), (1, 2, 1d, 0d), (1, 3, 2d, 0d), (1, 4, 3d, 0d),
            (2, 1, 0d, 1d), (2, 2, 1d, 1d), (2, 3, 2d, 1d), (2, 4, 3d, 1d) },
            result.Placements.Select(p => (p.Row, p.Column, p.X, p.Y)));
        Assert.False(result.StructureOrElectricalDesignApproved);
        Assert.False(result.Provenance.IsNormative);
        Assert.Equal(64, result.Provenance.InputSha256.Length);
    }

    [Fact]
    public void An_exclusion_removes_only_intersecting_modules_and_keeps_boundary_touch_valid()
    {
        var result = PvModuleLayout.Calculate(Sample() with {
            Exclusions = [new("Roof light", 1, 0, 1, 1)]
        });
        Assert.Equal(8, result.GridCandidateCount);
        Assert.Equal(1, result.ObstructionRejectedCount);
        Assert.Equal(7, result.InstalledModules);
        Assert.Contains(result.Placements, p => p.X == 0 && p.Y == 0);
        Assert.DoesNotContain(result.Placements, p => p.X == 1 && p.Y == 0);
    }

    [Fact]
    public void Exclusion_clearance_can_remove_adjacent_modules_without_relabeling_the_roof_area()
    {
        var result = PvModuleLayout.Calculate(Sample() with {
            Exclusions = [new("Vent", 1, 0, 1, 1)], ExclusionClearanceM = .1
        });
        Assert.Equal(8, result.RoofPlaneAreaM2);
        Assert.Equal(2, result.InstalledModules);
        Assert.Equal(6, result.ObstructionRejectedCount);
    }

    [Fact]
    public void Automatic_orientation_chooses_more_modules_and_is_stable_on_ties()
    {
        var input = Sample() with { RoofPlaneWidthM = 3.5, RoofPlaneHeightM = 2.1,
            ModuleWidthM = 1, ModuleHeightM = 1.6, Orientation = PvModuleOrientation.MostModules };
        var result = PvModuleLayout.Calculate(input);
        Assert.Equal(PvModuleOrientation.Landscape, result.SelectedOrientation);
        Assert.Equal(4, result.InstalledModules);
        Assert.Equal(PvModuleOrientation.Portrait, PvModuleLayout.Calculate(Sample() with {
            Orientation = PvModuleOrientation.MostModules }).SelectedOrientation);
    }

    [Fact]
    public void Gap_and_edge_clearances_reduce_count_and_preserve_measured_plane_coordinates()
    {
        var result = PvModuleLayout.Calculate(Sample() with {
            RoofPlaneWidthM = 4.1, RoofPlaneHeightM = 3.1, EdgeClearanceM = .1,
            ModuleGapXM = .1, ModuleGapYM = .1
        });
        Assert.Equal(6, result.InstalledModules);
        Assert.Equal(.1, result.Placements[0].X, 10);
        Assert.Equal(.1, result.Placements[0].Y, 10);
        Assert.Equal(2.3, result.Placements[^1].X, 10);
        Assert.Equal(1.2, result.Placements[^1].Y, 10);
    }

    [Fact]
    public void Too_small_roof_returns_explicit_empty_plan_instead_of_guessing_a_module()
    {
        var result = PvModuleLayout.Calculate(Sample() with { RoofPlaneWidthM = .5,
            RoofPlaneHeightM = .5 });
        Assert.Empty(result.Placements);
        Assert.Equal(0, result.NominalDcPowerWp);
    }

    [Fact]
    public void Invalid_sources_dimensions_locations_and_orientations_fail_closed()
    {
        PvLayoutInput[] invalid = [
            Sample() with { RoofPlaneWidthM = double.NaN },
            Sample() with { ModuleHeightM = double.PositiveInfinity },
            Sample() with { ModuleRatedPowerWp = 0 },
            Sample() with { RoofMeasurementSource = " " },
            Sample() with { ModuleDataSource = " " },
            Sample() with { EdgeClearanceM = -1 },
            Sample() with { Orientation = (PvModuleOrientation)100 },
            Sample() with { Exclusions = [new("Outside", 3.5, 0, 1, 1)] },
            Sample() with { Exclusions = [new("Invalid", 0, 0, -1, 1)] },
            Sample() with { Exclusions = [new("No source", 0, 0, 1, 1), new(" ", 1, 0, 1, 1)] }
        ];
        foreach (var input in invalid)
            Assert.ThrowsAny<Exception>(() => PvModuleLayout.Calculate(input));
        Assert.Equal("pv_orientation", Assert.Throws<DomainException>(() =>
            PvModuleLayout.Calculate(Sample() with { Orientation = (PvModuleOrientation)100 })).Code);
        Assert.Equal("pv_exclusion", Assert.Throws<DomainException>(() =>
            PvModuleLayout.Calculate(Sample() with { Exclusions = [new("Outside", 3.5, 0, 1, 1)] })).Code);
    }

    [Fact]
    public void Grid_budget_is_enforced_before_placement_or_obstruction_scanning()
    {
        var input = Sample() with { RoofPlaneWidthM = 100, RoofPlaneHeightM = 100,
            ModuleWidthM = .1, ModuleHeightM = .1 };
        Assert.Equal("pv_layout_budget", Assert.Throws<DomainException>(() => PvModuleLayout.Calculate(input)).Code);
    }

    [Fact]
    public void Normalized_sources_are_in_digest_and_can_be_recomputed()
    {
        var baseResult = PvModuleLayout.Calculate(Sample());
        var trimmed = PvModuleLayout.Calculate(Sample() with {
            RoofMeasurementSource = "  Synthetic roof-plane survey  " });
        var changed = PvModuleLayout.Calculate(Sample() with { ModuleRatedPowerWp = 430 });
        Assert.Equal(baseResult.Provenance.InputSha256, trimmed.Provenance.InputSha256);
        Assert.NotEqual(baseResult.Provenance.InputSha256, changed.Provenance.InputSha256);
    }
}
