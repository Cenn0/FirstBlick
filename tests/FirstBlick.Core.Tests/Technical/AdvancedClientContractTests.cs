using System.Text.Json;
using System.Text.Json.Nodes;
using FirstBlick.TechnicalFixtures;

namespace FirstBlick.Core.Tests.Technical;

public sealed class AdvancedClientContractTests
{
    [Fact]
    public void Client_and_browser_fixtures_are_exact_current_core_outputs_not_hand_written_answers()
    {
        var expected = JsonSerializer.SerializeToNode(Fixtures.Create(), Fixtures.JsonOptions);
        var retained = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "advanced-technical.json")));
        Assert.True(JsonNode.DeepEquals(expected, retained),
            "Regenerate web/tests/fixtures/advanced-technical.json with the explicit TechnicalFixtures tool and review the changed contracts.");
    }
}
