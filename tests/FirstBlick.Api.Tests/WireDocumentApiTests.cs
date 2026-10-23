using System.Net;
using System.Text;
using System.Text.Json;

namespace FirstBlick.Api.Tests;

public sealed class WireDocumentApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    [PostgresApiFact]
    public async Task Both_documents_are_authenticated_and_wire_document_preserves_all_operations_and_resolvable_refs()
    {
        using var anonymous = fixture.Client();
        using var denied = await anonymous.GetAsync("/openapi/wire-v1.json");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        var tenant = await fixture.CreateTenant();
        using var client = await fixture.Login(await fixture.CreateUser(tenant.Id));
        using var response = await client.GetAsync("/openapi/wire-v1.json");
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        Assert.InRange(Encoding.UTF8.GetByteCount(text), 1, 8 * 1024 * 1024);
        using var wire = JsonDocument.Parse(text);
        using var legacy = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var expected = Operations(legacy.RootElement);
        var actual = Operations(wire.RootElement);
        Assert.NotEmpty(expected);
        Assert.Equal(expected, actual);
        CheckRefs(wire.RootElement, wire.RootElement);
        foreach (var property in wire.RootElement.GetProperty("paths").EnumerateObject())
        {
            if (!property.Name.StartsWith("/api/v1/technical/", StringComparison.Ordinal) || !property.Value.TryGetProperty("post", out var post)) continue;
            if (!post.TryGetProperty("operationId", out var id) || !id.GetString()!.StartsWith("Calculate", StringComparison.Ordinal)) continue;
            var error = post.GetProperty("responses").GetProperty("400").GetProperty("content").GetProperty("application/problem+json").GetProperty("schema");
            Assert.Contains("requestId", Properties(wire.RootElement, error));
        }
    }

    private static string[] Operations(JsonElement root)
        => root.GetProperty("paths").EnumerateObject().SelectMany(path => path.Value.EnumerateObject()
            .Where(method => method.Name is "get" or "post" or "put" or "patch" or "delete" or "head" or "options")
            .Select(method => method.Name + " " + path.Name)).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    private static JsonElement Resolve(JsonElement root, JsonElement schema)
        => schema.TryGetProperty("$ref", out var reference) ? Pointer(root, reference.GetString()!) : schema;

    private static JsonElement Pointer(JsonElement root, string pointer)
    {
        Assert.StartsWith("#/components/schemas/", pointer);
        return root.GetProperty("components").GetProperty("schemas").GetProperty(pointer["#/components/schemas/".Length..].Replace("~1", "/").Replace("~0", "~"));
    }

    private static void CheckRefs(JsonElement root, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
                if (property.Name == "$ref") Assert.NotEqual(JsonValueKind.Undefined, Pointer(root, property.Value.GetString()! ).ValueKind);
                else CheckRefs(root, property.Value);
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) CheckRefs(root, child);
    }

    private static HashSet<string> Properties(JsonElement root, JsonElement schema)
    {
        schema = Resolve(root, schema);
        var result = schema.TryGetProperty("properties", out var properties) ? properties.EnumerateObject().Select(x => x.Name).ToHashSet() : [];
        if (schema.TryGetProperty("allOf", out var all)) foreach (var part in all.EnumerateArray()) result.UnionWith(Properties(root, part));
        return result;
    }
}
