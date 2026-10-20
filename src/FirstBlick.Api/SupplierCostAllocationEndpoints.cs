using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FirstBlick.Api;

public sealed record PostSupplierProjectCostRequest(Guid ProjectId, decimal NetAmount, string Reason, string ExpectedSourceDigest);
public sealed record ReverseSupplierProjectCostRequest(string Reason);

/// <summary>
/// Manually authorized project allocations of approved external supplier costs.
/// Nothing is silently inferred from a purchase order or bank payment.
/// </summary>
public static class SupplierCostAllocationEndpoints
{
    private const int MaximumJournal = SupplierCostAllocationRules.MaximumJournalEntries;

    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/supplier-invoices/{id:guid}/cost-allocations", (Guid id, PostSupplierProjectCostRequest body,
            [AsParameters] BusinessCommandServices c) =>
            c.Execute("supplier-cost-allocations", "supplier-cost.allocate", body, async ct =>
            {
                await Authorize(c, true, ct);
                var invoice = await c.Current<SupplierInvoice>(id, ct, false);
                var project = await c.Current<Project>(body.ProjectId, ct, false);
                Guard.Require(invoice.ApprovedDigest == body.ExpectedSourceDigest, "stale_supplier_cost_source",
                    "The supplier document changed since the allocation was reviewed.");
                var journal = await Journal(c, [id], ct);
                var allocated = SupplierCostAllocationRules.Post(invoice, project, journal,
                    body.NetAmount, body.Reason, c.Scope.UserId, DateTimeOffset.UtcNow);
                if (invoice.Kind == SupplierInvoiceKind.CreditNote)
                {
                    var original = await c.Current<SupplierInvoice>(invoice.OriginalInvoiceId!.Value, ct, false);
                    Guard.Require(original.Kind == SupplierInvoiceKind.Invoice && original.Status == SupplierInvoiceStatus.Approved,
                        "invalid_correction", "Supplier credit requires its approved original invoice.");
                    var originalJournal = await Journal(c, [original.Id], ct);
                    var familyIds = await ApprovedCreditIds(c, original.Id, ct);
                    var familyJournal = await Journal(c, familyIds, ct);
                    SupplierCostAllocationRules.ValidateCreditProjectBalance(project.Id, body.NetAmount, originalJournal, familyJournal);
                }
                return await c.SaveNew(allocated, "supplier-cost.allocate", ct);
            })).Produces<SupplierCostAllocation>();

        api.MapPost("/supplier-cost-allocations/{id:guid}/reverse", (Guid id, ReverseSupplierProjectCostRequest body,
            [AsParameters] BusinessCommandServices c) =>
            c.Execute("supplier-cost-allocations", "supplier-cost.reverse", body, async ct =>
            {
                await Authorize(c, true, ct);
                var original = await c.Current<SupplierCostAllocation>(id, ct);
                var journal = await Journal(c, [original.SupplierInvoiceId], ct);
                var reversal = SupplierCostAllocationRules.Reverse(original, journal,
                    body.Reason, c.Scope.UserId, DateTimeOffset.UtcNow);
                // An original invoice cannot lose its project allocation while a linked,
                // approved credit still reduces that project's posted costs.
                var supplierInvoice = await c.Current<SupplierInvoice>(original.SupplierInvoiceId, ct, false);
                if (supplierInvoice.Kind == SupplierInvoiceKind.Invoice)
                {
                    var creditIds = await ApprovedCreditIds(c, supplierInvoice.Id, ct);
                    var creditJournal = await Journal(c, creditIds, ct);
                    SupplierCostAllocationRules.ValidateCreditProjectBalance(original.ProjectId, 0m,
                        [..journal, reversal], creditJournal);
                }
                return await c.SaveNew(reversal, "supplier-cost.reverse", ct);
            })).Produces<SupplierCostAllocation>();

        api.MapGet("/supplier-invoices/{id:guid}/cost-allocations", async (Guid id,
            [AsParameters] BusinessCommandServices c, CancellationToken ct) =>
        {
            await Authorize(c, false, ct);
            var invoice = await c.Current<SupplierInvoice>(id, ct, false);
            invoice.Validate();
            var journal = await Journal(c, [id], ct);
            var active = SupplierCostAllocationRules.Active(journal);
            c.Http.Response.Headers.CacheControl = "no-store, private";
            return Results.Ok(new { items = journal, active = active.Select(x => x.Id).ToArray(),
                allocatedNet = active.Sum(x => x.NetAmount), availableNet = invoice.Net - active.Sum(x => x.NetAmount) });
        }).WithTags("reports");

        api.MapGet("/projects/{id:guid}/supplier-cost-allocations", async (Guid id,
            [AsParameters] BusinessCommandServices c, CancellationToken ct) =>
        {
            await Authorize(c, false, ct);
            await c.Current<Project>(id, ct, false);
            var tenant = c.Scope.TenantId;
            var ids = c.Db.Links.Where(x => x.TenantId == tenant && x.SourceKind == "supplier-cost-allocations"
                && x.TargetKind == "projects" && x.TargetId == id).Select(x => x.SourceId);
            var rows = await c.Db.Entities.AsNoTracking().Where(x => x.TenantId == tenant
                && x.Kind == "supplier-cost-allocations" && !x.Archived && ids.Contains(x.Id))
                .OrderBy(x => x.Id).Take(MaximumJournal + 1).ToListAsync(ct);
            Guard.Require(rows.Count <= MaximumJournal, "cost_allocation_budget", "The project cost allocation history is too large.");
            var entries = Decode(rows, tenant);
            Guard.Require(entries.All(x => x.ProjectId == id), "cost_allocation_metadata",
                "The project cost allocation references do not match the stored journal.");
            var active = SupplierCostAllocationRules.Active(entries);
            c.Http.Response.Headers.CacheControl = "no-store, private";
            return Results.Ok(new { items = entries, active = active.Select(x => x.Id).ToArray(),
                grossPostedNet = active.Sum(x => x.NetAmount) });
        }).WithTags("reports");
    }

    private static async Task Authorize(BusinessCommandServices c, bool write, CancellationToken ct)
    {
        await c.Commands.Authorize("supplier-cost-allocations", write, ct);
        Guard.Require(c.Scope.Role is BusinessRole.ManagingDirector or BusinessRole.Office, "forbidden",
            "Supplier project-cost allocations require management or office authorization.");
    }

    private static async Task<Guid[]> ApprovedCreditIds(BusinessCommandServices c, Guid originalId, CancellationToken ct)
    {
        var tenant = c.Scope.TenantId;
        var ids = c.Db.Links.Where(x => x.TenantId == tenant && x.SourceKind == "supplier-invoices"
            && x.TargetKind == "supplier-invoices" && x.TargetId == originalId).Select(x => x.SourceId);
        var rows = await c.Db.Entities.AsNoTracking().Where(x => x.TenantId == tenant && x.Kind == "supplier-invoices"
            && !x.Archived && ids.Contains(x.Id))
            .OrderBy(x => x.Id).Take(MaximumJournal + 1).Select(x => x.Data).ToListAsync(ct);
        Guard.Require(rows.Count <= MaximumJournal, "cost_allocation_budget",
            "The supplier correction history is too large.");
        var credits = rows.Select(AppJson.Read<SupplierInvoice>).ToList();
        foreach (var credit in credits) credit.Validate();
        Guard.Require(credits.All(x => x.Kind == SupplierInvoiceKind.CreditNote && x.OriginalInvoiceId == originalId),
            "invalid_correction", "Supplier correction links do not match the original invoice.");
        return credits.Where(x => x.Status == SupplierInvoiceStatus.Approved).Select(x => x.Id).ToArray();
    }

    private static async Task<List<SupplierCostAllocation>> Journal(BusinessCommandServices c,
        IReadOnlyCollection<Guid> invoiceIds, CancellationToken ct)
    {
        if (invoiceIds.Count == 0) return [];
        var tenant = c.Scope.TenantId;
        var ids = c.Db.Links.Where(x => x.TenantId == tenant && x.SourceKind == "supplier-cost-allocations"
            && x.TargetKind == "supplier-invoices" && invoiceIds.Contains(x.TargetId)).Select(x => x.SourceId);
        var rows = await c.Db.Entities.AsNoTracking().Where(x => x.TenantId == tenant
            && x.Kind == "supplier-cost-allocations" && !x.Archived && ids.Contains(x.Id))
            .OrderBy(x => x.Id).Take(MaximumJournal + 1).ToListAsync(ct);
        Guard.Require(rows.Count <= MaximumJournal, "cost_allocation_budget",
            "The supplier cost allocation history exceeds the supported journal budget.");
        var entries = Decode(rows, tenant);
        Guard.Require(entries.All(x => invoiceIds.Contains(x.SupplierInvoiceId)), "cost_allocation_metadata",
            "The cost allocation source links do not match the stored journal.");
        SupplierCostAllocationRules.Active(entries); // validate reversal chain before any new posting
        return entries;
    }

    private static List<SupplierCostAllocation> Decode(List<StoredEntity> rows, Guid tenant)
    {
        var entries = new List<SupplierCostAllocation>(rows.Count);
        foreach (var row in rows)
        {
            var value = AppJson.Read<SupplierCostAllocation>(row.Data);
            Guard.Require(row.TenantId == tenant && value.TenantId == tenant
                && row.Id == value.Id && row.Kind == "supplier-cost-allocations"
                && row.Revision == value.Revision && row.Archived == value.Archived && !value.Archived,
                "cost_allocation_metadata", "Recorded cost allocation identity or revision has changed.");
            value.Validate(); entries.Add(value);
        }
        return entries;
    }
}
