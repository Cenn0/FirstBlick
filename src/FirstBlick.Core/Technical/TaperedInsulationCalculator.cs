namespace FirstBlick.Core.Technical;

/// <summary>
/// Rectangular horizontal plan area with a linearly varying insulation thickness.
/// Thicknesses are vertical dimensions above one common horizontal reference plane.
/// </summary>
public sealed record TaperedInsulationInput(double LengthM, double WidthM, double MinimumThicknessM,
    double LengthGradientPercent, double WidthGradientPercent, string MeasurementSource);

public sealed record TaperedInsulationCorner(double X, double Y, double ThicknessM);

public sealed record TaperedInsulationResult(TaperedInsulationInput Input, double ProjectedAreaM2,
    double MinimumThicknessM, double MaximumThicknessM, double AverageThicknessM,
    double CalculatedVolumeM3, IReadOnlyList<TaperedInsulationCorner> Corners,
    CalculationProvenance Provenance);

/// <summary>
/// Pure volume calculation for a rectangular, affine thickness field t(x,y) = t0 + gx*x + gy*y.
/// This is NOT a drainage design, thermal verification, or manufacturer-specific panel layout.
/// </summary>
public static class TaperedInsulationCalculator
{
    public static TaperedInsulationResult Calculate(TaperedInsulationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var length = TechnicalInput.Range(input.LengthM, nameof(input.LengthM), 0.01, 1000);
        var width = TechnicalInput.Range(input.WidthM, nameof(input.WidthM), 0.01, 1000);
        var min = TechnicalInput.Range(input.MinimumThicknessM, nameof(input.MinimumThicknessM), 0.01, 2);
        var alongLength = TechnicalInput.Range(input.LengthGradientPercent, nameof(input.LengthGradientPercent), 0, 10);
        var alongWidth = TechnicalInput.Range(input.WidthGradientPercent, nameof(input.WidthGradientPercent), 0, 10);
        var source = TechnicalInput.Label(input.MeasurementSource, nameof(input.MeasurementSource), 500);

        // The caller explicitly locates the thinnest point at (0,0).
        // Non-negative gradients prevent an undisclosed thinner or negative corner.
        var lengthRise = length * alongLength / 100;
        var widthRise = width * alongWidth / 100;
        var max = min + lengthRise + widthRise;
        if (max > 2)
            throw new ArgumentOutOfRangeException(nameof(input),
                "The maximum insulation thickness must not exceed 2 m.");

        var area = length * width;
        var mean = min + (lengthRise + widthRise) / 2;
        var volume = area * mean; // exact integral of the affine thickness over the horizontal plan
        var normalized = input with { MeasurementSource = source };
        var corners = Array.AsReadOnly(new[]
        {
            new TaperedInsulationCorner(0, 0, min),
            new TaperedInsulationCorner(length, 0, min + lengthRise),
            new TaperedInsulationCorner(length, width, max),
            new TaperedInsulationCorner(0, width, min + widthRise)
        });
        return new(normalized, area, min, max, mean, volume, corners,
            CalculationEvidence.Create("rectangular-linear-tapered-insulation", normalized,
                "Plan dimensions and vertical thicknesses are in metres; gradient percentages are vertical rise per horizontal run.",
                "The volume is the exact integral of a continuous affine thickness over an unobstructed rectangular horizontal plan.",
                "No roof openings, upstands, parapets, saddles, crickets, nonrectangular drainage geometry or cutting/waste allowance.",
                "No tapered board dimensions, stock sizes, adhesives, fixings, manufacturer tolerances, detailing or installation optimization.",
                "Does not determine drainage adequacy, wind uplift, structural load, hygrothermal suitability or DIN/EN compliance."));
    }
}
