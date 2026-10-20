using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FirstBlick.Core.Business;
using Microsoft.EntityFrameworkCore;

namespace FirstBlick.Infrastructure.Persistence;

public static class AppJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
        AllowOutOfOrderMetadataProperties = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, value?.GetType() ?? typeof(T), Options);
    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options) ?? throw new DomainException("invalid_json", "The request body is required.");
    public static TenantEntity Read(string kind, string json) => (TenantEntity)(JsonSerializer.Deserialize(json, BusinessRegistry.Entities[kind], Options) ?? throw new DomainException("invalid_json", "The request body is required."));
}

public sealed class EntityStore(FirstBlickDbContext db, TenantScope scope)
{
    public async Task<StoredEntity> Find(string kind, Guid id, CancellationToken ct = default) =>
        await db.Entities.SingleOrDefaultAsync(x => x.Kind == kind && x.Id == id, ct)
        ?? throw new DomainException("not_found", "The record does not exist or is not available in this business.");

    public async Task<T> Get<T>(Guid id, CancellationToken ct = default) where T : TenantEntity =>
        AppJson.Read<T>((await Find(BusinessRegistry.KindOf<T>(), id, ct)).Data);

    public async Task<List<T>> All<T>(CancellationToken ct = default) where T : TenantEntity
    {
        var kind = BusinessRegistry.KindOf<T>();
        var values = await db.Entities.Where(x => x.Kind == kind && !x.Archived).Select(x => x.Data).ToListAsync(ct);
        return values.Select(AppJson.Read<T>).ToList();
    }

    public async Task<StoredEntity> Save(TenantEntity entity, string action, bool isNew, CancellationToken ct = default)
    {
        Guard.Require(entity.TenantId == scope.TenantId, "forbidden", "The record belongs to a different business.");
        entity.Validate();
        var kind = BusinessRegistry.Entities.Single(x => x.Value == entity.GetType()).Key;
        var json = AppJson.Serialize(entity);
        Guard.Require(Encoding.UTF8.GetByteCount(json) <= 1_900_000, "payload_too_large", "The document exceeds the 1.9 MB snapshot budget.");
        var refs = entity.References().Distinct().ToArray();
        foreach (var reference in refs)
        {
            Guard.Require(await db.Entities.AnyAsync(x => x.Kind == reference.Kind && x.Id == reference.Id && !x.Archived, ct)
                || db.Entities.Local.Any(x => x.TenantId == scope.TenantId && x.Kind == reference.Kind && x.Id == reference.Id && !x.Archived),
                "invalid_reference", $"The referenced {reference.Kind} record is unavailable.");
        }
        var stored = isNew ? new StoredEntity { TenantId = scope.TenantId, Kind = kind, Id = entity.Id } : await Find(kind, entity.Id, ct);
        if (!isNew)
        {
            Guard.Require(entity.Revision == stored.Revision, "revision_conflict", "The record changed. Reload it before saving.");
            entity.Revision++;
            entity.CreatedAt = stored.CreatedAt;
        }
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        stored.Revision = entity.Revision;
        stored.CreatedAt = entity.CreatedAt;
        stored.UpdatedAt = entity.UpdatedAt;
        stored.Archived = entity.Archived;
        stored.Data = AppJson.Serialize(entity);
        stored.UniqueKey = entity switch
        {
            Order o => $"quote:{o.QuoteId}",
            Invoice i when i.Status == InvoiceStatus.Issued => $"number:{i.Number}",
            Quote q when q.Status != QuoteStatus.Draft => $"number:{q.Number}",
            BankTransaction t => $"bank:{ContentDigest.Of(new { t.AccountReference, t.ExternalId })}",
            Employee e when e.UserId.HasValue => $"user:{e.UserId}",
            NotificationPreference p => $"user:{p.UserId}",
            SupplierInvoice i => $"supplier:{i.SupplierId}:number:{i.Number.Trim().ToUpperInvariant()}",
            _ => null
        };
        using var doc = JsonDocument.Parse(stored.Data);
        stored.SearchText = string.Join(' ', doc.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String)
            .Select(p => p.Value.GetString()).Where(x => x is not null)).ToLowerInvariant();
        if (stored.SearchText.Length > 12000) stored.SearchText = stored.SearchText[..12000];
        if (isNew) db.Entities.Add(stored);
        else db.Links.RemoveRange(await db.Links.Where(x => x.SourceKind == kind && x.SourceId == entity.Id).ToListAsync(ct));
        // The transaction writes source, tenant-qualified references, audit, outbox and receipt together.
        foreach (var reference in refs)
        {
            var existing = db.Links.Local.FirstOrDefault(x => x.TenantId == scope.TenantId && x.SourceKind == kind && x.SourceId == entity.Id && x.TargetKind == reference.Kind && x.TargetId == reference.Id);
            if (existing is not null) db.Entry(existing).State = EntityState.Unchanged;
            else db.Links.Add(new EntityLink { TenantId = scope.TenantId, SourceKind = kind, SourceId = entity.Id, TargetKind = reference.Kind, TargetId = reference.Id });
        }
        db.Audit.Add(new AuditEntry { TenantId = scope.TenantId, ActorId = scope.UserId, Action = action, Kind = kind,
            EntityId = entity.Id, Revision = entity.Revision, ContentDigest = ContentDigest.OfUtf8(stored.Data),
            CorrelationId = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? "" });
        db.Jobs.Add(new OutboxJob { TenantId = scope.TenantId, ActorId = scope.UserId, Type = "entity.changed", Kind = kind,
            EntityId = entity.Id, EntityRevision = entity.Revision, Payload = AppJson.Serialize(new { action, revision = entity.Revision }) });
        return stored;
    }

    public async Task<string> NextNumber(string kind, DateOnly date, CancellationToken ct = default)
    {
        // The enclosing tenant advisory lock prevents concurrent number reuse, including first use of a year.
        var counter = await db.Counters.SingleOrDefaultAsync(x => x.Kind == kind && x.Year == date.Year, ct);
        if (counter is null) { counter = new NumberCounter { TenantId = scope.TenantId, Kind = kind, Year = date.Year }; db.Counters.Add(counter); }
        counter.Value++;
        return $"{kind}-{date.Year}-{counter.Value:D6}";
    }
}
