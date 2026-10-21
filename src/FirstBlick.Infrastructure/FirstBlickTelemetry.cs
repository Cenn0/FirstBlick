using System.Diagnostics;
using System.Diagnostics.Metrics;
using FirstBlick.Core.Business;
using FirstBlick.Infrastructure.Persistence;

namespace FirstBlick.Infrastructure;

/// <summary>Process-local instruments. Only bounded code-owned dimensions enter metrics or custom spans.</summary>
public sealed class FirstBlickTelemetry : IDisposable
{
    public const string MeterName = "FirstBlick.Operations";
    public const string ActivitySourceName = "FirstBlick.Operations";
    private readonly ActivitySource activities = new(ActivitySourceName, "1.0.0");
    private readonly Counter<long> commands;
    private readonly Histogram<double> commandDuration;
    private readonly Histogram<double> lockDuration;
    private readonly Counter<long> jobTransitions;

    public FirstBlickTelemetry(IMeterFactory factory)
    {
        var meter = factory.Create(MeterName);
        commands = meter.CreateCounter<long>("firstblick.command.completed", "{command}", "Completed command attempts, classified only after the database operation ends.");
        commandDuration = meter.CreateHistogram<double>("firstblick.command.duration", "s", "Time spent validating, waiting and executing one command attempt.");
        lockDuration = meter.CreateHistogram<double>("firstblick.command.lock_wait", "s", "Time awaiting the business transaction lock.");
        jobTransitions = meter.CreateCounter<long>("firstblick.outbox.transition", "{transition}", "Persisted outbox transitions committed by this process; not provider delivery confirmations.");
    }

    public CommandObservation ObserveCommand(string kind) => new(this, SafeKind(kind));

    public void RecordLockWait(string kind, TimeSpan duration)
        => lockDuration.Record(Math.Max(0, duration.TotalSeconds), new KeyValuePair<string, object?>("resource.kind", SafeKind(kind)));

    public void RecordJobTransition(string type, JobState state)
        => jobTransitions.Add(1, new KeyValuePair<string, object?>("job.type", type switch
        {
            "entity.changed" or "quote.delivery" or "identity.recovery" or "notification.daily" => type,
            _ => "other"
        }), new KeyValuePair<string, object?>("job.state", Enum.IsDefined(state) ? state.ToString().ToLowerInvariant() : "other"));

    private static string SafeKind(string kind) => BusinessRegistry.Entities.ContainsKey(kind) ? kind : kind switch
    {
        "documents" or "settings" or "memberships" or "approvals" or "jobs" or "audit" or "portal-access" => kind,
        _ => "other"
    };

    public void Dispose() => activities.Dispose(); // IMeterFactory owns the meter lifetime.

    public sealed class CommandObservation : IDisposable
    {
        private readonly FirstBlickTelemetry owner;
        private readonly string kind;
        private readonly long started = Stopwatch.GetTimestamp();
        private readonly Activity? activity;
        private string outcome = "failed";
        private bool disposed;

        internal CommandObservation(FirstBlickTelemetry owner, string kind)
        {
            this.owner = owner; this.kind = kind;
            activity = owner.activities.StartActivity("firstblick.command", ActivityKind.Internal);
            activity?.SetTag("resource.kind", kind);
        }

        public void Committed(bool replayed) => outcome = replayed ? "replayed" : "committed";
        public void Rejected() => outcome = "rejected";
        public void Cancelled() => outcome = "cancelled";

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            var duration = Stopwatch.GetElapsedTime(started).TotalSeconds;
            var resourceTag = new KeyValuePair<string, object?>("resource.kind", kind);
            var outcomeTag = new KeyValuePair<string, object?>("command.outcome", outcome);
            owner.commands.Add(1, resourceTag, outcomeTag);
            owner.commandDuration.Record(duration, resourceTag, outcomeTag);
            activity?.SetTag("command.outcome", outcome);
            activity?.SetStatus(outcome == "failed" ? ActivityStatusCode.Error : ActivityStatusCode.Ok);
            activity?.Dispose();
        }
    }
}
