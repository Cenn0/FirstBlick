using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FirstBlick.Infrastructure.Persistence;

/// <summary>Preserves decimal input and output without routing exact amounts through a JavaScript binary float.</summary>
public sealed class DecimalStringConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetDecimal(out var number)) return number;
        if (reader.TokenType == JsonTokenType.String && decimal.TryParse(reader.GetString(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var text)) return text;
        throw new JsonException("Use an invariant decimal value without separators or exponent notation.");
    }
    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
}
