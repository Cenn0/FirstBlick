using FirstBlick.Core.Business;
using FirstBlick.Core.Technical;

namespace FirstBlick.Core.Tests.Technical;

public sealed class PvRoofSetLayoutTests
{
    private static PvLayoutInput Plane(double width, double height, PvModuleOrientation orientation = PvModuleOrientation.Portrait)
        => new(width, height, 1, 1, 420, 0, 0, 0, 0, orientation, [],
            "Synthetic measured roof plane", "Synthetic module datasheet");

    private static PvRoofSetInput Sample() => new("Synthetic two-plane survey", [
        new("South", Plane(4, 2)),
        new("North", Plane(2, 2))
    ]);

    [Fact]
    public void Distinct_planes_are_calculated_and_summed_without_connecting_coordinates()
    {
        var result = PvRoofSetLayout.Calculate(Sample());
        Assert.Equal(12, result.InstalledModules);
        Assert.Equal(5040, result.NominalDcPowerWp);
        Assert.Equal(12, result.RoofPlaneAreaM2);
        Assert.Equal(12, result.InstalledModuleAreaM2);
        Assert.Equal(12, result.GridCandidateCount);
        Assert.Equal(0, result.ObstructionRejectedCount);
        Assert.Equal(2, result.Planes.Count);
        Assert.Equal(0, result.Planes[1].Layout.Placements[0].X);
        Assert.Equal("South", result.Planes[0].Name);
        Assert.False(result.StructureOrElectricalDesignApproved);
        Assert.False(result.Provenance.IsNormative);
        Assert.Equal("pv-roof-set-grid", result.Provenance.MethodId);
    }

    [Fact]
    public void Each_plane_retains_independent_obstacles_and_orientation()
    {
        var set = Sample() with { Planes = [
            new("South", Plane(4, 2) with { Exclusions = [new("Window", 0, 0, 1, 1)] }),
            new("North", Plane(3.5, 2.1, PvModuleOrientation.MostModules) with {
                ModuleHeightM = 1.6
            })
        ]};
        var result = PvRoofSetLayout.Calculate(set);
        Assert.Equal(11, result.InstalledModules);
        Assert.Equal(1, result.ObstructionRejectedCount);
        Assert.Equal(PvModuleOrientation.Landscape, result.Planes[1].Layout.SelectedOrientation);
        Assert.Equal(7, result.Planes[0].Layout.InstalledModules);
        Assert.Equal(4, result.Planes[1].Layout.InstalledModules);
    }

    [Fact]
    public void Trimming_is_stable_but_order_and_measured_inputs_change_evidence_digest()
    {
        var original = PvRoofSetLayout.Calculate(Sample());
        var trimmed = PvRoofSetLayout.Calculate(Sample() with { RoofSetSource = "  Synthetic two-plane survey  ",
            Planes = [new("  South  ", Plane(4, 2)), new("North", Plane(2, 2))] });
        Assert.Equal(original.Provenance.InputSha256, trimmed.Provenance.InputSha256);
        var changed = PvRoofSetLayout.Calculate(Sample() with {
            Planes = [new("South", Plane(4, 2) with { ModuleRatedPowerWp = 430 }),
                      new("North", Plane(2, 2))] });
        Assert.NotEqual(original.Provenance.InputSha256, changed.Provenance.InputSha256);
        var swapped = PvRoofSetLayout.Calculate(Sample() with { Planes = Sample().Planes.Reverse().ToArray() });
        Assert.NotEqual(original.Provenance.InputSha256, swapped.Provenance.InputSha256);
    }

    [Fact]
    public void Missing_duplicate_or_unbounded_planes_fail_closed()
    {
        var invalid = new PvRoofSetInput[] {
            Sample() with { RoofSetSource = " " },
            Sample() with { Planes = [] },
            Sample() with { Planes = [new("A", Plane(2, 2)), new(" a ", Plane(2, 2))] },
            Sample() with { Planes = [new(" ", Plane(2, 2))] },
            Sample() with { Planes = Enumerable.Range(0, 9).Select(i =>
                new PvRoofSetPlane("Plane " + i, Plane(1, 1))).ToArray() },
            Sample() with { Planes = [new("A", Plane(2, 2) with { ModuleWidthM = double.NaN })] }
        };
        foreach (var item in invalid)
            Assert.ThrowsAny<Exception>(() => PvRoofSetLayout.Calculate(item));
        Assert.Equal("pv_roof_plane", Assert.Throws<DomainException>(() =>
            PvRoofSetLayout.Calculate(Sample() with { Planes = [
                new("South", Plane(2, 2)), new(" SOUTH ", Plane(2, 2))] })).Code);
    }

    [Fact]
    public void Combined_candidate_limit_rejects_large_multi_plane_arrangements()
    {
        var input = new PvRoofSetInput("Synthetic multi-plane stress", [
            new("A", Plane(5, 5) with { ModuleWidthM = .1, ModuleHeightM = .1 }),
            new("B", Plane(5, 5) with { ModuleWidthM = .1, ModuleHeightM = .1 }),
            new("C", Plane(5, 5) with { ModuleWidthM = .1, ModuleHeightM = .1 }),
            new("D", Plane(5, 5) with { ModuleWidthM = .1, ModuleHeightM = .1 }),
            new("E", Plane(5, 5) with { ModuleWidthM = .1, ModuleHeightM = .1 })
        ]);
        Assert.Equal("pv_roof_set_budget", Assert.Throws<DomainException>(() =>
            PvRoofSetLayout.Calculate(input)).Code);
    }
}
