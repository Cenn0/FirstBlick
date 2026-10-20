using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FirstBlick.Api;

public static class RequestFingerprint
{
    public const string ItemKey = "FirstBlick.RequestBodyDigest";
    public static async Task Capture(HttpContext http)
    {
        if (http.Request.ContentType?.Split(';')[0].Equals("application/json", StringComparison.OrdinalIgnoreCase) != true) return;
        const int limit = 2_097_152;
        Guard.Require(http.Request.ContentLength is null or <= limit, "payload_too_large", "JSON requests are limited to 2 MiB.");
        http.Request.EnableBuffering(bufferThreshold: 65536, bufferLimit: limit * 2L);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; long total = 0;
        while (true)
        {
            var count = await http.Request.Body.ReadAsync(buffer, http.RequestAborted); if (count == 0) break;
            total += count; Guard.Require(total <= limit, "payload_too_large", "JSON requests are limited to 2 MiB."); hash.AppendData(buffer, 0, count);
        }
        http.Items[ItemKey] = Convert.ToHexStringLower(hash.GetHashAndReset()); http.Request.Body.Position = 0;
    }
}

public sealed class ErrorMiddleware(RequestDelegate next, ILogger<ErrorMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try { await next(context); }
        catch (Exception e) when (!context.Response.HasStarted)
        {
            var (status, code, detail) = e switch
            {
                DomainException d => (d.Code switch { "not_found" => 404, "forbidden" or "module_disabled" => 403, "unauthorized" => 401,
                    "revision_required" => 428, "revision_conflict" or "idempotency_conflict" or "approval_required" or "approval_invalid" or "duplicate" => 409,
                    "payload_too_large" => 413, "storage_unconfigured" or "provider_unconfigured" => 503, _ => 400 }, d.Code, d.Message),
                AntiforgeryValidationException => (400, "csrf_invalid", "The session verification token is missing or expired. Reload the page."),
                DbUpdateConcurrencyException => (409, "revision_conflict", "The record changed. Reload it before saving."),
                DbUpdateException { InnerException: PostgresException { SqlState: "23505" } } => (409, "duplicate", "An equivalent record or number already exists."),
                DbUpdateException { InnerException: PostgresException { SqlState: "23503" } } => (409, "invalid_reference", "A required related record is unavailable."),
                PostgresException { SqlState: "40001" or "40P01" } => (409, "concurrent_change", "Another operation changed this business. Retry with the same request key."),
                JsonException => (400, "invalid_json", "A field, data type or enum value in the request is invalid."),
                BadHttpRequestException b => (b.StatusCode, "invalid_request", "The request is malformed or exceeds the supported budget."),
                ArgumentException a => (400, "invalid_input", a.Message),
                _ => (500, "server_error", "The operation failed. No successful result has been confirmed.")
            };
            if (status >= 500) logger.LogError("Request {TraceId} failed with {ExceptionType}", context.TraceIdentifier, e.GetType().Name);
            context.Response.Clear();
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
            await Results.Problem(statusCode: status, title: code, detail: detail,
                extensions: new Dictionary<string, object?> { ["code"] = code, ["traceId"] = context.TraceIdentifier }).ExecuteAsync(context);
        }
    }
}

public sealed class TenantAccessFilter(FirstBlickDbContext db, TenantScope scope) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actor)) return Results.Unauthorized();
        if (!Guid.TryParse(http.Request.Headers["X-Tenant-Id"], out var tenant))
            throw new DomainException("tenant_required", "Select a business before continuing.");
        var member = await db.Memberships.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant && x.UserId == actor && x.Active, http.RequestAborted);
        if (member is null) throw new DomainException("forbidden", "You no longer have access to this business.");
        scope.TenantId = tenant; scope.UserId = actor; scope.Role = member.Role;
        return await next(context);
    }
}

public sealed class CommandExecutor(FirstBlickDbContext db, TenantScope scope, EntityStore store)
{
    public async Task Authorize(string kind, bool write, CancellationToken ct)
    {
        // Repeat membership authorization after acquiring the write lock to close revocation races.
        var membership = await db.Memberships.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == scope.TenantId && x.UserId == scope.UserId && x.Active, ct);
        Guard.Require(membership is not null, "forbidden", "Business access has been revoked.");
        scope.Role = membership!.Role;
        Guard.Require(write ? AccessRules.CanWrite(scope.Role, kind) : AccessRules.CanRead(scope.Role, kind), "forbidden", "Your role does not allow this operation.");
        if (write)
        {
            var modules = await db.Tenants.Where(x => x.Id == scope.TenantId).Select(x => x.Modules).SingleAsync(ct);
            Guard.Require(modules.Contains(AccessRules.ModuleFor(kind)), "module_disabled", "This module is disabled for new actions.");
        }
    }

    public async Task<IResult> Execute(HttpContext http, string kind, string action, object? input, Func<CancellationToken, Task<object>> mutation)
    {
        var ct = http.RequestAborted;
        var key = http.Request.Headers["Idempotency-Key"].ToString();
        Guard.Require(key.Length is >= 8 and <= 128 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':'), "idempotency_required", "Supply an Idempotency-Key of 8 to 128 safe characters.");
        var digest = ContentDigest.Of(new { method = http.Request.Method, path = http.Request.Path.Value, action,
            bodyDigest = http.Items[RequestFingerprint.ItemKey] as string ?? ContentDigest.Of(input), revision = http.Request.Headers.IfMatch.ToString() });
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({scope.TenantId.ToString()}, 0))", ct);
        await Authorize(kind, true, ct);
        var receipt = await db.Receipts.SingleOrDefaultAsync(x => x.ActorId == scope.UserId && x.Key == key, ct);
        if (receipt is not null)
        {
            Guard.Require(receipt.RequestDigest == digest, "idempotency_conflict", "This request key was already used for different content.");
            // A receipt is not an authority token. Personal-role access can change after an earlier successful request.
            if (scope.Role is BusinessRole.Worker or BusinessRole.Trainer)
            {
                using var response = JsonDocument.Parse(receipt.Response);
                Guard.Require(response.RootElement.TryGetProperty("id", out var property) && property.TryGetGuid(out _), "forbidden", "This result requires a fresh authorization check before replay.");
                var entityId = property.GetGuid();
                if (kind == "documents")
                {
                    var document = await db.Documents.SingleOrDefaultAsync(x => x.Id == entityId, ct);
                    Guard.Require(document is not null && document.ProjectId.HasValue, "forbidden", "The document is no longer in an assigned project.");
                    await RecordPolicy.EnsureVisible("projects", document.ProjectId.Value, scope, store, ct);
                    Guard.Require(document.SourceKind is null || AccessRules.CanRead(scope.Role, document.SourceKind), "forbidden", "The document source is no longer accessible.");
                }
                else await RecordPolicy.EnsureVisible(kind, entityId, scope, store, ct);
            }
            await transaction.CommitAsync(ct);
            http.Response.Headers["Idempotency-Replayed"] = "true";
            return Results.Content(receipt.Response, "application/json");
        }
        var result = await mutation(ct);
        var json = AppJson.Serialize(result);
        db.Receipts.Add(new CommandReceipt { TenantId = scope.TenantId, ActorId = scope.UserId, Key = key, RequestDigest = digest, Response = json });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Content(json, "application/json");
    }

    public static void Match(HttpContext http, long revision)
    {
        var supplied = http.Request.Headers.IfMatch.ToString().Trim('"');
        Guard.Require(long.TryParse(supplied, out var expected) && expected > 0, "revision_required", "Supply the record revision in If-Match.");
        Guard.Require(expected == revision, "revision_conflict", "The record changed. Reload it before continuing.");
    }
}
