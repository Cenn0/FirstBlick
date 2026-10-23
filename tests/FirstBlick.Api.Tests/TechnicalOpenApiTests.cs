using System.Net;
using System.Text;
using System.Text.Json;
using static FirstBlick.Api.Tests.PostgresApiFixture;

namespace FirstBlick.Api.Tests;

public sealed class TechnicalOpenApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private static JsonElement Resolve(JsonElement document, JsonElement schema)
    {
        if (!schema.TryGetProperty("$ref", out var reference)) return schema;
        const string prefix = "#/components/schemas/";
        var value = reference.GetString()!; Assert.StartsWith(prefix, value);
        return Resolve(document, document.GetProperty("components").GetProperty("schemas")
            .GetProperty(value[prefix.Length..].Replace("~1", "/").Replace("~0", "~")));
    }
    private static HashSet<string> Properties(JsonElement document, JsonElement schema)
    {
        schema = Resolve(document, schema);
        var names = schema.TryGetProperty("properties", out var p)
            ? p.EnumerateObject().Select(x => x.Name).ToHashSet(StringComparer.Ordinal) : [];
        if (schema.TryGetProperty("allOf", out var all))
            foreach (var part in all.EnumerateArray()) names.UnionWith(Properties(document, part));
        return names;
    }
    private static JsonElement ContentSchema(JsonElement operation, string status, string mediaType)
        => operation.GetProperty("responses").GetProperty(status).GetProperty("content").GetProperty(mediaType).GetProperty("schema");

    [PostgresApiFact]
    public async Task Authenticated_document_retains_the_versioned_minimum_for_all_18_calculation_previews()
    {
        using var anonymous = fixture.Client();
        using var denied = await anonymous.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        var tenant = await fixture.CreateTenant(); using var client = await fixture.Login(await fixture.CreateUser(tenant.Id));
        using var response = await client.GetAsync("/openapi/v1.json");
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        using var json = JsonDocument.Parse(text); var document = json.RootElement;
        using var contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "technical-calculations-v1.json")));
        var minimum = contract.RootElement.GetProperty("operations").EnumerateArray().ToArray(); Assert.Equal(18, minimum.Length);
        foreach (var expected in minimum)
        {
            var operation = document.GetProperty("paths").GetProperty(expected.GetProperty("path").GetString()!).GetProperty("post");
            Assert.Equal("Calculate" + expected.GetProperty("responseSchema").GetString(), operation.GetProperty("operationId").GetString());
            Assert.True(operation.GetProperty("requestBody").GetProperty("required").GetBoolean());
            var input = operation.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema");
            Assert.EndsWith("/" + expected.GetProperty("requestSchema").GetString(), input.GetProperty("$ref").GetString());
            var output = ContentSchema(operation, "200", "application/json");
            Assert.EndsWith("/" + expected.GetProperty("responseSchema").GetString(), output.GetProperty("$ref").GetString());
            var properties = Properties(document, output);
            foreach (var name in expected.GetProperty("responseProperties").EnumerateArray()) Assert.Contains(name.GetString()!, properties);
            foreach (var status in new[] { "400", "403", "413", "415", "423", "500" })
            {
                var errors = Properties(document, ContentSchema(operation, status, "application/problem+json"));
                foreach (var field in new[] { "title", "detail", "status", "code", "traceId", "requestId" }) Assert.Contains(field, errors);
            }
            Assert.True(operation.GetProperty("responses").TryGetProperty("401", out _));
        }
    }

    [PostgresApiFact]
    public async Task Binding_errors_use_the_same_non_development_problem_shape_without_echoing_raw_json()
    {
        var tenant = await fixture.CreateTenant(); using var client = await fixture.Login(await fixture.CreateUser(tenant.Id));
        foreach (var (body, media, status) in new[] {
            ("{\"untrusted-secret-canary\":", "application/json", HttpStatusCode.BadRequest),
            ("{\"ridgeLengthM\":\"untrusted-secret-canary\",\"horizontalRunM\":4,\"riseM\":3}", "application/json", HttpStatusCode.BadRequest),
            ("untrusted-secret-canary", "text/plain", HttpStatusCode.UnsupportedMediaType)
        })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/technical/roof/rectangle")
                { Content = new StringContent(body, Encoding.UTF8, media) };
            using var response = await client.SendAsync(request);
            Assert.Equal(status, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var payload = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("untrusted-secret-canary", payload);
            using var json = JsonDocument.Parse(payload); var problem = json.RootElement;
            Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
            Assert.Equal("invalid_request", problem.GetProperty("code").GetString());
            Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
            Assert.Equal(Assert.Single(response.Headers.GetValues("X-Request-Id")), problem.GetProperty("requestId").GetString());
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
    }
}
