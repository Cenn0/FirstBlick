using System.Text.Json;
using System.Text;
using System.Text.Json.Serialization;
using FirstBlick.Core.Technical;

namespace FirstBlick.Core.Business;

public sealed class TechnicalRecord : TenantEntity
{
    public Guid ProjectId { get; set; }
    public Guid BuildingId { get; set; }
    public Guid? RoofRevisionId { get; set; }
    public string Title { get; set; } = "";
    public string Method { get; set; } = "";
    public string MethodVersion { get; set; } = "1.0.0";
    public JsonElement Input { get; set; }
    public JsonElement Result { get; set; }
    public Guid CalculatedBy { get; set; }
    public DateTimeOffset CalculatedAt { get; set; }
    public string Digest { get; set; } = "";
    public bool IsNormative { get; set; }

    public override void Validate()
    {
        ValidateCommon(); Guard.Id(ProjectId, nameof(ProjectId)); Guard.Id(BuildingId, nameof(BuildingId)); Guard.OptionalId(RoofRevisionId, nameof(RoofRevisionId));
        Guard.Text(Title, nameof(Title)); Guard.Id(CalculatedBy, nameof(CalculatedBy)); Guard.Utc(CalculatedAt, nameof(CalculatedAt));
        Guard.Require(!IsNormative && MethodVersion == "1.0.0", "unsupported_method", "These versioned methods are calculation aids and cannot be labeled normative.");
        Guard.Require(Input.ValueKind == JsonValueKind.Object && Result.ValueKind == JsonValueKind.Object, "missing_calculation", "Calculation inputs and results are required.");
        var expected = TechnicalRecords.Evaluate(Method, Input);
        Guard.Require(TechnicalRecords.Canonical(Result) == TechnicalRecords.Canonical(expected) && Digest == TechnicalRecords.Digest(this),
            "calculation_changed", "The stored calculation does not match its inputs, method version and evidence digest.");
    }
    public override IEnumerable<EntityReference> References()
    {
        yield return BusinessRegistry.Reference<Project>(ProjectId);
        yield return BusinessRegistry.Reference<Building>(BuildingId);
        if (RoofRevisionId is { } id) yield return BusinessRegistry.Reference<RoofRevision>(id);
    }
}

public static class TechnicalRecords
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { MaxDepth = 32, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static JsonElement Evaluate(string method, JsonElement input)
    {
        Guard.Require(input.ValueKind == JsonValueKind.Object, "invalid_input", "The method requires an input object.");
        object result = method switch
        {
            "thermal" => ThermalCalculator.Calculate(input.Deserialize<ThermalInput>(Options)!),
            "thermal.parallel" => ThermalCalculator.CalculateParallelPaths(input.Deserialize<ParallelThermalInput>(Options)!),
            "roof.rectangle" => RoofGeometry.Rectangle(input.Deserialize<RectangularRoofInput>(Options)!),
            "roof.polygon" => RoofGeometry.Calculate(input.Deserialize<PolygonRoofInput>(Options)!),
            "thermal-bridges" => ThermalBridgeCalculator.Aggregate(input.Deserialize<ThermalBridgeInput>(Options)!),
            _ => throw new DomainException("unsupported_method", "This calculation method has not been implemented and verified.")
        };
        return JsonSerializer.SerializeToElement(result, result.GetType(), Options);
    }

    public static TechnicalRecord Create(Guid tenantId, Guid projectId, Guid buildingId, Guid? roofId, string title, string method, JsonElement input, Guid actor, DateTimeOffset now)
    {
        var result = Evaluate(method, input);
        var record = new TechnicalRecord { TenantId = tenantId, ProjectId = projectId, BuildingId = buildingId, RoofRevisionId = roofId, Title = title, Method = method,
            Input = result.GetProperty("input").Clone(), Result = result, CalculatedBy = actor, CalculatedAt = now, CreatedAt = now, UpdatedAt = now };
        record.Digest = Digest(record); record.Validate(); return record;
    }

    public static string Digest(TechnicalRecord record) => ContentDigest.Of(new
    {
        record.TenantId, record.Id, record.ProjectId, record.BuildingId, record.RoofRevisionId, record.Title, record.Method, record.MethodVersion,
        record.CalculatedBy, record.CalculatedAt, input = Canonical(record.Input), result = Canonical(record.Result), record.IsNormative
    });

    public static string Canonical(JsonElement element)
    {
        // These method contracts contain finite IEEE-754 doubles and bounded integer
        // counts. Rewriting their numeric values removes PostgreSQL JSONB's harmless
        // exponent/decimal and negative-zero spelling changes. Do not use for money.
        static void Write(Utf8JsonWriter writer, JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                    { writer.WritePropertyName(property.Name); Write(writer, property.Value); }
                    writer.WriteEndObject(); break;
                case JsonValueKind.Array:
                    writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(writer, item); writer.WriteEndArray(); break;
                case JsonValueKind.Number:
                    var number = value.GetDouble();
                    Guard.Require(double.IsFinite(number), "invalid_number", "Technical evidence requires finite numeric values.");
                    writer.WriteNumberValue(number == 0 ? 0d : number); break;
                default: value.WriteTo(writer); break;
            }
        }
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) Write(writer, element);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
