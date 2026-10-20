using System.Reflection;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FirstBlick.Api;

public static class BusinessEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        foreach (var (kind, type) in BusinessRegistry.Entities)
            typeof(BusinessEndpoints).GetMethod(nameof(MapResource), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(type).Invoke(null, [api, kind]);
        CommercialEndpoints.Map(api);
        FieldEndpoints.Map(api);
        api.MapGet("/dashboard", Dashboard);
    }

    private static void MapResource<T>(RouteGroupBuilder api, string kind) where T : TenantEntity
    {
        api.MapGet($"/{kind}", async (HttpContext http, FirstBlickDbContext db, TenantScope scope, EntityStore store, CommandExecutor commands, CancellationToken ct) =>
        {
            await commands.Authorize(kind, false, ct);
            var page = int.TryParse(http.Request.Query["page"], out var p) ? Math.Clamp(p, 1, 100_000) : 1;
            var pageSize = int.TryParse(http.Request.Query["pageSize"], out var s) ? Math.Clamp(s, 1, 100) : 30;
            var search = http.Request.Query["search"].ToString().Trim().ToLowerInvariant();
            Guard.Text(search, "Search", 200, false);
            var status = http.Request.Query["status"].ToString();
            var project = http.Request.Query["projectId"].ToString();
            var customer = http.Request.Query["customerId"].ToString();
            var timeZone = await db.Tenants.Where(x => x.Id == scope.TenantId).Select(x => x.TimeZone).SingleAsync(ct);
            var due = http.Request.Query["due"].ToString() == "today" ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(timeZone)).DateTime).ToString("yyyy-MM-dd") : "";
            var openOnly = kind == "invoices" && http.Request.Query["openOnly"].ToString() == "true";
            var visible = await RecordPolicy.VisibleIds(kind, scope, store, ct);
            var predicate = "\"TenantId\"=@tenant AND \"Kind\"=@kind AND NOT \"Archived\" AND (@search='' OR position(@search in \"SearchText\")>0) AND (@status='' OR \"Data\"->>'status'=@status) AND (@project='' OR \"Data\"->>'projectId'=@project) AND (@customer='' OR \"Data\"->>'customerId'=@customer) AND (@due='' OR (@kind='tasks' AND \"Data\"->>'dueDate'<=@due AND \"Data\"->>'status' NOT IN ('Done','Cancelled')) OR (@kind='maintenance-contracts' AND \"Data\"->>'nextDueDate'<=@due AND \"Data\"->>'active'='true' AND (\"Data\"->>'endDate' IS NULL OR \"Data\"->>'nextDueDate'<=\"Data\"->>'endDate'))) AND (@all OR \"Id\"=ANY(@visible))";
            if (openOnly) predicate += " AND \"Id\" IN (" + FinanceReadModel.OpenInvoiceIdsSql + ")";
            NpgsqlParameter[] Parameters() => [new("tenant", scope.TenantId), new("kind", kind), new("search", search), new("status", status), new("project", project), new("customer", customer), new("due", due), new("all", visible is null), new("visible", visible ?? Array.Empty<Guid>())];
            // Only source-code SQL fragments are composed here. Every request value remains a typed SQL parameter.
#pragma warning disable EF1002
            var total = await db.Database.SqlQueryRaw<long>($"SELECT count(*) AS \"Value\" FROM \"Entities\" WHERE {predicate}", Parameters()).SingleAsync(ct);
            var parameters = Parameters().Concat([new NpgsqlParameter("limit", pageSize), new NpgsqlParameter("offset", checked((page - 1) * pageSize))]).ToArray();
            // PostgreSQL removes full quote/invoice/form snapshots before sending list data to the API process.
            var data = await db.Database.SqlQueryRaw<string>($"SELECT (\"Data\" - 'snapshot' - 'contract' - 'lines' - 'deliveries' - 'fields' - 'values' || jsonb_build_object('net',COALESCE(\"Data\"->'snapshot'->'net',\"Data\"->'contract'->'net'),'gross',COALESCE(\"Data\"->'snapshot'->'gross',\"Data\"->'contract'->'gross')))::text AS \"Value\" FROM \"Entities\" WHERE {predicate} ORDER BY \"UpdatedAt\" DESC, \"Id\" LIMIT @limit OFFSET @offset", parameters).ToListAsync(ct);
#pragma warning restore EF1002
            return Results.Ok(new { items = data.Select(x => JsonSerializer.Deserialize<JsonElement>(x)), total, page, pageSize });
        }).WithName($"List_{kind}").WithTags(kind);

        api.MapGet($"/{kind}/{{id:guid}}", async (Guid id, HttpContext http, EntityStore store, TenantScope scope, CommandExecutor commands, CancellationToken ct) =>
        {
            await commands.Authorize(kind, false, ct);
            await RecordPolicy.EnsureVisible(kind, id, scope, store, ct);
            var stored = await store.Find(kind, id, ct);
            http.Response.Headers.ETag = $"\"{stored.Revision}\"";
            return Results.Content(stored.Data, "application/json");
        }).Produces<T>().WithName($"Get_{kind}").WithTags(kind);

        api.MapPost($"/{kind}", (T body, HttpContext http, EntityStore store, TenantScope scope, FirstBlickDbContext db, CommandExecutor commands) =>
            commands.Execute(http, kind, "create", body, async ct =>
            {
                EditRules.EnsureGenericCreate(body);
                body.Id = Guid.NewGuid(); body.TenantId = scope.TenantId; body.Revision = 1;
                body.CreatedAt = body.UpdatedAt = DateTimeOffset.UtcNow; body.Archived = false;
                await RecordPolicy.EnsureWrite(body, scope, store, db, ct);
                await GraphRules.Validate(body, store, db, scope, ct);
                await store.Save(body, "create", true, ct);
                return body;
            })).Produces<T>().WithName($"Create_{kind}").WithTags(kind);

        api.MapPut($"/{kind}/{{id:guid}}", (Guid id, T body, HttpContext http, EntityStore store, TenantScope scope, FirstBlickDbContext db, CommandExecutor commands) =>
            commands.Execute(http, kind, "update", body, async ct =>
            {
                var previous = await store.Get<T>(id, ct);
                CommandExecutor.Match(http, previous.Revision);
                await RecordPolicy.EnsureVisible(kind, id, scope, store, ct);
                if (previous is Employee employee && body is Employee changedEmployee && employee.UserId != changedEmployee.UserId)
                    Guard.Require(scope.Role == BusinessRole.ManagingDirector, "forbidden", "Only a managing director can change or remove a personal-account link.");
                EditRules.EnsureGenericUpdate(previous, body);
                body.Id = id; body.TenantId = scope.TenantId; body.Revision = previous.Revision;
                body.CreatedAt = previous.CreatedAt; body.Archived = previous.Archived;
                await RecordPolicy.EnsureWrite(body, scope, store, db, ct);
                await GraphRules.Validate(body, store, db, scope, ct);
                await store.Save(body, "update", false, ct);
                return body;
            })).Produces<T>().WithName($"Update_{kind}").WithTags(kind);

        api.MapDelete($"/{kind}/{{id:guid}}", (Guid id, HttpContext http, EntityStore store, TenantScope scope, FirstBlickDbContext db, CommandExecutor commands) =>
            commands.Execute(http, kind, "archive", new { id }, async ct =>
            {
                var entity = await store.Get<T>(id, ct); CommandExecutor.Match(http, entity.Revision);
                await RecordPolicy.EnsureVisible(kind, id, scope, store, ct);
                EditRules.EnsureArchivable(entity);
                Guard.Require(!await db.Links.AnyAsync(x => x.TargetKind == kind && x.TargetId == id, ct), "record_in_use", "This record is referenced by other work. Retain it in the project history.");
                Guard.Require(!await db.Documents.AnyAsync(x => (x.SourceKind == kind && x.SourceId == id) || (kind == "projects" && x.ProjectId == id), ct), "record_in_use", "Documents preserve this record in the project history.");
                entity.Archived = true; await store.Save(entity, "archive", false, ct); return entity;
            })).WithName($"Archive_{kind}").WithTags(kind);
    }

    private static async Task<IResult> Dashboard(TenantScope scope, FirstBlickDbContext db, EntityStore store, CancellationToken ct)
    {
        var counts = new Dictionary<string, long>();
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == scope.TenantId, ct);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(tenant.TimeZone)).DateTime).ToString("yyyy-MM-dd");
        foreach (var (key, kind) in new[] { ("projects", "projects"), ("quoteDrafts", "quotes"), ("tasksDue", "tasks"), ("openInvoices", "invoices"), ("employees", "employees"), ("maintenanceDue", "maintenance-contracts") })
        {
            if (!AccessRules.CanRead(scope.Role, kind) || !tenant.Modules.Contains(AccessRules.ModuleFor(kind))) continue;
            var visible = await RecordPolicy.VisibleIds(kind, scope, store, ct);
            var clause = key switch
            {
                "quoteDrafts" => "AND \"Data\"->>'status'='Draft'",
                "tasksDue" => "AND \"Data\"->>'dueDate'<=@today AND \"Data\"->>'status' NOT IN ('Done','Cancelled')",
                "maintenanceDue" => "AND \"Data\"->>'nextDueDate'<=@today AND \"Data\"->>'active'='true' AND (\"Data\"->>'endDate' IS NULL OR \"Data\"->>'nextDueDate'<=\"Data\"->>'endDate')",
                "openInvoices" => "AND \"Id\" IN (" + FinanceReadModel.OpenInvoiceIdsSql + ")",
                _ => ""
            };
            // The switch above chooses literal SQL; it never inserts client text into the command.
#pragma warning disable EF1002
            counts[key] = await db.Database.SqlQueryRaw<long>($"SELECT count(*) AS \"Value\" FROM \"Entities\" WHERE \"TenantId\"=@tenant AND \"Kind\"=@kind AND NOT \"Archived\" AND (@all OR \"Id\"=ANY(@visible)) {clause}",
                new NpgsqlParameter("tenant", scope.TenantId), new NpgsqlParameter("kind", kind), new NpgsqlParameter("all", visible is null),
                new NpgsqlParameter("visible", visible ?? Array.Empty<Guid>()), new NpgsqlParameter("today", today)).SingleAsync(ct);
#pragma warning restore EF1002
        }
        return Results.Ok(counts);
    }
}

internal static class EditRules
{
    public static void EnsureGenericCreate(TenantEntity e)
    {
        Guard.Require(e is not Order and not BankTransaction and not PaymentAllocation and not SupplierPaymentAllocation and not StockMovement and not FormSubmission and not TechnicalRecord,
            "command_required", "This record can only be created through its dedicated business command.");
        switch (e)
        {
            case Quote q: Guard.Require(q.Status == QuoteStatus.Draft && q.Snapshot is null && q.Presentation is null && q.Deliveries.Count == 0 && q.AcceptedAt is null && q.Number == "", "command_required", "Create a draft quote; issue and delivery are separate commands."); break;
            case Invoice i: Guard.Require(i.Status == InvoiceStatus.Draft && i.Snapshot is null && i.Number == "" && i.Kind != InvoiceKind.CreditNote && i.OriginalInvoiceId is null, "command_required", "Create a draft receivable; corrections and issuance use dedicated commands."); break;
            case ChangeOrder c: Guard.Require(c.Status == ChangeOrderStatus.Draft && c.Snapshot is null && c.AcceptedAt is null, "command_required", "Create a draft change order."); break;
            case PurchaseOrder p: Guard.Require(p.Status == PurchaseOrderStatus.Draft && p.OrderedDigest == "" && p.Number == "", "command_required", "Create a draft purchase order."); break;
            case MaterialReservation r: Guard.Require(r.ConsumedQuantity == 0 && r.ReleasedQuantity == 0, "command_required", "A new reservation cannot contain previous movements."); break;
            case FormDefinition f: Guard.Require(!f.Published && f.PublishedDigest == "", "command_required", "Create an unpublished form definition."); break;
            case SupplierInvoice i: Guard.Require(i.Status == SupplierInvoiceStatus.Draft && i.ApprovedAt is null && i.ApprovedBy is null && i.ApprovedDigest == "", "command_required", "Create an unapproved supplier invoice."); break;
            case TimeEntry t: Guard.Require(!t.Approved, "command_required", "Create unapproved time; approval uses its dedicated command."); break;
            case TrainingRecord t: Guard.Require(t.Status == TrainingRecordStatus.Draft && t.ReviewedAt is null && t.MentorFeedback == "", "command_required", "Create a draft training record without a review."); break;
            case MaintenanceContract m: Guard.Require(m.CompletedOccurrences == 0, "command_required", "A new maintenance contract cannot invent completed occurrences."); break;
            case RoofRevision r: Guard.Require(r.Status == RoofRevisionStatus.Draft && r.Snapshot is null && r.PublishedAt is null && r.Version == 1 && r.PreviousRevisionId is null, "command_required", "Create a first draft roof; subsequent versions use the revision command."); break;
            case Measurement m: Guard.Require(m.Status == MeasurementStatus.Draft && m.Snapshot is null && m.FrozenAt is null, "command_required", "Create a draft measurement; freezing uses its dedicated command."); break;
        }
    }
    public static void EnsureGenericUpdate(TenantEntity old, TenantEntity next)
    {
        Guard.Require(old.GetType() == next.GetType() && !old.Archived, "record_immutable", "This record cannot be edited.");
        Guard.Require(old is not Order and not BankTransaction and not PaymentAllocation and not SupplierPaymentAllocation and not StockMovement and not FormSubmission and not MaterialReservation and not Acceptance and not TechnicalRecord,
            "command_required", "Use the dedicated auditable command for this record.");
        switch (old)
        {
            case Quote q:
                Guard.Require(q.Status == QuoteStatus.Draft, "quote_immutable", "An issued quote is immutable; create a new version.");
                EnsureGenericCreate(next); break;
            case Invoice i:
                Guard.Require(i.Status == InvoiceStatus.Draft && ((Invoice)next).Status == InvoiceStatus.Draft && ((Invoice)next).Snapshot is null && ((Invoice)next).Kind == i.Kind && ((Invoice)next).OriginalInvoiceId == i.OriginalInvoiceId,
                    "invoice_immutable", "Issued invoices require a separate correction document."); break;
            case ChangeOrder c: Guard.Require(c.Status == ChangeOrderStatus.Draft, "change_immutable", "Accepted changes are immutable."); EnsureGenericCreate(next); break;
            case PurchaseOrder p: Guard.Require(p.Status == PurchaseOrderStatus.Draft, "purchase_immutable", "Ordered quantities and prices are immutable."); EnsureGenericCreate(next); break;
            case FormDefinition f: Guard.Require(!f.Published, "form_immutable", "Published forms require a new version."); EnsureGenericCreate(next); break;
            case TimeEntry t: Guard.Require(!t.Approved && !((TimeEntry)next).Approved, "time_immutable", "Approved time requires an explicit correction; approval uses its dedicated command."); break;
            case TrainingRecord t: Guard.Require(t.Status == TrainingRecordStatus.Draft, "training_immutable", "Submitted training records are sealed; review uses its dedicated command."); EnsureGenericCreate(next); break;
            case SupplierInvoice i: Guard.Require(i.Status == SupplierInvoiceStatus.Draft, "supplier_invoice_immutable", "Approved supplier invoices require separate corrections."); EnsureGenericCreate(next); break;
            case MaintenanceContract m: Guard.Require(((MaintenanceContract)next).CompletedOccurrences == m.CompletedOccurrences && (m.CompletedOccurrences == 0 || ((MaintenanceContract)next).StartDate == m.StartDate), "command_required", "Maintenance occurrence history uses the completion command."); break;
            case RoofRevision r:
                var nextRoof = (RoofRevision)next;
                Guard.Require(r.Status == RoofRevisionStatus.Draft && nextRoof.Status == RoofRevisionStatus.Draft && nextRoof.Snapshot is null && nextRoof.PublishedAt is null && r.Version == nextRoof.Version && r.PreviousRevisionId == nextRoof.PreviousRevisionId,
                    "roof_immutable", "Published roofs require a new draft revision; predecessor and version cannot be edited."); break;
            case Measurement m: Guard.Require(m.Status == MeasurementStatus.Draft, "measurement_immutable", "Frozen measurements retain their input evidence."); EnsureGenericCreate(next); break;
        }
    }
    public static void EnsureArchivable(TenantEntity e)
    {
        Guard.Require(e is not Order and not Invoice and not SupplierInvoice and not PaymentAllocation and not SupplierPaymentAllocation and not BankTransaction and not StockMovement and not MaterialReservation and not FormSubmission and not Acceptance and not TechnicalRecord,
            "record_immutable", "Business evidence is retained; use a correction or reversal.");
        if (e is Quote q) Guard.Require(q.Status == QuoteStatus.Draft, "quote_immutable", "Issued quote versions are retained.");
        if (e is TimeEntry t) Guard.Require(!t.Approved, "time_immutable", "Approved time is retained.");
        if (e is RoofRevision r) Guard.Require(r.Status == RoofRevisionStatus.Draft, "roof_immutable", "Published roof evidence is retained.");
        if (e is Measurement m) Guard.Require(m.Status == MeasurementStatus.Draft, "measurement_immutable", "Frozen measurement evidence is retained.");
    }
}
