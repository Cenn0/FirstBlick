using System.Text.Json;
using System.Text.Json.Nodes;
using FirstBlick.Core.Business;
using static FirstBlick.Api.Tests.PostgresApiFixture;

namespace FirstBlick.Api.Tests;

public sealed class AdvancedTechnicalWireApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    [PostgresApiFact]
    public async Task All_client_fixtures_match_actual_preview_wire_and_retained_project_JSON()
    {
        var tenant = await fixture.CreateTenant(); var actor = await fixture.CreateUser(tenant.Id);
        using var client = await fixture.Login(actor);
        var customer = await fixture.Save(new Customer { TenantId = tenant.Id, Name = "Synthetic contract customer" }, actor.UserId);
        var building = await fixture.Save(new Building { TenantId = tenant.Id, CustomerId = customer.Id,
            Name = "Synthetic contract building", Address = "Synthetic" }, actor.UserId);
        var project = await fixture.Save(new Project { TenantId = tenant.Id, CustomerId = customer.Id,
            BuildingId = building.Id, Name = "Synthetic contract project" }, actor.UserId);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "advanced-technical.json")));
        foreach (var scenario in document.RootElement.EnumerateObject())
        {
            var item = scenario.Value; var mode = item.GetProperty("mode").GetString();
            var (endpoint, method) = mode switch {
                "balance" => ("moisture/iso13788/balance", "moisture.iso13788.balance"),
                "drying" => ("moisture/iso13788/drying", "moisture.iso13788.drying"),
                "boards" => ("roof/tapered-board-layout", "roof.tapered-board-layout"),
                "assembly" => ("roof/tapered-assembly", "roof.tapered-assembly"),
                _ => throw new InvalidOperationException("Unsupported retained fixture")
            };
            using var preview = await Send(client, HttpMethod.Post, "/api/v1/technical/" + endpoint, item.GetProperty("input"));
            var previewJson = await preview.Content.ReadAsStringAsync(); Assert.True(preview.IsSuccessStatusCode, previewJson);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(item.GetProperty("result").GetRawText()), JsonNode.Parse(previewJson)), scenario.Name + " preview wire drift");
            using var saved = await Send(client, HttpMethod.Post, "/api/v1/technical/runs", new {
                projectId = project.Id, roofRevisionId = (Guid?)null, title = "Synthetic " + scenario.Name, method, input = item.GetProperty("input") });
            var savedJson = await saved.Content.ReadAsStringAsync(); Assert.True(saved.IsSuccessStatusCode, savedJson);
            using var record = JsonDocument.Parse(savedJson);
            var expected = item.TryGetProperty("persistedResult", out var retained) ? retained : item.GetProperty("result");
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected.GetRawText()), JsonNode.Parse(record.RootElement.GetProperty("result").GetRawText())), scenario.Name + " retained JSON drift");
            var stored = await fixture.Read<TechnicalRecord>(tenant.Id, record.RootElement.GetProperty("id").GetGuid());
            stored.Validate(); Assert.Equal(project.Id, stored.ProjectId);
        }
    }
}
