using System.Data;
using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FirstBlick.Infrastructure;

public sealed record OutboxEmail(Guid JobId, string Recipient, string Subject, string Body);

/// <summary>Return only a bounded, non-secret reference. Success means provider acceptance, not inbox delivery.</summary>
public interface IOutboxEmailSender
{
    bool IsConfigured { get; }
    Task<string> Send(OutboxEmail message, CancellationToken cancellationToken);
}

/// <summary>An optional SMTP adapter. STARTTLS is required; implicit TLS and OAuth SMTP are not implemented.</summary>
public sealed class SmtpOutboxEmailSender(IConfiguration configuration) : IOutboxEmailSender
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(configuration["Smtp:Host"])
                                && ValidAddress(configuration["Smtp:From"])
                                && int.TryParse(configuration["Smtp:Port"] ?? "587", out var port) && port is > 0 and <= 65535 && port != 465
                                && !string.Equals(configuration["Smtp:EnableSsl"], "false", StringComparison.OrdinalIgnoreCase);

    public async Task<string> Send(OutboxEmail message, CancellationToken cancellationToken)
    {
        if (!IsConfigured) throw new InvalidOperationException("provider_unconfigured");
        if (!ValidAddress(message.Recipient)) throw new InvalidOperationException("invalid_recipient");
        var from = new MailAddress(configuration["Smtp:From"]!);
        using var mail = new MailMessage(from, new MailAddress(message.Recipient))
        {
            Subject = message.Subject, Body = message.Body, IsBodyHtml = false,
            SubjectEncoding = Encoding.UTF8, BodyEncoding = Encoding.UTF8
        };
        var messageId = $"<{message.JobId:N}@{from.Host}>";
        mail.Headers.Add("Message-ID", messageId);
        using var client = new SmtpClient(configuration["Smtp:Host"], int.Parse(configuration["Smtp:Port"] ?? "587", CultureInfo.InvariantCulture))
        {
            DeliveryMethod = SmtpDeliveryMethod.Network, EnableSsl = true, UseDefaultCredentials = false
        };
        if (!string.IsNullOrEmpty(configuration["Smtp:Username"]))
            client.Credentials = new NetworkCredential(configuration["Smtp:Username"], configuration["Smtp:Password"]);
        await client.SendMailAsync(mail, cancellationToken);
        // This is our submitted Message-ID, not a provider-issued delivery confirmation.
        return $"smtp-message-id:{messageId}";
    }

    internal static bool ValidAddress(string? value)
        => value is { Length: > 0 and <= 254 } && !value.Any(char.IsControl)
           && MailAddress.TryCreate(value, out var address)
           && string.Equals(address.Address, value, StringComparison.OrdinalIgnoreCase);
}

public sealed class OutboxProcessor
{
    public const string RecoveryPurpose = "FirstBlick.Identity.Recovery.v1";
    private readonly FirstBlickDbContext db;
    private readonly TenantScope scope;
    private readonly IConfiguration configuration;
    private readonly IDataProtectionProvider protection;
    private readonly ILogger<OutboxProcessor> logger;
    private readonly IOutboxEmailSender sender;
    private readonly IHostEnvironment? environment;
    private readonly UserManager<ApplicationUser>? users;
    private readonly int leaseSeconds;
    private readonly int sendTimeoutSeconds;
    private readonly int maximumAttempts;

    public OutboxProcessor(FirstBlickDbContext db, TenantScope scope, IConfiguration configuration,
        IDataProtectionProvider protection, ILogger<OutboxProcessor> logger, IOutboxEmailSender? sender = null,
        IHostEnvironment? environment = null, UserManager<ApplicationUser>? users = null)
    {
        this.db = db; this.scope = scope; this.configuration = configuration;
        this.protection = protection; this.logger = logger; this.sender = sender ?? new SmtpOutboxEmailSender(configuration);
        this.environment = environment; this.users = users;
        leaseSeconds = ReadOption(configuration, "Outbox:LeaseSeconds", 120, 30, 600);
        sendTimeoutSeconds = ReadOption(configuration, "Outbox:SendTimeoutSeconds", 30, 1, 120);
        maximumAttempts = ReadOption(configuration, "Outbox:MaximumAttempts", 5, 1, 20);
        if (leaseSeconds <= sendTimeoutSeconds + 10)
            throw new InvalidOperationException("The outbox lease must exceed the send timeout by more than ten seconds.");
    }

    private static int ReadOption(IConfiguration config, string name, int fallback, int minimum, int maximum)
    {
        if (config[name] is null) return fallback;
        if (!int.TryParse(config[name], out var value) || value < minimum || value > maximum)
            throw new InvalidOperationException($"{name} must be between {minimum} and {maximum}.");
        return value;
    }

    /// <summary>Atomically claim a due job across tenants. The database clock determines lease ownership.</summary>
    public async Task<OutboxJob?> Claim(CancellationToken cancellationToken = default)
    {
        var token = Guid.NewGuid();
        var jobs = await db.Jobs.FromSqlInterpolated($"""
            WITH candidate AS (
                SELECT "Id" FROM "Jobs"
                WHERE ("State" = 'Pending' AND "AvailableAt" <= clock_timestamp())
                   OR ("State" = 'Leased' AND ("LeaseUntil" IS NULL OR "LeaseUntil" <= clock_timestamp()))
                ORDER BY "AvailableAt", "Id"
                LIMIT 1 FOR UPDATE SKIP LOCKED
            )
            UPDATE "Jobs" AS job
            SET "State" = 'Leased', "LeaseToken" = {token},
                "LeaseUntil" = clock_timestamp() + ({leaseSeconds} * interval '1 second'),
                "LastError" = CASE WHEN job."Attempts" >= {maximumAttempts} AND NOT job."ExternalAttemptStarted"
                                   THEN 'attempt_limit' ELSE job."LastError" END,
                "Attempts" = CASE WHEN job."ExternalAttemptStarted" THEN job."Attempts"
                                  ELSE LEAST(job."Attempts" + 1, {maximumAttempts}) END
            FROM candidate WHERE job."Id" = candidate."Id"
            RETURNING job.*
            """).IgnoreQueryFilters().AsNoTracking().ToListAsync(cancellationToken);
        return jobs.SingleOrDefault();
    }

    public async Task<bool> ProcessNext(CancellationToken cancellationToken = default)
    {
        var job = await Claim(cancellationToken);
        return job is not null && await ProcessClaimed(job, cancellationToken);
    }

    /// <summary>Process a claimed lease; the supplied object conveys only ID/token, never trusted payload or authority.</summary>
    public async Task<bool> ProcessClaimed(OutboxJob claimed, CancellationToken cancellationToken = default)
    {
        if (claimed.LeaseToken is null) return false;
        var job = await db.Jobs.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == claimed.Id && x.LeaseToken == claimed.LeaseToken && x.State == JobState.Leased, cancellationToken);
        if (job is null) return false;
        try
        {
            if (job.ExternalAttemptStarted)
            {
                await Finish(job, JobState.Uncertain, "lease_expired_after_external_attempt", "", false, cancellationToken);
                return true;
            }
            if (job.LastError == "attempt_limit")
            {
                await Finish(job, JobState.Failed, "attempt_limit", "", false, cancellationToken);
                return true;
            }
            if (job.Type == "entity.changed")
            {
                await Finish(job, JobState.Completed, "", "", false, cancellationToken);
                return true;
            }
            if (job.Type is not ("quote.delivery" or "identity.recovery" or "notification.daily"))
                throw new PermanentJobFailure("unsupported_job_type");
            if (Encoding.UTF8.GetByteCount(job.Payload) > 65_536)
                throw new PermanentJobFailure("job_payload_budget_exceeded");
            if (!sender.IsConfigured) throw new PermanentJobFailure("provider_unconfigured");

            var message = await BeginExternalAttempt(job, cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(sendTimeoutSeconds));
            string reference;
            try
            {
                reference = await sender.Send(message, timeout.Token);
                if (string.IsNullOrWhiteSpace(reference) || reference.Length > 512 || reference.Any(char.IsControl))
                    throw new InvalidOperationException("invalid_provider_reference");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // After the durable marker even a timeout/cancellation may follow provider acceptance.
                await FinishSafely(job, JobState.Uncertain, "external_outcome_unknown", "", false);
                return true;
            }
            await FinishSafely(job, JobState.Completed, "provider_accepted", reference, false);
            return true;
        }
        catch (LeaseLostException) { return false; }
        catch (PermanentJobFailure failure)
        {
            await FinishSafely(job, failure.Uncertain ? JobState.Uncertain : JobState.Failed, failure.Code, "", false);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException or DomainException or FormatException or OverflowException)
        {
            await FinishSafely(job, JobState.Failed, "invalid_job_evidence", "", false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Re-read the durable marker when possible. A later worker also applies this same rule after lease expiry.
            await FinishSafely(job, JobState.Pending, "worker_stopped_before_completion", "", true);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await FinishSafely(job, JobState.Pending, "internal_processing_error", "", true);
            return true;
        }
    }

    private async Task<OutboxEmail> BeginExternalAttempt(OutboxJob claimed, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await TenantLock(claimed.TenantId, ct);
        var job = await OwnedJob(claimed, ct);
        if (job.ExternalAttemptStarted) throw new PermanentJobFailure("external_attempt_already_started", true);
        scope.TenantId = job.TenantId; scope.UserId = job.ActorId;
        var now = await DatabaseNow(ct);
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == job.TenantId, ct)
                     ?? throw new PermanentJobFailure("tenant_unavailable");
        OutboxEmail message;
        switch (job.Type)
        {
            case "quote.delivery":
            {
                var membership = await RequireMembership(job.TenantId, job.ActorId, ct);
                if (!AccessRules.CanWrite(membership.Role, "quotes") || !tenant.Modules.Contains("commercial", StringComparer.Ordinal))
                    throw new PermanentJobFailure("permission_revoked");
                var payload = AppJson.Read<QuoteDeliveryPayload>(job.Payload);
                if (job.Kind != "quotes" || payload.DeliveryId == Guid.Empty || payload.Channel != CommunicationChannel.Email)
                    throw new PermanentJobFailure(payload.Channel != CommunicationChannel.Email ? "channel_unconfigured" : "invalid_job_evidence");
                var stored = await LockedQuote(job, ct) ?? throw new PermanentJobFailure("quote_unavailable");
                var quote = AppJson.Read<Quote>(stored.Data); quote.Validate();
                if (quote.TenantId != job.TenantId || quote.Id != job.EntityId || quote.Revision != stored.Revision || quote.Archived || stored.Archived
                    || quote.Status is not (QuoteStatus.Issued or QuoteStatus.Accepted) || (quote.Presentation is null || quote.IssuedDigest != payload.SnapshotDigest))
                    throw new PermanentJobFailure("quote_snapshot_changed");
                var delivery = quote.Deliveries.SingleOrDefault(x => x.Id == payload.DeliveryId)
                               ?? throw new PermanentJobFailure("delivery_unavailable");
                if (delivery.Recipient != payload.Recipient || delivery.Channel != payload.Channel)
                    throw new PermanentJobFailure("delivery_evidence_changed");
                if (delivery.State != DeliveryState.Queued)
                    throw new PermanentJobFailure("delivery_state_changed", delivery.State == DeliveryState.Processing);
                if (!SmtpOutboxEmailSender.ValidAddress(payload.Recipient)) throw new PermanentJobFailure("invalid_recipient");
                message = ComposeQuote(job, quote, payload.Recipient);
                CommercialWorkflow.BeginDelivery(delivery);
                await WriteQuote(stored, quote, job.ActorId, "delivery.started", now, ct);
                break;
            }
            case "identity.recovery":
            {
                var wrapper = AppJson.Read<RecoveryPayload>(job.Payload);
                if (wrapper.ProtectedMessage is not { Length: > 0 and <= 65_536 })
                    throw new PermanentJobFailure("invalid_recovery_evidence");
                var plaintext = protection.CreateProtector(RecoveryPurpose).Unprotect(wrapper.ProtectedMessage);
                var recovery = AppJson.Read<RecoveryMessage>(plaintext);
                if (job.Kind != "identity" || job.ActorId != Guid.Empty || job.EntityId != recovery.UserId || recovery.UserId == Guid.Empty
                    || recovery.Token is not { Length: > 0 and <= 16_384 })
                    throw new PermanentJobFailure("invalid_recovery_evidence");
                await RequireMembership(job.TenantId, recovery.UserId, ct);
                var account = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == recovery.UserId, ct);
                if (account is null || !account.EmailConfirmed || account.Email != recovery.Email || !SmtpOutboxEmailSender.ValidAddress(recovery.Email))
                    throw new PermanentJobFailure("recovery_account_changed");
                if (users is null) throw new PermanentJobFailure("identity_provider_unconfigured");
                if (!await users.VerifyUserTokenAsync(account, users.Options.Tokens.PasswordResetTokenProvider, UserManager<ApplicationUser>.ResetPasswordTokenPurpose, recovery.Token))
                    throw new PermanentJobFailure("recovery_token_expired");
                var baseUrl = ApplicationBaseUri();
                var link = baseUrl.AbsoluteUri.TrimEnd('/') + "/#reset-email=" + Uri.EscapeDataString(recovery.Email)
                           + "&reset-token=" + Uri.EscapeDataString(recovery.Token);
                message = new(job.Id, recovery.Email, "FirstBlick: Passwort zurücksetzen",
                    "Für dein FirstBlick-Konto wurde eine Passwortzurücksetzung angefordert.\n\n" + link
                    + "\n\nFalls du diese Anfrage nicht gestellt hast, kannst du diese Nachricht ignorieren.");
                break;
            }
            case "notification.daily":
                message = await ComposeDaily(job, tenant, now, ct);
                break;
            default: throw new PermanentJobFailure("unsupported_job_type");
        }
        if (Encoding.UTF8.GetByteCount(message.Body) > 2_097_152 || message.Subject.Length > 200)
            throw new PermanentJobFailure("message_budget_exceeded");
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Jobs" SET "ExternalAttemptStarted" = TRUE, "LastError" = '',
                              "LeaseUntil" = clock_timestamp() + ({leaseSeconds} * interval '1 second')
            WHERE "Id" = {job.Id} AND "LeaseToken" = {job.LeaseToken}
              AND "State" = 'Leased' AND "LeaseUntil" > clock_timestamp() AND NOT "ExternalAttemptStarted"
            """, ct);
        if (changed != 1) throw new LeaseLostException();
        await AuditJob(job, "outbox.external_started", "", "", now, ct);
        await transaction.CommitAsync(ct);
        return message;
    }

    private static OutboxEmail ComposeQuote(OutboxJob job, Quote quote, string recipient)
    {
        var culture = CultureInfo.GetCultureInfo("de-DE");
        var body = new StringBuilder().AppendLine($"Angebot {quote.Number} – {quote.Title}")
            .AppendLine($"Version {quote.Version}").AppendLine($"Von: {quote.Presentation!.SellerName}")
            .AppendLine($"An: {quote.Presentation.BuyerName}").AppendLine(quote.Presentation.BuyerAddress).AppendLine($"Quellen-Digest: {quote.IssuedDigest}").AppendLine();
        foreach (var line in quote.Snapshot!.Lines)
        {
            body.AppendLine(line.Inputs.Description)
                .AppendLine($"{line.Inputs.Quantity.ToString("0.######", culture)} {line.Inputs.Unit} × {line.UnitNet.ToString("0.00", culture)} {quote.Currency} netto = {line.Net.ToString("0.00", culture)} {quote.Currency} netto")
                .AppendLine($"Umsatzsteuer {line.Inputs.VatPercent.ToString("0.##", culture)} %: {line.Tax.ToString("0.00", culture)} {quote.Currency}").AppendLine();
        }
        body.AppendLine($"Netto: {quote.Snapshot.Net.ToString("0.00", culture)} {quote.Currency}")
            .AppendLine($"Umsatzsteuer: {quote.Snapshot.Tax.ToString("0.00", culture)} {quote.Currency}")
            .AppendLine($"Gesamt: {quote.Snapshot.Gross.ToString("0.00", culture)} {quote.Currency}");
        if (quote.ValidUntil is { } validUntil) body.AppendLine($"Gültig bis: {validUntil:dd.MM.yyyy}");
        var subject = $"Angebot {quote.Number}: {quote.Title}".Replace('\r', ' ').Replace('\n', ' ');
        if (subject.Length > 200) subject = subject[..200];
        return new(job.Id, recipient, subject, body.ToString());
    }

    private async Task FinishSafely(OutboxJob job, JobState state, string code, string reference, bool retry)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await Finish(job, state, code, reference, retry, cleanup.Token); }
        catch (LeaseLostException) { logger.LogInformation("Outbox job {JobId} no longer owns its lease", job.Id); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never log exception messages, payloads, email bodies, recipient addresses or recovery tokens.
            logger.LogWarning("Outbox result persistence remains pending for {JobId}; expired-lease recovery will reconcile it", job.Id);
        }
    }

    private async Task Finish(OutboxJob claimed, JobState requestedState, string code, string reference, bool retry, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await TenantLock(claimed.TenantId, ct);
        var job = await OwnedJob(claimed, ct);
        scope.TenantId = job.TenantId; scope.UserId = job.ActorId;
        var now = await DatabaseNow(ct);
        var state = requestedState;
        if (job.ExternalAttemptStarted && state is JobState.Pending or JobState.Failed)
        {
            state = JobState.Uncertain; code = "external_outcome_unknown";
        }
        if (retry && state == JobState.Pending && job.Attempts >= maximumAttempts) { state = JobState.Failed; code = "attempt_limit"; }
        if (job.Type == "quote.delivery" && state != JobState.Pending)
        {
            bool updated;
            try { updated = await UpdateDeliveryResult(job, state, code, reference, now, ct); }
            catch (Exception ex) when (ex is JsonException or DomainException or FormatException or OverflowException or PermanentJobFailure)
            {
                // Invalid or oversized quote evidence must not strand a possibly accepted external attempt in an endless lease cycle.
                // These failures occur before the quote UPDATE. Database/lease failures still roll back both records together.
                updated = false;
            }
            if (!updated && job.ExternalAttemptStarted)
            {
                state = JobState.Uncertain; code = "delivery_evidence_unavailable";
            }
        }
        var nextAt = now.AddSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Max(0, job.Attempts - 1))));
        DateTimeOffset? completedAt = state is JobState.Completed or JobState.Failed ? now : null;
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Jobs"
            SET "State" = {state.ToString()}, "LastError" = {code}, "ProviderReference" = {reference},
                "LeaseToken" = NULL, "LeaseUntil" = NULL, "CompletedAt" = {completedAt},
                "AvailableAt" = {nextAt}
            WHERE "Id" = {job.Id} AND "LeaseToken" = {job.LeaseToken}
              AND "State" = 'Leased' AND "LeaseUntil" > clock_timestamp()
            """, ct);
        if (changed != 1) throw new LeaseLostException();
        await AuditJob(job, "outbox." + state.ToString().ToLowerInvariant(), code, reference, now, ct);
        await transaction.CommitAsync(ct);
        if (state is JobState.Uncertain or JobState.Failed) logger.LogWarning("Outbox job {JobId}: {Code}", job.Id, code);
    }

    private async Task<bool> UpdateDeliveryResult(OutboxJob job, JobState state, string code, string reference, DateTimeOffset now, CancellationToken ct)
    {
        QuoteDeliveryPayload payload;
        try { payload = AppJson.Read<QuoteDeliveryPayload>(job.Payload); }
        catch (Exception ex) when (ex is JsonException or DomainException) { return false; }
        var stored = await LockedQuote(job, ct);
        if (stored is null) return false;
        Quote quote;
        try { quote = AppJson.Read<Quote>(stored.Data); quote.Validate(); }
        catch (Exception ex) when (ex is JsonException or DomainException) { return false; }
        var delivery = quote.Deliveries.SingleOrDefault(x => x.Id == payload.DeliveryId);
        if (quote.TenantId != job.TenantId || quote.Id != job.EntityId || quote.Revision != stored.Revision || delivery is null
            || delivery.Recipient != payload.Recipient || delivery.Channel != payload.Channel) return false;
        if (state == JobState.Completed && (quote.Presentation is null || quote.IssuedDigest != payload.SnapshotDigest)) return false;
        var target = state switch
        {
            JobState.Completed => DeliveryState.Sent,
            JobState.Uncertain => DeliveryState.Uncertain,
            _ => DeliveryState.Failed
        };
        if (delivery.State is DeliveryState.Sent or DeliveryState.Failed) return delivery.State == target;
        delivery.State = target; delivery.ProviderReference = reference; delivery.ResolutionNote = code;
        delivery.ResolvedAt = target is DeliveryState.Sent or DeliveryState.Failed ? now : null;
        await WriteQuote(stored, quote, job.ActorId, "delivery." + target.ToString().ToLowerInvariant(), now, ct);
        return true;
    }

    private async Task WriteQuote(StoredEntity stored, Quote quote, Guid actorId, string action, DateTimeOffset now, CancellationToken ct)
    {
        quote.Revision = checked(stored.Revision + 1); quote.UpdatedAt = now; quote.CreatedAt = stored.CreatedAt;
        quote.Validate();
        var json = AppJson.Serialize(quote);
        if (Encoding.UTF8.GetByteCount(json) > 1_900_000) throw new PermanentJobFailure("delivery_record_budget_exceeded");
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Entities" SET "Data" = CAST({json} AS jsonb), "Revision" = {quote.Revision}, "UpdatedAt" = {now}
            WHERE "TenantId" = {stored.TenantId} AND "Kind" = 'quotes' AND "Id" = {stored.Id} AND "Revision" = {stored.Revision}
            """, ct);
        if (changed != 1) throw new LeaseLostException();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Audit" ("TenantId", "ActorId", "Action", "Kind", "EntityId", "Revision", "ContentDigest", "CorrelationId", "At")
            VALUES ({stored.TenantId}, {actorId}, {action}, 'quotes', {stored.Id}, {quote.Revision}, {ContentDigest.OfUtf8(json)}, '', {now})
            """, ct);
    }

    private async Task<StoredEntity?> LockedQuote(OutboxJob job, CancellationToken ct)
        => (await db.Entities.FromSqlInterpolated($"""
            SELECT * FROM "Entities" WHERE "TenantId" = {job.TenantId} AND "Kind" = 'quotes' AND "Id" = {job.EntityId} FOR UPDATE
            """).IgnoreQueryFilters().AsNoTracking().ToListAsync(ct)).SingleOrDefault();

    private async Task<OutboxJob> OwnedJob(OutboxJob claimed, CancellationToken ct)
        => (await db.Jobs.FromSqlInterpolated($"""
            SELECT * FROM "Jobs" WHERE "Id" = {claimed.Id} AND "TenantId" = {claimed.TenantId}
              AND "LeaseToken" = {claimed.LeaseToken} AND "State" = 'Leased' AND "LeaseUntil" > clock_timestamp() FOR UPDATE
            """).IgnoreQueryFilters().AsNoTracking().ToListAsync(ct)).SingleOrDefault() ?? throw new LeaseLostException();

    private Task TenantLock(Guid tenantId, CancellationToken ct)
        => db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({tenantId.ToString()}, 0))", ct);

    private Task<DateTimeOffset> DatabaseNow(CancellationToken ct)
        => db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);

    private async Task<Membership> RequireMembership(Guid tenantId, Guid userId, CancellationToken ct)
        => await db.Memberships.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.UserId == userId && x.Active, ct)
           ?? throw new PermanentJobFailure("membership_revoked");

    private Task<int> AuditJob(OutboxJob job, string action, string code, string reference, DateTimeOffset now, CancellationToken ct)
        => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Audit" ("TenantId", "ActorId", "Action", "Kind", "EntityId", "Revision", "ContentDigest", "CorrelationId", "At")
            VALUES ({job.TenantId}, {job.ActorId}, {action}, 'outbox', {job.Id}, {job.Attempts},
                    {ContentDigest.Of(new { job.Id, action, code, reference })}, '', {now})
            """, ct);

    private Uri ApplicationBaseUri()
    {
        var value = configuration["App:BaseUrl"];
        var local = environment?.IsDevelopment() == true || environment?.IsEnvironment("Testing") == true;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.Scheme != Uri.UriSchemeHttps && !(local && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            throw new PermanentJobFailure("public_url_unconfigured");
        return uri;
    }

    private async Task<OutboxEmail> ComposeDaily(OutboxJob job, BusinessTenant tenant, DateTimeOffset now, CancellationToken ct)
    {
        var payload = AppJson.Read<DailyNotificationPayload>(job.Payload);
        var stored = await db.Entities.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(
            x => x.TenantId == job.TenantId && x.Kind == "notification-preferences" && x.Id == payload.PreferenceId && !x.Archived, ct)
                     ?? throw new PermanentJobFailure("notification_preference_unavailable");
        var preference = AppJson.Read<NotificationPreference>(stored.Data); preference.Validate();
        if (job.Kind != "notification-preferences" || job.EntityId != preference.Id || preference.Id != stored.Id
            || preference.Revision != stored.Revision || preference.Archived || preference.TenantId != job.TenantId
            || preference.UserId != job.ActorId || !tenant.Modules.Contains("maintenance", StringComparer.Ordinal)
            || !preference.Channels.Contains(payload.Channel) || !OperationsRules.BriefingDue(preference, now)
            || OperationsRules.BriefingOccurrenceKey(preference, now) != payload.OccurrenceKey)
            throw new PermanentJobFailure("notification_preference_changed");
        var membership = await RequireMembership(job.TenantId, preference.UserId, ct);
        if (!AccessRules.CanRead(membership.Role, "notification-preferences")) throw new PermanentJobFailure("permission_revoked");
        if (payload.Channel != CommunicationChannel.Email) throw new PermanentJobFailure("channel_unconfigured");
        var account = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == preference.UserId, ct);
        if (account is null || !account.EmailConfirmed || !SmtpOutboxEmailSender.ValidAddress(account.Email))
            throw new PermanentJobFailure("notification_address_unavailable");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(preference.TimeZone);
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var day = DateOnly.FromDateTime(localNow.DateTime);
        var body = new StringBuilder().AppendLine($"Dein Arbeitstag am {day:dd.MM.yyyy}").AppendLine();
        if (tenant.Modules.Contains("field", StringComparer.Ordinal))
        {
            var employees = await ReadEntities<Employee>(job.TenantId, "employees", ct);
            var employeeIds = employees.Where(x => x.UserId == preference.UserId && x.Active && !x.Archived).Select(x => x.Id).ToHashSet();
            if (preference.IncludeToday && AccessRules.CanRead(membership.Role, "schedule-assignments"))
            {
                var assignments = (await ReadEntities<ScheduleAssignment>(job.TenantId, "schedule-assignments", ct))
                    .Where(x => employeeIds.Contains(x.EmployeeId) && !x.Cancelled &&
                                DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(x.Start, zone).DateTime) <= day &&
                                DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(x.End.AddTicks(-1), zone).DateTime) >= day)
                    .OrderBy(x => x.Start).ToArray();
                body.AppendLine($"Heute eingeplant: {assignments.Length} Einsatz/Einsätze.");
                foreach (var assignment in assignments.Take(30))
                    body.AppendLine($"{TimeZoneInfo.ConvertTime(assignment.Start, zone):HH:mm}–{TimeZoneInfo.ConvertTime(assignment.End, zone):HH:mm}: {assignment.Title}");
                if (assignments.Length > 30) body.AppendLine("Weitere Einsätze findest du in FirstBlick.");
                body.AppendLine();
            }
            if (preference.IncludeYesterday && AccessRules.CanRead(membership.Role, "time-entries"))
            {
                var yesterday = day.AddDays(-1);
                var entries = (await ReadEntities<TimeEntry>(job.TenantId, "time-entries", ct)).Where(x => employeeIds.Contains(x.EmployeeId)).ToArray();
                var start = LocalMidnight(yesterday, zone);
                var end = LocalMidnight(day, zone);
                var ticks = entries.Sum(entry => OverlapTicks(entry.Start, entry.End, start, end)
                    - entry.Breaks.Sum(pause => OverlapTicks(pause.Start, pause.End, start, end)));
                body.AppendLine($"Gestern erfasste eigene Nettoarbeitszeit: {((decimal)ticks / TimeSpan.TicksPerHour).ToString("0.00", CultureInfo.GetCultureInfo("de-DE"))} Stunden.").AppendLine();
            }
        }
        if (preference.IncludeMaintenance && AccessRules.CanRead(membership.Role, "maintenance-contracts"))
        {
            var due = (await ReadEntities<MaintenanceContract>(job.TenantId, "maintenance-contracts", ct))
                .Where(x => x.Active && x.NextDueDate <= day).OrderBy(x => x.NextDueDate).ToArray();
            body.AppendLine($"Fällige Wartungen im Betrieb: {due.Length}.");
            foreach (var contract in due.Take(20)) body.AppendLine($"{contract.NextDueDate:dd.MM.yyyy}: {contract.Title}");
        }
        return new(job.Id, account.Email!, $"FirstBlick: Dein Arbeitstag am {day:dd.MM.yyyy}", body.ToString());
    }

    private static long OverlapTicks(DateTimeOffset start, DateTimeOffset end, DateTimeOffset windowStart, DateTimeOffset windowEnd)
        => Math.Max(0, (end < windowEnd ? end : windowEnd).Ticks - (start > windowStart ? start : windowStart).Ticks);

    private static DateTimeOffset LocalMidnight(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
            throw new PermanentJobFailure("briefing_timezone_boundary_unsupported");
        return new(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }

    private async Task<List<T>> ReadEntities<T>(Guid tenantId, string kind, CancellationToken ct) where T : TenantEntity
    {
        var json = await db.Entities.IgnoreQueryFilters().AsNoTracking().Where(x => x.TenantId == tenantId && x.Kind == kind && !x.Archived)
            .Select(x => x.Data).Take(10001).ToListAsync(ct);
        if (json.Count > 10000) throw new PermanentJobFailure("briefing_query_budget_exceeded");
        var values = json.Select(AppJson.Read<T>).ToList();
        foreach (var value in values)
        {
            value.Validate();
            if (value.TenantId != tenantId) throw new PermanentJobFailure("entity_tenant_mismatch");
        }
        return values;
    }

    /// <summary>Persist one occurrence per tenant/user/local date/channel. No external provider is called here.</summary>
    public async Task<int> ScheduleDueNotifications(CancellationToken cancellationToken = default)
    {
        var count = 0;
        var offset = 0;
        while (true)
        {
            var batch = await db.Entities.IgnoreQueryFilters().AsNoTracking().Where(x => x.Kind == "notification-preferences" && !x.Archived)
                .OrderBy(x => x.TenantId).ThenBy(x => x.Id).Skip(offset).Take(200).ToListAsync(cancellationToken);
            if (batch.Count == 0) break;
            offset += batch.Count;
            foreach (var candidate in batch)
            {
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
                await TenantLock(candidate.TenantId, cancellationToken);
                var current = await db.Entities.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(
                    x => x.TenantId == candidate.TenantId && x.Kind == candidate.Kind && x.Id == candidate.Id && !x.Archived, cancellationToken);
                if (current is null) continue;
                NotificationPreference preference;
                try { preference = AppJson.Read<NotificationPreference>(current.Data); preference.Validate(); }
                catch (Exception ex) when (ex is JsonException or DomainException) { continue; }
                var now = await DatabaseNow(cancellationToken);
                if (preference.Id != current.Id || preference.Revision != current.Revision || preference.Archived
                    || preference.TenantId != candidate.TenantId || !OperationsRules.BriefingDue(preference, now)) continue;
                var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == candidate.TenantId, cancellationToken);
                var membership = await db.Memberships.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == candidate.TenantId && x.UserId == preference.UserId && x.Active, cancellationToken);
                if (tenant is null || !tenant.Modules.Contains("maintenance", StringComparer.Ordinal) || membership is null
                    || !AccessRules.CanRead(membership.Role, "notification-preferences")) continue;
                var occurrence = OperationsRules.BriefingOccurrenceKey(preference, now);
                foreach (var channel in preference.Channels)
                {
                    var key = occurrence + ":" + channel;
                    var payload = AppJson.Serialize(new DailyNotificationPayload(preference.Id, channel, occurrence));
                    var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                        INSERT INTO "Receipts" ("TenantId", "ActorId", "Key", "RequestDigest", "Response", "CreatedAt")
                        VALUES ({candidate.TenantId}, {preference.UserId}, {key}, {ContentDigest.OfUtf8(payload)}, jsonb_build_object(), {now})
                        ON CONFLICT ("TenantId", "ActorId", "Key") DO NOTHING
                        """, cancellationToken);
                    if (inserted == 0) continue;
                    var id = Guid.NewGuid();
                    await db.Database.ExecuteSqlInterpolatedAsync($"""
                        INSERT INTO "Jobs" ("Id", "TenantId", "ActorId", "Type", "Kind", "EntityId", "EntityRevision", "Payload", "State",
                                            "AvailableAt", "Attempts", "LastError", "ProviderReference", "ExternalAttemptStarted")
                        VALUES ({id}, {candidate.TenantId}, {preference.UserId}, 'notification.daily', 'notification-preferences',
                                {preference.Id}, {current.Revision}, CAST({payload} AS jsonb), 'Pending', {now}, 0, '', '', FALSE)
                        """, cancellationToken);
                    await AuditJob(new OutboxJob { Id = id, TenantId = candidate.TenantId, ActorId = preference.UserId },
                        "outbox.notification_scheduled", "", "", now, cancellationToken);
                    count++;
                }
                await transaction.CommitAsync(cancellationToken);
            }
        }
        return count;
    }

    private sealed record QuoteDeliveryPayload(Guid DeliveryId, string SnapshotDigest, CommunicationChannel Channel, string Recipient);
    private sealed record RecoveryPayload(string ProtectedMessage);
    private sealed record RecoveryMessage(string Email, string Token, Guid UserId);
    private sealed record DailyNotificationPayload(Guid PreferenceId, CommunicationChannel Channel, string OccurrenceKey);
    private sealed class LeaseLostException : Exception { }
    private sealed class PermanentJobFailure(string code, bool uncertain = false) : Exception(code)
    {
        public string Code { get; } = code;
        public bool Uncertain { get; } = uncertain;
    }
}
