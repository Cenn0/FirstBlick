using System.Text.Json;
using System.Text.Json.Nodes;
using FirstBlick.Core.Business;
using FirstBlick.Infrastructure.Persistence;

namespace FirstBlick.Operations;

internal static class MigrationEntityJson
{
    public static string Serialize(TenantEntity entity, JsonElement source)
    {
        var json = AppJson.Serialize(entity);
        // Early v1 supplier invoices predate this nullable evidence property.
        // Preserve omission and explicit null as distinct historical source forms;
        // neither may gain a new hash during checking, importing or comparison.
        // This one compatibility rule never suppresses non-null evidence or any
        // other member. The complete typed JSON must still match the source hash.
        if (entity is not SupplierInvoice { ElectronicSource: null } || source.TryGetProperty("electronicSource", out _)) return json;
        var value = JsonNode.Parse(json)!.AsObject();
        value.Remove("electronicSource");
        return value.ToJsonString(AppJson.Options);
    }

    public static JsonElement Element(TenantEntity entity, JsonElement source)
    {
        using var document = JsonDocument.Parse(Serialize(entity, source));
        return document.RootElement.Clone();
    }
}
