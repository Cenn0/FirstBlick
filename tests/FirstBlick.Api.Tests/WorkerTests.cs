using System.Collections.Concurrent;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace FirstBlick.Api.Tests;

/// <summary>Real PostgreSQL locking/JSONB tests; every test owns and removes a distinct, explicitly named test database.</summary>
public sealed class WorkerTests : IAsyncLifetime
{
    private WorkerDatabase database = null!;
    public async Task InitializeAsync() => database = await WorkerDatabase.Create();
    public Task DisposeAsync() => database.DisposeAsync().AsTask();

    [WorkerPostgresFact]
    public async Task Concurrent_claims_have_distinct_tokens_and_skip_a_row_locked_by_another_transaction()
    {
        await using var seed = database.Open();
        var blocked = InternalJob(); blocked.AvailableAt = DateTimeOffset.UtcNow.AddHours(-1);
        var jobs = Enumerable.Range(0, 8).Select(_ => InternalJob()).Prepend(blocked).ToArray();
        seed.Db.Jobs.AddRange(jobs); await seed.Db.SaveChangesAsync();
        await using var locked = database.Open();
        await using var transaction = await locked.Db.Database.BeginTransactionAsync();
        await locked.Db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Jobs\" WHERE \"Id\" = {blocked.Id} FOR UPDATE");
        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var worker = database.Open();
            return await worker.Processor.Claim().WaitAsync(TimeSpan.FromSeconds(5));
        }));
        Assert.All(claims, claim => { Assert.NotNull(claim); Assert.Equal(1, claim!.Attempts); Assert.NotNull(claim.LeaseToken); });
        Assert.Equal(8, claims.Select(x => x!.Id).Distinct().Count());
        Assert.Equal(8, claims.Select(x => x!.LeaseToken).Distinct().Count());
        Assert.DoesNotContain(claims, x => x!.Id == blocked.Id);
        await transaction.RollbackAsync();
        var last = await seed.Processor.Claim(); Assert.Equal(blocked.Id, last!.Id);
    }

    [WorkerPostgresFact]
    public async Task Expired_lease_replacement_rejects_the_old_token_and_completes_internal_work()
    {
        await using var worker = database.Open();
        var job = InternalJob(); worker.Db.Jobs.Add(job); await worker.Db.SaveChangesAsync();
        var first = (await worker.Processor.Claim())!;
        await Expire(worker.Db, first.Id);
        await using var replacement = database.Open();
        var second = (await replacement.Processor.Claim())!;
        Assert.NotEqual(first.LeaseToken, second.LeaseToken);
        Assert.False(await worker.Processor.ProcessClaimed(first));
        Assert.True(await replacement.Processor.ProcessClaimed(second));
        var result = await database.ReadJob(job.Id);
        Assert.Equal(JobState.Completed, result.State); Assert.Equal(2, result.Attempts);
        Assert.False(result.ExternalAttemptStarted); Assert.Null(result.LeaseToken);
    }

    [WorkerPostgresTheory]
    [InlineData(4, JobState.Completed)]
    [InlineData(5, JobState.Failed)]
    public async Task Internal_crash_recovery_is_bounded_by_the_attempt_limit(int priorAttempts, JobState expected)
    {
        await using var worker = database.Open();
        var job = InternalJob(); job.State = JobState.Leased; job.Attempts = priorAttempts;
        job.LeaseToken = Guid.NewGuid(); job.LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(-1);
        worker.Db.Jobs.Add(job); await worker.Db.SaveChangesAsync();
        Assert.True(await worker.Processor.ProcessNext());
        var result = await database.ReadJob(job.Id);
        Assert.Equal(expected, result.State); Assert.Equal(5, result.Attempts);
        if (expected == JobState.Failed) Assert.Equal("attempt_limit", result.LastError);
    }

    [WorkerPostgresTheory]
    [InlineData("invoice.generate")]
    [InlineData("document.generate")]
    public async Task Unsupported_generation_jobs_fail_without_claiming_a_deliverable(string type)
    {
        var sender = new SyntheticSender();
        await using var worker = database.Open(sender);
        var job = InternalJob(); job.Type = type;
        worker.Db.Jobs.Add(job); await worker.Db.SaveChangesAsync();
        Assert.True(await worker.Processor.ProcessNext());
        var result = await database.ReadJob(job.Id);
        Assert.Equal(JobState.Failed, result.State); Assert.Equal("unsupported_job_type", result.LastError);
        Assert.False(result.ExternalAttemptStarted); Assert.Empty(sender.Messages);
    }

    [WorkerPostgresFact]
    public async Task An_unconfigured_provider_fails_the_queued_attempt_without_reporting_sent()
    {
        var sender = new SyntheticSender { Configured = false };
        var seeded = await database.SeedQuote();
        await using var worker = database.Open(sender);
        Assert.True(await worker.Processor.ProcessNext());
        var result = await database.ReadJob(seeded.Job.Id);
        Assert.Equal(JobState.Failed, result.State); Assert.Equal("provider_unconfigured", result.LastError);
        Assert.False(result.ExternalAttemptStarted); Assert.Empty(sender.Messages);
        var quote = await database.ReadQuote(seeded.Quote);
        Assert.Equal(DeliveryState.Failed, Assert.Single(quote.Deliveries).State);
        Assert.Equal(QuoteStatus.Issued, quote.Status);
    }

    [WorkerPostgresTheory]
    [InlineData("membership", "membership_revoked")]
    [InlineData("role", "permission_revoked")]
    [InlineData("module", "permission_revoked")]
    [InlineData("snapshot", "quote_snapshot_changed")]
    [InlineData("rejected", "quote_snapshot_changed")]
    public async Task Current_authority_and_snapshot_are_rechecked_before_external_send(string change, string expectedError)
    {
        var sender = new SyntheticSender();
        var seeded = await database.SeedQuote();
        await using var worker = database.Open(sender);
        switch (change)
        {
            case "membership":
                await worker.Db.Memberships.Where(x => x.TenantId == seeded.Quote.TenantId).ExecuteUpdateAsync(x => x.SetProperty(y => y.Active, false)); break;
            case "role":
                await worker.Db.Memberships.Where(x => x.TenantId == seeded.Quote.TenantId).ExecuteUpdateAsync(x => x.SetProperty(y => y.Role, BusinessRole.Worker)); break;
            case "module":
                await worker.Db.Tenants.Where(x => x.Id == seeded.Quote.TenantId).ExecuteUpdateAsync(x => x.SetProperty(y => y.Modules, new[] { "core" })); break;
            case "snapshot":
                var badPayload = AppJson.Serialize(new { deliveryId = seeded.Delivery.Id, snapshotDigest = new string('0', 64), channel = CommunicationChannel.Email, recipient = seeded.Delivery.Recipient });
                await worker.Db.Jobs.IgnoreQueryFilters().Where(x => x.Id == seeded.Job.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.Payload, badPayload)); break;
            case "rejected":
                CommercialWorkflow.RejectQuote(seeded.Quote); await database.ReplaceQuote(seeded.Quote); break;
        }
        Assert.True(await worker.Processor.ProcessNext());
        var result = await database.ReadJob(seeded.Job.Id);
        Assert.Equal(JobState.Failed, result.State); Assert.Equal(expectedError, result.LastError);
        Assert.False(result.ExternalAttemptStarted); Assert.Empty(sender.Messages);
    }

    [WorkerPostgresFact]
    public async Task A_cross_tenant_quote_identifier_cannot_dispatch_or_modify_the_other_tenants_delivery()
    {
        var sender = new SyntheticSender();
        var foreign = await database.SeedQuote();
        var (tenant, user) = await database.SeedIdentity();
        await using var worker = database.Open(sender);
        await worker.Db.Jobs.IgnoreQueryFilters().Where(x => x.Id == foreign.Job.Id).ExecuteUpdateAsync(x => x
            .SetProperty(y => y.TenantId, tenant.Id).SetProperty(y => y.ActorId, user.Id));
        Assert.True(await worker.Processor.ProcessNext());
        Assert.Empty(sender.Messages);
        Assert.Equal("quote_unavailable", (await database.ReadJob(foreign.Job.Id)).LastError);
        var quote = await database.ReadQuote(foreign.Quote);
        Assert.Equal(1, quote.Revision); Assert.Equal(DeliveryState.Queued, Assert.Single(quote.Deliveries).State);
    }

    [WorkerPostgresFact]
    public async Task Dispatch_durably_marks_the_attempt_and_does_not_hold_tenant_locks_during_transport()
    {
        var seeded = await database.SeedQuote();
        var sender = new SyntheticSender();
        sender.OnSend = async (_, _) =>
        {
            await using var concurrent = database.Open();
            var marked = await database.ReadJob(seeded.Job.Id);
            Assert.True(marked.ExternalAttemptStarted); Assert.Equal(JobState.Leased, marked.State);
            var quote = await database.ReadQuote(seeded.Quote);
            Assert.Equal(DeliveryState.Processing, Assert.Single(quote.Deliveries).State);
            Assert.Equal(2, quote.Revision);
            await using var transaction = await concurrent.Db.Database.BeginTransactionAsync();
            await concurrent.Db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'");
            await concurrent.Db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({quote.TenantId.ToString()}, 0))");
            CommercialWorkflow.AcceptQuote(quote, quote.IssuedDigest, DateTimeOffset.UtcNow, DateOnly.FromDateTime(DateTime.UtcNow));
            quote.Revision++; quote.UpdatedAt = DateTimeOffset.UtcNow;
            var json = AppJson.Serialize(quote);
            await concurrent.Db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Entities\" SET \"Data\" = CAST({json} AS jsonb), \"Revision\" = {quote.Revision}, \"UpdatedAt\" = {quote.UpdatedAt} WHERE \"TenantId\" = {quote.TenantId} AND \"Kind\" = 'quotes' AND \"Id\" = {quote.Id}");
            await transaction.CommitAsync();
            return "synthetic:accepted";
        };
        await using var worker = database.Open(sender);
        Assert.True(await worker.Processor.ProcessNext());
        var result = await database.ReadJob(seeded.Job.Id);
        Assert.Equal(JobState.Completed, result.State); Assert.Equal("synthetic:accepted", result.ProviderReference);
        var final = await database.ReadQuote(seeded.Quote);
        Assert.Equal(QuoteStatus.Accepted, final.Status); Assert.NotNull(final.AcceptedAt);
        Assert.Equal(4, final.Revision); Assert.Equal(DeliveryState.Sent, Assert.Single(final.Deliveries).State);
        Assert.Equal(4, (await database.ReadStored(seeded.Quote)).Revision);
        var message = Assert.Single(sender.Messages);
        Assert.Contains("250,00 EUR netto", message.Body); Assert.Contains("297,50 EUR", message.Body);
        Assert.DoesNotContain("100,00", message.Body); Assert.DoesNotContain("Markup", message.Body);
        Assert.False(await worker.Processor.ProcessNext());
    }

    [WorkerPostgresFact]
    public async Task A_transport_exception_after_possible_acceptance_is_uncertain_and_never_automatically_retried()
    {
        var seeded = await database.SeedQuote();
        var sender = new SyntheticSender { OnSend = (_, _) => throw new IOException("Synthetic provider accepted before disconnect") };
        await using var worker = database.Open(sender);
        Assert.True(await worker.Processor.ProcessNext());
        var result = await database.ReadJob(seeded.Job.Id);
        Assert.Equal(JobState.Uncertain, result.State); Assert.True(result.ExternalAttemptStarted);
        Assert.Equal("external_outcome_unknown", result.LastError); Assert.Null(result.CompletedAt);
        Assert.Equal(DeliveryState.Uncertain, Assert.Single((await database.ReadQuote(seeded.Quote)).Deliveries).State);
        Assert.False(await worker.Processor.ProcessNext()); Assert.Single(sender.Messages);
    }

    [WorkerPostgresFact]
    public async Task Lease_recovery_during_an_external_attempt_quarantines_it_and_rejects_the_old_completion()
    {
        var seeded = await database.SeedQuote();
        var sender = new SyntheticSender();
        sender.OnSend = async (_, _) =>
        {
            await using var recovery = database.Open(sender);
            await Expire(recovery.Db, seeded.Job.Id);
            Assert.True(await recovery.Processor.ProcessNext());
            Assert.Equal(JobState.Uncertain, (await database.ReadJob(seeded.Job.Id)).State);
            return "synthetic:late-acceptance";
        };
        await using var worker = database.Open(sender);
        Assert.True(await worker.Processor.ProcessNext());
        var result = await database.ReadJob(seeded.Job.Id);
        Assert.Equal(JobState.Uncertain, result.State); Assert.Equal(1, result.Attempts);
        Assert.Equal("lease_expired_after_external_attempt", result.LastError); Assert.Empty(result.ProviderReference);
        Assert.Null(result.LeaseToken); Assert.Single(sender.Messages);
        Assert.False(await worker.Processor.ProcessNext());
    }

    [WorkerPostgresFact]
    public async Task Missing_quote_evidence_after_a_crash_still_persists_uncertainty_instead_of_repeating_leases()
    {
        var seeded = await database.SeedQuote();
        var sender = new SyntheticSender();
        await using var worker = database.Open(sender);
        await worker.Db.Jobs.IgnoreQueryFilters().Where(x => x.Id == seeded.Job.Id).ExecuteUpdateAsync(x => x
            .SetProperty(y => y.State, JobState.Leased).SetProperty(y => y.ExternalAttemptStarted, true)
            .SetProperty(y => y.Attempts, 1).SetProperty(y => y.LeaseToken, Guid.NewGuid())
            .SetProperty(y => y.LeaseUntil, DateTimeOffset.UtcNow.AddMinutes(-1)));
        await worker.Db.Entities.IgnoreQueryFilters().Where(x => x.TenantId == seeded.Quote.TenantId && x.Id == seeded.Quote.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.Data, "{}"));
        Assert.True(await worker.Processor.ProcessNext());
        var result = await database.ReadJob(seeded.Job.Id);
        Assert.Equal(JobState.Uncertain, result.State); Assert.Equal("delivery_evidence_unavailable", result.LastError);
        Assert.False(await worker.Processor.ProcessNext()); Assert.Empty(sender.Messages);
    }

    [WorkerPostgresFact]
    public async Task A_transient_failure_before_the_marker_retries_with_backoff_and_does_not_duplicate_a_send()
    {
        var seeded = await database.SeedQuote();
        var sender = new SyntheticSender { ConfigurationCheckFailure = new InvalidOperationException("synthetic preflight failure") };
        await using var worker = database.Open(sender);
        Assert.True(await worker.Processor.ProcessNext());
        var first = await database.ReadJob(seeded.Job.Id);
        Assert.Equal(JobState.Pending, first.State); Assert.Equal(1, first.Attempts); Assert.False(first.ExternalAttemptStarted);
        Assert.True(first.AvailableAt > DateTimeOffset.UtcNow); Assert.Empty(sender.Messages);
        Assert.False(await worker.Processor.ProcessNext());
        sender.ConfigurationCheckFailure = null;
        await worker.Db.Jobs.IgnoreQueryFilters().Where(x => x.Id == seeded.Job.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.AvailableAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        Assert.True(await worker.Processor.ProcessNext());
        var result = await database.ReadJob(seeded.Job.Id);
        Assert.Equal(JobState.Completed, result.State); Assert.Equal(2, result.Attempts); Assert.Single(sender.Messages);
    }

    [WorkerPostgresFact]
    public async Task Recovery_uses_shared_persistent_keys_and_puts_the_reset_token_only_in_the_email_fragment()
    {
        var (tenant, user) = await database.SeedIdentity();
        using var identityScope = database.Services.CreateScope();
        var users = identityScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var job = RecoveryJob(database.Protection, tenant.Id, user, token);
        await using (var seed = database.Open()) { seed.Db.Jobs.Add(job); await seed.Db.SaveChangesAsync(); }
        Assert.DoesNotContain(token, job.Payload); Assert.DoesNotContain(user.Email!, job.Payload);
        await using var secondHost = database.BuildServices();
        using var secondScope = secondHost.CreateScope();
        var sender = new SyntheticSender();
        await using var worker = database.Open(sender, secondHost.GetRequiredService<IDataProtectionProvider>(),
            secondScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>());
        Assert.True(await worker.Processor.ProcessNext());
        var result = await database.ReadJob(job.Id); Assert.Equal(JobState.Completed, result.State);
        var message = Assert.Single(sender.Messages);
        Assert.Equal(user.Email, message.Recipient);
        Assert.Contains("https://firstblick.example.test/#reset-email=", message.Body);
        Assert.Contains("&reset-token=" + Uri.EscapeDataString(token), message.Body);
        Assert.DoesNotContain("?reset", message.Body);
        await using var check = database.Open();
        Assert.True(await check.Db.DataProtectionKeys.AnyAsync());
        var audits = await check.Db.Audit.IgnoreQueryFilters().Where(x => x.EntityId == job.Id).ToListAsync();
        Assert.DoesNotContain(audits, audit => AppJson.Serialize(audit).Contains(token, StringComparison.Ordinal));
    }

    [WorkerPostgresTheory]
    [InlineData("purpose")]
    [InlineData("confirmation")]
    [InlineData("stamp")]
    [InlineData("email")]
    [InlineData("membership")]
    public async Task Recovery_rejects_wrong_purposes_invalidated_tokens_and_changed_accounts(string change)
    {
        var (tenant, user) = await database.SeedIdentity();
        using var identityScope = database.Services.CreateScope();
        var users = identityScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var token = change == "confirmation" ? await users.GenerateEmailConfirmationTokenAsync(user) : await users.GeneratePasswordResetTokenAsync(user);
        var job = RecoveryJob(database.Protection, tenant.Id, user, token, change == "purpose" ? "FirstBlick.Identity.Invitation.v1" : OutboxProcessor.RecoveryPurpose);
        await using var seed = database.Open();
        seed.Db.Jobs.Add(job); await seed.Db.SaveChangesAsync();
        if (change == "stamp")
            await seed.Db.Users.Where(x => x.Id == user.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.SecurityStamp, Guid.NewGuid().ToString()));
        if (change == "email")
            await seed.Db.Users.Where(x => x.Id == user.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.Email, "changed@example.test"));
        if (change == "membership")
            await seed.Db.Memberships.Where(x => x.TenantId == tenant.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.Active, false));
        var sender = new SyntheticSender();
        await using var worker = database.Open(sender, users: users);
        Assert.True(await worker.Processor.ProcessNext());
        var result = await database.ReadJob(job.Id);
        Assert.Equal(JobState.Failed, result.State); Assert.False(result.ExternalAttemptStarted); Assert.Empty(sender.Messages);
    }

    [WorkerPostgresFact]
    public async Task Daily_notifications_deduplicate_concurrent_schedulers_and_include_only_currently_authorized_own_work()
    {
        var (tenant, user) = await database.SeedIdentity(BusinessRole.Worker);
        var today = DateTime.UtcNow.Date;
        var preference = new NotificationPreference
        {
            TenantId = tenant.Id, UserId = user.Id, TimeZone = "UTC", LocalTime = TimeOnly.MinValue,
            Channels = [CommunicationChannel.Email, CommunicationChannel.Push], Days = Enum.GetValues<DayOfWeek>().ToList()
        };
        var own = new Employee { TenantId = tenant.Id, UserId = user.Id, Name = "Own synthetic employee" };
        var other = new Employee { TenantId = tenant.Id, UserId = Guid.NewGuid(), Name = "Other synthetic employee" };
        await using (var seed = database.Open())
        {
            seed.Db.Entities.AddRange(Stored(preference), Stored(own), Stored(other),
                Stored(new ScheduleAssignment { TenantId = tenant.Id, EmployeeId = own.Id, ProjectId = Guid.NewGuid(), Title = "Eigener Einsatz", Start = today.AddHours(8), End = today.AddHours(16) }),
                Stored(new ScheduleAssignment { TenantId = tenant.Id, EmployeeId = other.Id, ProjectId = Guid.NewGuid(), Title = "PRIVATE_OTHER_EMPLOYEE", Start = today.AddHours(8), End = today.AddHours(16) }),
                Stored(new TimeEntry { TenantId = tenant.Id, EmployeeId = own.Id, ProjectId = Guid.NewGuid(), Activity = "Synthetic work", Start = today.AddDays(-1).AddHours(8), End = today.AddDays(-1).AddHours(16), Breaks = [new(today.AddDays(-1).AddHours(12), today.AddDays(-1).AddHours(12.5))] }),
                Stored(new TimeEntry { TenantId = tenant.Id, EmployeeId = other.Id, ProjectId = Guid.NewGuid(), Activity = "PRIVATE_OTHER_TIME", Start = today.AddDays(-1).AddHours(1), End = today.AddDays(-1).AddHours(23) }),
                Stored(new MaintenanceContract { TenantId = tenant.Id, CustomerId = Guid.NewGuid(), BuildingId = Guid.NewGuid(), Title = "PRIVATE_MAINTENANCE", Scope = "Synthetic maintenance scope", StartDate = DateOnly.FromDateTime(today), NextDueDate = DateOnly.FromDateTime(today) }));
            var foreign = new BusinessTenant { Name = "Foreign synthetic tenant" }; seed.Db.Tenants.Add(foreign);
            seed.Db.Entities.Add(Stored(new ScheduleAssignment { TenantId = foreign.Id, EmployeeId = own.Id, ProjectId = Guid.NewGuid(), Title = "PRIVATE_FOREIGN_TENANT", Start = today.AddHours(8), End = today.AddHours(16) }));
            await seed.Db.SaveChangesAsync();
        }
        var counts = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var scheduler = database.Open(); return await scheduler.Processor.ScheduleDueNotifications();
        }));
        Assert.Equal(2, counts.Sum());
        var sender = new SyntheticSender(); await using var worker = database.Open(sender);
        Assert.Equal(0, await worker.Processor.ScheduleDueNotifications());
        Assert.True(await worker.Processor.ProcessNext()); Assert.True(await worker.Processor.ProcessNext());
        Assert.False(await worker.Processor.ProcessNext());
        var message = Assert.Single(sender.Messages);
        Assert.Equal(user.Email, message.Recipient); Assert.Contains("Eigener Einsatz", message.Body); Assert.Contains("7,50 Stunden", message.Body);
        Assert.DoesNotContain("PRIVATE_", message.Body);
        var jobs = await worker.Db.Jobs.IgnoreQueryFilters().Where(x => x.Type == "notification.daily").ToListAsync();
        Assert.Single(jobs, x => x.State == JobState.Completed);
        Assert.Single(jobs, x => x.State == JobState.Failed && x.LastError == "channel_unconfigured" && !x.ExternalAttemptStarted);
        Assert.Equal(2, await worker.Db.Receipts.IgnoreQueryFilters().CountAsync());
    }

    [WorkerPostgresFact]
    public async Task A_disabled_notification_preference_cancels_queued_send_at_preflight()
    {
        var (tenant, user) = await database.SeedIdentity();
        var preference = new NotificationPreference { TenantId = tenant.Id, UserId = user.Id, TimeZone = "UTC", LocalTime = TimeOnly.MinValue, Channels = [CommunicationChannel.Email], Days = Enum.GetValues<DayOfWeek>().ToList() };
        var sender = new SyntheticSender(); await using var worker = database.Open(sender);
        worker.Db.Entities.Add(Stored(preference)); await worker.Db.SaveChangesAsync();
        Assert.Equal(1, await worker.Processor.ScheduleDueNotifications());
        preference.MorningBriefing = false;
        var changed = AppJson.Serialize(preference);
        await worker.Db.Entities.IgnoreQueryFilters().Where(x => x.TenantId == tenant.Id && x.Id == preference.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.Data, changed));
        Assert.True(await worker.Processor.ProcessNext()); Assert.Empty(sender.Messages);
        var result = await worker.Db.Jobs.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(JobState.Failed, result.State); Assert.Equal("notification_preference_changed", result.LastError);
        Assert.False(result.ExternalAttemptStarted);
    }

    private static OutboxJob InternalJob() => new() { TenantId = Guid.NewGuid(), ActorId = Guid.NewGuid(), Type = "entity.changed", Kind = "synthetic", EntityId = Guid.NewGuid() };

    private static Task Expire(FirstBlickDbContext db, Guid id) => db.Jobs.IgnoreQueryFilters().Where(x => x.Id == id)
        .ExecuteUpdateAsync(x => x.SetProperty(y => y.LeaseUntil, DateTimeOffset.UtcNow.AddMinutes(-1)));

    private static OutboxJob RecoveryJob(IDataProtectionProvider provider, Guid tenantId, ApplicationUser user, string token, string purpose = OutboxProcessor.RecoveryPurpose)
        => new()
        {
            TenantId = tenantId, ActorId = Guid.Empty, EntityId = user.Id, Type = "identity.recovery", Kind = "identity",
            Payload = AppJson.Serialize(new { protectedMessage = provider.CreateProtector(purpose).Protect(AppJson.Serialize(new { email = user.Email, token, userId = user.Id })) })
        };

    private static StoredEntity Stored<T>(T entity) where T : TenantEntity
    {
        entity.Validate();
        return new() { TenantId = entity.TenantId, Kind = BusinessRegistry.KindOf<T>(), Id = entity.Id, Revision = entity.Revision, Data = AppJson.Serialize(entity), CreatedAt = entity.CreatedAt, UpdatedAt = entity.UpdatedAt, Archived = entity.Archived };
    }

    private sealed class SyntheticSender : IOutboxEmailSender
    {
        public bool Configured { get; set; } = true;
        public Exception? ConfigurationCheckFailure { get; set; }
        public bool IsConfigured => ConfigurationCheckFailure is { } error ? throw error : Configured;
        public ConcurrentQueue<OutboxEmail> Messages { get; } = new();
        public Func<OutboxEmail, CancellationToken, Task<string>>? OnSend { get; set; }
        public Task<string> Send(OutboxEmail message, CancellationToken cancellationToken)
        {
            Messages.Enqueue(message);
            return OnSend?.Invoke(message, cancellationToken) ?? Task.FromResult("synthetic:accepted");
        }
    }

    private sealed class WorkerSession : IAsyncDisposable
    {
        public FirstBlickDbContext Db { get; }
        public OutboxProcessor Processor { get; }
        public WorkerSession(WorkerDatabase database, IOutboxEmailSender? sender, IDataProtectionProvider? protection, UserManager<ApplicationUser>? users)
        {
            var tenant = new TenantScope();
            Db = new(new DbContextOptionsBuilder<FirstBlickDbContext>().UseNpgsql(database.ConnectionString).Options, tenant);
            Processor = new(Db, tenant, database.Configuration, protection ?? database.Protection,
                NullLogger<OutboxProcessor>.Instance, sender ?? new SyntheticSender(), users: users);
        }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class WorkerDatabase : IAsyncDisposable
    {
        private readonly string name;
        private readonly string adminConnectionString;
        public string ConnectionString { get; }
        public IConfiguration Configuration { get; } = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["App:BaseUrl"] = "https://firstblick.example.test" }).Build();
        public ServiceProvider Services { get; private set; } = null!;
        public IDataProtectionProvider Protection => Services.GetRequiredService<IDataProtectionProvider>();
        private WorkerDatabase(string source)
        {
            name = "firstblick_test_worker_" + Guid.NewGuid().ToString("N");
            var settings = new NpgsqlConnectionStringBuilder(source) { Database = "postgres", Pooling = false };
            adminConnectionString = settings.ConnectionString;
            settings.Database = name; ConnectionString = settings.ConnectionString;
        }
        public static async Task<WorkerDatabase> Create()
        {
            var source = Environment.GetEnvironmentVariable("FIRSTBLICK_TEST_POSTGRES")
                         ?? throw new InvalidOperationException("FIRSTBLICK_TEST_POSTGRES is required for PostgreSQL worker tests.");
            var database = new WorkerDatabase(source);
            await using var connection = new NpgsqlConnection(database.adminConnectionString); await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{database.name}\"", connection); await command.ExecuteNonQueryAsync();
            try
            {
                database.Services = database.BuildServices();
                await using var worker = database.Open(); await worker.Db.Database.EnsureCreatedAsync(); return database;
            }
            catch { await database.DisposeAsync(); throw; }
        }
        public ServiceProvider BuildServices()
        {
            var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton(Configuration); services.AddScoped<TenantScope>();
            services.AddDbContext<FirstBlickDbContext>(options => options.UseNpgsql(ConnectionString));
            services.AddIdentityCore<ApplicationUser>().AddEntityFrameworkStores<FirstBlickDbContext>().AddDefaultTokenProviders();
            services.AddDataProtection().SetApplicationName("FirstBlick").PersistKeysToDbContext<FirstBlickDbContext>();
            return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        }
        public WorkerSession Open(IOutboxEmailSender? sender = null, IDataProtectionProvider? protection = null, UserManager<ApplicationUser>? users = null)
            => new(this, sender, protection, users);
        public async Task<(BusinessTenant Tenant, ApplicationUser User)> SeedIdentity(BusinessRole role = BusinessRole.Office)
        {
            await using var worker = Open();
            var tenant = new BusinessTenant { Name = "Synthetic worker test tenant" };
            var id = Guid.NewGuid(); var email = $"synthetic-{id:N}@example.test";
            var user = new ApplicationUser { Id = id, Email = email, NormalizedEmail = email.ToUpperInvariant(), UserName = email, NormalizedUserName = email.ToUpperInvariant(), EmailConfirmed = true, DisplayName = "Synthetic test user", SecurityStamp = Guid.NewGuid().ToString() };
            worker.Db.Tenants.Add(tenant); worker.Db.Users.Add(user);
            worker.Db.Memberships.Add(new() { TenantId = tenant.Id, UserId = id, Role = role });
            await worker.Db.SaveChangesAsync(); return (tenant, user);
        }
        public async Task<(Quote Quote, DeliveryAttempt Delivery, OutboxJob Job)> SeedQuote()
        {
            var (tenant, user) = await SeedIdentity();
            var quote = new Quote { TenantId = tenant.Id, CustomerId = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Title = "Synthetic roof work", Lines = [new() { Description = "Geplante Dachfläche", Quantity = 2, UnitMaterialCost = 100, MarkupPercent = 25, VatPercent = 19 }] };
            CommercialWorkflow.IssueQuote(quote, "A-TEST-0001");
            CommercialWorkflow.FreezePresentation(quote, tenant.Name, new Customer { TenantId = tenant.Id, Id = quote.CustomerId, Name = "Synthetic customer", Address = "Synthetic address" }, DateTimeOffset.UtcNow);
            var delivery = CommercialWorkflow.RequestDelivery(quote, CommunicationChannel.Email, "customer@example.test", DateTimeOffset.UtcNow);
            var job = new OutboxJob { TenantId = tenant.Id, ActorId = user.Id, Type = "quote.delivery", Kind = "quotes", EntityId = quote.Id, EntityRevision = quote.Revision,
                Payload = AppJson.Serialize(new { deliveryId = delivery.Id, snapshotDigest = quote.IssuedDigest, channel = delivery.Channel, recipient = delivery.Recipient }) };
            await using var worker = Open(); worker.Db.Entities.Add(Stored(quote)); worker.Db.Jobs.Add(job); await worker.Db.SaveChangesAsync(); return (quote, delivery, job);
        }
        public async Task<OutboxJob> ReadJob(Guid id)
        {
            await using var worker = Open(); return await worker.Db.Jobs.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == id);
        }
        public async Task<StoredEntity> ReadStored(Quote quote)
        {
            await using var worker = Open(); return await worker.Db.Entities.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.TenantId == quote.TenantId && x.Kind == "quotes" && x.Id == quote.Id);
        }
        public async Task<Quote> ReadQuote(Quote quote) => AppJson.Read<Quote>((await ReadStored(quote)).Data);
        public async Task ReplaceQuote(Quote quote)
        {
            quote.Validate(); var data = AppJson.Serialize(quote); await using var worker = Open();
            await worker.Db.Entities.IgnoreQueryFilters().Where(x => x.TenantId == quote.TenantId && x.Kind == "quotes" && x.Id == quote.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.Data, data));
        }
        public async ValueTask DisposeAsync()
        {
            if (Services is not null) await Services.DisposeAsync();
            if (!name.StartsWith("firstblick_test_worker_", StringComparison.Ordinal) || name.Length != "firstblick_test_worker_".Length + 32)
                throw new InvalidOperationException("Refusing to drop a database outside this worker test fixture.");
            await using var connection = new NpgsqlConnection(adminConnectionString); await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", connection); await command.ExecuteNonQueryAsync();
        }
    }
}

public sealed class WorkerPostgresFactAttribute : FactAttribute
{
    public WorkerPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FIRSTBLICK_TEST_POSTGRES")))
            Skip = "Set FIRSTBLICK_TEST_POSTGRES to an isolated PostgreSQL test server; SQLite cannot verify SKIP LOCKED or JSONB semantics.";
    }
}

public sealed class WorkerPostgresTheoryAttribute : TheoryAttribute
{
    public WorkerPostgresTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FIRSTBLICK_TEST_POSTGRES")))
            Skip = "Set FIRSTBLICK_TEST_POSTGRES to an isolated PostgreSQL test server; SQLite cannot verify SKIP LOCKED or JSONB semantics.";
    }
}
