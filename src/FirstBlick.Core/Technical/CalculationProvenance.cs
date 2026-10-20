using System.Security.Cryptography;
using System.Text.Json;

namespace FirstBlick.Core.Technical;

public sealed record CalculationProvenance(
    string MethodId,
    string MethodVersion,
    string InputSha256,
    bool IsNormative,
    IReadOnlyList<string> Limitations);

internal static class CalculationEvidence
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static CalculationProvenance Create<T>(string methodId, T input, params string[] limitations)
        => new(methodId, "1.0.0",
            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input, JsonOptions))).ToLowerInvariant(),
            false, Array.AsReadOnly(limitations));
}

internal static class TechnicalInput
{
    public static double Range(double value, string field, double minimum, double maximum)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(field, $"{field} must be finite and between {minimum} and {maximum}.");
        return value;
    }

    public static double Positive(double value, string field, double maximum)
    {
        Range(value, field, 0, maximum);
        if (value == 0) throw new ArgumentOutOfRangeException(field, $"{field} must be greater than zero.");
        return value;
    }

    public static string Label(string? value, string field, int maximum = 200)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
            throw new ArgumentException($"{field} must contain 1 to {maximum} visible characters.", field);
        return value.Trim();
    }

    public static void Count<T>(IReadOnlyList<T>? values, string field, int minimum, int maximum)
    {
        if (values is null || values.Count < minimum || values.Count > maximum)
            throw new ArgumentException($"{field} must contain {minimum} to {maximum} entries.", field);
    }
}
