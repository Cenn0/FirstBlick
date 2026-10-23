using System.Net;
using System.Text;
using System.Text.Json;
using FirstBlick.Api;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace FirstBlick.Api.Tests;

public sealed class OpenApiWireContractTests
{
    public sealed record Input(decimal Amount, decimal? Optional, IReadOnlyList<decimal> Parts,
        IReadOnlyDictionary<string, decimal?> Rates, decimal[][] Matrix);
    public sealed record Output(decimal Amount, decimal? Optional, IReadOnlyList<decimal> Parts,
        IReadOnlyDictionary<string, decimal?> Rates, decimal[][] Matrix);

    [Fact]
    public async Task Actual_HTTP_decimal_strings_match_output_schemas_and_number_inputs_remain_supported()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Services.AddFirstBlickOpenApi();
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new DecimalStringConverter()));
        await using var app = builder.Build();
        app.MapOpenApi();
        app.MapPost("/api/decimal", (Input input) => TypedResults.Ok(new Output(input.Amount, input.Optional, input.Parts, input.Rates, input.Matrix)));
        app.MapPost("/api/echo", (Input input) => TypedResults.Ok(input));
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var trailing = await client.GetAsync("/openapi/wire-v1.json/");
        // MapOpenApi accepts this alias; production limiters must cover it too.
        Assert.Equal(HttpStatusCode.OK, trailing.StatusCode);
        using var doubleTrailing = await client.GetAsync("/openapi/wire-v1.json//");
        Assert.NotEqual(HttpStatusCode.OK, doubleTrailing.StatusCode);
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/wire-v1.json"));
        var root = document.RootElement;
        var operation = root.GetProperty("paths").GetProperty("/api/decimal").GetProperty("post");
        var inputSchema = Resolve(root, operation.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema"));
        var outputSchema = Resolve(root, operation.GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema"));
        Assert.Equal(new[] { "number", "string" }, Types(inputSchema.GetProperty("properties").GetProperty("amount")));
        Assert.Equal(new[] { "string" }, Types(outputSchema.GetProperty("properties").GetProperty("amount")));
        Assert.Equal(new[] { "null", "string" }, Types(outputSchema.GetProperty("properties").GetProperty("optional")));
        var partsSchema = Resolve(root, outputSchema.GetProperty("properties").GetProperty("parts"));
        Assert.True(partsSchema.TryGetProperty("items", out var items), partsSchema.GetRawText());
        Assert.Equal(new[] { "string" }, Types(Resolve(root, items)));
        Assert.Equal(new[] { "null", "string" }, Types(outputSchema.GetProperty("properties").GetProperty("rates").GetProperty("additionalProperties")));
        Assert.Equal(new[] { "null", "number", "string" }, Types(inputSchema.GetProperty("properties").GetProperty("rates").GetProperty("additionalProperties")));
        Assert.Equal(new[] { "string" }, Types(outputSchema.GetProperty("properties").GetProperty("matrix").GetProperty("items").GetProperty("items")));
        var echo = root.GetProperty("paths").GetProperty("/api/echo").GetProperty("post");
        var echoInput = Resolve(root, echo.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema"));
        var echoOutput = Resolve(root, echo.GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema"));
        Assert.Equal(new[] { "number", "string" }, Types(echoInput.GetProperty("properties").GetProperty("amount")));
        Assert.Equal(new[] { "string" }, Types(echoOutput.GetProperty("properties").GetProperty("amount")));
        foreach (var body in new[] {
            "{\"amount\":\"9007199254740993.01\",\"optional\":null,\"parts\":[\"0.10\",0.20],\"rates\":{\"a\":\"0.05\",\"missing\":null},\"matrix\":[[\"0.01\",0.02]]}",
            "{\"amount\":9007199254740993.01,\"optional\":null,\"parts\":[0.10,\"0.20\"],\"rates\":{\"a\":0.05,\"missing\":null},\"matrix\":[[0.01,\"0.02\"]]}"
        })
        {
            using var response = await client.PostAsync("/api/decimal", new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var wire = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(JsonValueKind.String, wire.RootElement.GetProperty("amount").ValueKind);
            Assert.Equal("9007199254740993.01", wire.RootElement.GetProperty("amount").GetString());
            Assert.Equal(JsonValueKind.Null, wire.RootElement.GetProperty("optional").ValueKind);
            Assert.All(wire.RootElement.GetProperty("parts").EnumerateArray(), value => Assert.Equal(JsonValueKind.String, value.ValueKind));
            Assert.Equal("0.05", wire.RootElement.GetProperty("rates").GetProperty("a").GetString());
            Assert.Equal(JsonValueKind.Null, wire.RootElement.GetProperty("rates").GetProperty("missing").ValueKind);
            Assert.Equal("0.01", wire.RootElement.GetProperty("matrix")[0][0].GetString());
        }
    }

    private static string[] Types(JsonElement schema)
    {
        var type = schema.GetProperty("type");
        return (type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(x => x.GetString()!) : [type.GetString()!])
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    private static JsonElement Resolve(JsonElement root, JsonElement schema)
        => schema.TryGetProperty("$ref", out var reference)
            ? Resolve(root, root.GetProperty("components").GetProperty("schemas").GetProperty(reference.GetString()!.Split('/')[^1])) : schema;
}
