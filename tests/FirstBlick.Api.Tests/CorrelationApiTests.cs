using System.Net;
using System.Text.Json;

namespace FirstBlick.Api.Tests;

/// <summary>DP-02-07: correlation is a server-owned diagnostic, never a client authority token.</summary>
public sealed class CorrelationApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    [PostgresApiFact]
    public async Task Success_and_problem_responses_expose_a_server_owned_request_id()
    {
        var client = fixture.Client();
        client.DefaultRequestHeaders.Add("X-Request-Id", "caller-controlled-identifier");

        using var healthy = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
        var healthId = RequestId(healthy);
        Assert.NotEqual("caller-controlled-identifier", healthId);

        var tenant = await fixture.CreateTenant();
        var user = await fixture.CreateUser(tenant.Id);
        var loggedIn = await fixture.Login(user);
        loggedIn.DefaultRequestHeaders.Add("X-Request-Id", "caller-controlled-identifier");
        loggedIn.DefaultRequestHeaders.Remove("X-Tenant-Id");

        using var rejected = await loggedIn.GetAsync("/api/v1/customers");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var errorId = RequestId(rejected);
        Assert.NotEqual("caller-controlled-identifier", errorId);
        Assert.NotEqual(healthId, errorId);
        using var error = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        Assert.Equal("tenant_required", error.RootElement.GetProperty("code").GetString());
        Assert.Equal(errorId, error.RootElement.GetProperty("traceId").GetString());
        Assert.Equal("no-store", rejected.Headers.CacheControl?.ToString());
    }

    private static string RequestId(HttpResponseMessage response)
    {
        var id = Assert.Single(response.Headers.GetValues("X-Request-Id"));
        Assert.False(string.IsNullOrWhiteSpace(id));
        Assert.True(id.Length <= 128);
        return id;
    }
}
