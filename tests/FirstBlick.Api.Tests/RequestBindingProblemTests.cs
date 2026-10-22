using System.Net;
using System.Text;
using System.Text.Json;
using FirstBlick.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FirstBlick.Api.Tests;

public sealed class RequestBindingProblemTests
{
    public sealed record Input(double Amount);

    [Theory]
    [InlineData("Testing")]
    [InlineData("Production")]
    public async Task Non_development_JSON_and_empty_media_rejections_share_the_bounded_problem_contract(string environment)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Services.AddProblemDetails();
        builder.Services.PostConfigure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
        await using var app = builder.Build();
        app.UseMiddleware<ErrorMiddleware>();
        app.MapPost("/api/binding", (Input input) => Results.Ok(input));
        app.MapPost("/outside/binding", (Input input) => Results.Ok(input));
        app.MapPost("/api/custom", () => Results.Text("Explicit rejection", "text/plain", statusCode: 415));
        await app.StartAsync();
        Assert.True(app.Services.GetRequiredService<IOptions<RouteHandlerOptions>>().Value.ThrowOnBadRequest);
        using var client = app.GetTestClient();
        foreach (var (body, media, status) in new[] {
            ("{\"canary\":", "application/json", HttpStatusCode.BadRequest),
            ("{\"amount\":\"canary\"}", "application/json", HttpStatusCode.BadRequest),
            ("canary", "text/plain", HttpStatusCode.UnsupportedMediaType)
        })
        {
            using var response = await client.PostAsync("/api/binding", new StringContent(body, Encoding.UTF8, media));
            Assert.Equal(status, response.StatusCode);
            Assert.True(response.Content.Headers.ContentType?.MediaType == "application/problem+json", $"Media {media}, status {(int)response.StatusCode}: missing problem body");
            var text = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("canary", text);
            using var json = JsonDocument.Parse(text);
            Assert.Equal("invalid_request", json.RootElement.GetProperty("code").GetString());
        }
        using var valid = await client.PostAsync("/api/binding", new StringContent("{\"amount\":2}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        using var outside = await client.PostAsync("/outside/binding", new StringContent("canary", Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, outside.StatusCode);
        Assert.Null(outside.Content.Headers.ContentType);
        using var custom = await client.PostAsync("/api/custom", null);
        Assert.Equal("Explicit rejection", await custom.Content.ReadAsStringAsync());
        Assert.Equal("text/plain", custom.Content.Headers.ContentType?.MediaType);
    }
}
