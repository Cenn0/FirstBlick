using System.Net;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Technical;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FirstBlick.Api.Tests.PostgresApiFixture;

namespace FirstBlick.Api.Tests;

public sealed class TechnicalWorkbenchApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private static MonthlyMoistureScreeningInput MonthlyInput() => new(.13, .04,
        [new("Synthetic insulation", .12, .04, 1, "Explicit synthetic input"), new("Synthetic exterior", .02, .2, 10000, "Explicit synthetic input")],
        Enumerable.Range(1, 12).Select(m => new MonthlyMoistureClimate(m, 20, m == 1 ? -10 : 20, m == 1 ? 90 : 50, 50)).Reverse().ToArray());

    private async Task<Project> Project(Guid tenantId, Guid userId)
    {
        var customer = await fixture.Save(new Customer { TenantId = tenantId, Name = "Synthetic customer" }, userId);
        var building = await fixture.Save(new Building { TenantId = tenantId, CustomerId = customer.Id, Name = "Synthetic building", Address = "Synthetic site" }, userId);
        return await fixture.Save(new Project { TenantId = tenantId, CustomerId = customer.Id, BuildingId = building.Id, Name = "Synthetic technical review" }, userId);
    }
    private static async Task<T> Ok<T>(Task<HttpResponseMessage> action)
    {
        using var response = await action; var json = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {json}"); return AppJson.Read<T>(json);
    }

    [PostgresApiFact]
    public async Task Monthly_screening_is_saved_as_normalized_replayable_non_normative_project_evidence()
    {
        var tenant = await fixture.CreateTenant(); var user = await fixture.CreateUser(tenant.Id); var client = await fixture.Login(user);
        var project = await Project(tenant.Id, user.UserId);
        var body = new { projectId = project.Id, roofRevisionId = (Guid?)null, title = "Synthetic independent monthly profiles", method = "moisture.monthly-screening", input = MonthlyInput() };
        var key = Guid.NewGuid().ToString("N");
        var record = await Ok<TechnicalRecord>(Send(client, HttpMethod.Post, "/api/v1/technical/runs", body, key: key));
        Assert.False(record.IsNormative); Assert.Equal(user.UserId, record.CalculatedBy); Assert.Equal(project.Id, record.ProjectId);
        Assert.Equal(Enumerable.Range(1, 12), record.Input.GetProperty("months").EnumerateArray().Select(x => x.GetProperty("month").GetInt32()));
        Assert.Equal(new[] { 1 }, record.Result.GetProperty("supersaturatedMonths").EnumerateArray().Select(x => x.GetInt32()));
        Assert.True(record.Result.GetProperty("requiresDetailedAssessment").GetBoolean());
        var stored = await fixture.Read<TechnicalRecord>(tenant.Id, record.Id); stored.Validate(); Assert.Equal(record.Digest, stored.Digest);
        using var replay = await Send(client, HttpMethod.Post, "/api/v1/technical/runs", body, key: key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode); Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(record.Id, AppJson.Read<TechnicalRecord>(await replay.Content.ReadAsStringAsync()).Id);
        await fixture.InScope(async sp => Assert.Equal(1, await sp.GetRequiredService<FirstBlickDbContext>().Entities.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenant.Id && x.Kind == "technical-results")));
    }

    [PostgresApiFact]
    public async Task Another_business_cannot_attach_a_result_to_the_first_business_project_or_read_its_report()
    {
        var first = await fixture.CreateTenant(); var second = await fixture.CreateTenant();
        var user = await fixture.CreateUser(first.Id); var a = await fixture.Login(user); var b = await fixture.Login(await fixture.CreateUser(second.Id));
        var project = await Project(first.Id, user.UserId);
        var body = new { projectId = project.Id, roofRevisionId = (Guid?)null, title = "Synthetic tapered volume", method = "roof.tapered-insulation", input = new TaperedInsulationInput(12, 10, .08, 0, 2, "Synthetic explicit reference") };
        var record = await Ok<TechnicalRecord>(Send(a, HttpMethod.Post, "/api/v1/technical/runs", body));
        using var refused = await Send(b, HttpMethod.Post, "/api/v1/technical/runs", body);
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        using var hidden = await b.GetAsync($"/api/v1/technical-results/{record.Id}"); Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        await fixture.InScope(async sp => Assert.Equal(0, await sp.GetRequiredService<FirstBlickDbContext>().Entities.IgnoreQueryFilters().CountAsync(x => x.TenantId == second.Id && x.Kind == "technical-results")));
    }
}
