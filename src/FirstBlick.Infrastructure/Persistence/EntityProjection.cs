using System.Text.Json;
using FirstBlick.Core.Business;

namespace FirstBlick.Infrastructure.Persistence;

/// <summary>Shared deterministic indexes for transactional writes and verified canonical migration.</summary>
public static class EntityProjection
{
    public static string? UniqueKey(TenantEntity entity) => entity switch
    {
        CustomerCommunicationVoid v => $"communication:{v.CommunicationId:N}",
        VehicleLedgerReversal r => $"{r.SourceKind}:{r.SourceId:N}",
        EquipmentInspection i when i.Status == EquipmentInspectionStatus.Scheduled => $"equipment:{i.EquipmentId:N}:pending",
        MaintenanceVisit v => $"contract:{v.ContractId:N}:due:{v.DueDate:yyyy-MM-dd}",
        Order o => $"quote:{o.QuoteId}",
        CollectionCase c => $"invoice:{c.InvoiceId}",
        Invoice i when i.Status == InvoiceStatus.Issued => $"number:{i.Number}",
        Quote q when q.Status != QuoteStatus.Draft => $"number:{q.Number}",
        BankTransaction t => $"bank:{ContentDigest.Of(new { t.AccountReference, t.ExternalId })}",
        Employee e when e.UserId.HasValue => $"user:{e.UserId}",
        NotificationPreference p => $"user:{p.UserId}",
        SupplierInvoice i => $"supplier:{i.SupplierId}:number:{i.Number.Trim().ToUpperInvariant()}",
        RoofRevision r when r.PreviousRevisionId.HasValue => $"previous:{r.PreviousRevisionId}",
        MaterialDemand d when d.Status != MaterialDemandStatus.Cancelled => $"measurement:{d.Evidence.MeasurementId}:line:{d.Evidence.MeasurementLineId}:material:{d.MaterialId}",
        CatalogItem c when c.GaebOrigin is { } origin => $"gaeb:{origin.SourceSha256}:line:{ContentDigest.Of(origin.PositionKey)}",
        TradeSource s => $"sha256:{s.Sha256}",
        _ => null
    };

    public static string SearchText(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var value = string.Join(' ', doc.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String)
            .Select(p => p.Value.GetString()).Where(x => x is not null)).ToLowerInvariant();
        return value.Length > 12000 ? value[..12000] : value;
    }
}
