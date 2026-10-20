using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using FirstBlick.Core.Business;
using FirstBlick.Infrastructure;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FirstBlick.Api.Tests;

public sealed class TelemetryTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    [Fact]
    public void Instrument_dimensions_are_bounded_and_custom_spans_never_contain_caller_identifiers()
    {
        using var services = new ServiceCollection().AddMetrics().AddSingleton<FirstBlickTelemetry>().BuildServiceProvider();
        var telemetry = services.GetRequiredService<FirstBlickTelemetry>();
        using var capture = new MetricCapture(services.GetRequiredService<IMeterFactory>());
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == FirstBlickTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { if (activity.GetTagItem("resource.kind")?.ToString() == "other") spans.Add(activity); }
        };
        ActivitySource.AddActivityListener(listener);
        using (var observation = telemetry.ObserveCommand("private@example.test/" + Guid.NewGuid())) observation.Rejected();
        telemetry.RecordJobTransition("private-job-" + Guid.NewGuid(), (JobState)999);
        Assert.Collection(capture.LongMeasurements,
            x => { Assert.Equal("firstblick.command.completed", x.Name); Assert.Equal("other", x.Tags["resource.kind"]); Assert.Equal("rejected", x.Tags["command.outcome"]); },
            x => { Assert.Equal("firstblick.outbox.transition", x.Name); Assert.Equal("other", x.Tags["job.type"]); Assert.Equal("other", x.Tags["job.state"]); });
        Assert.Single(spans); Assert.Equal("firstblick.command", spans[0].OperationName);
        Assert.Equal(2, spans[0].TagObjects.Count()); Assert.DoesNotContain("private", string.Join(' ', spans[0].TagObjects));
        Assert.All(capture.DoubleMeasurements, x => Assert.True(x.Value >= 0));
    }

    [PostgresApiFact]
    public async Task Commands_record_committed_replayed_and_rejected_attempts_separately()
    {
        var tenant = await fixture.CreateTenant(); var user = await fixture.CreateUser(tenant.Id); var client = await fixture.Login(user);
        using var capture = new MetricCapture(fixture.Factory.Services.GetRequiredService<IMeterFactory>());
        const string key = "synthetic-command-telemetry";
        var body = new { name = "Synthetic private customer", notes = "private@example.test" };
        var first = await PostgresApiFixture.Send(client, HttpMethod.Post, "/api/v1/customers", body, key: key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var replay = await PostgresApiFixture.Send(client, HttpMethod.Post, "/api/v1/customers", body, key: key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode); Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        var conflict = await PostgresApiFixture.Send(client, HttpMethod.Post, "/api/v1/customers", new { name = "Different body" }, key: key);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var commands = capture.LongMeasurements.Where(x => x.Name == "firstblick.command.completed").ToArray();
        Assert.Equal(new[] { "committed", "replayed", "rejected" }, commands.Select(x => x.Tags["command.outcome"]));
        Assert.All(commands, x => { Assert.Equal(1, x.Value); Assert.Equal("customers", x.Tags["resource.kind"]); Assert.Equal(2, x.Tags.Count); });
        Assert.Equal(3, capture.DoubleMeasurements.Count(x => x.Name == "firstblick.command.duration"));
        Assert.Equal(3, capture.DoubleMeasurements.Count(x => x.Name == "firstblick.command.lock_wait"));
        Assert.DoesNotContain("private", string.Join(' ', capture.LongMeasurements.SelectMany(x => x.Tags.Values)));
    }

    [PostgresApiFact]
    public async Task Outbox_counts_only_persisted_transitions_and_does_not_count_a_stale_claim_twice()
    {
        var tenant = await fixture.CreateTenant(); var user = await fixture.CreateUser(tenant.Id);
        using var capture = new MetricCapture(fixture.Factory.Services.GetRequiredService<IMeterFactory>());
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            var job = new OutboxJob { TenantId = tenant.Id, ActorId = user.UserId, Type = "entity.changed", Kind = "customers", AvailableAt = DateTimeOffset.UtcNow.AddSeconds(-1) };
            db.Jobs.Add(job); await db.SaveChangesAsync();
            var processor = services.GetRequiredService<OutboxProcessor>(); var claim = await processor.Claim();
            Assert.NotNull(claim); Assert.True(await processor.ProcessClaimed(claim!)); Assert.False(await processor.ProcessClaimed(claim!));
            Assert.Equal(JobState.Completed, (await db.Jobs.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == job.Id)).State);
        });
        var measurement = Assert.Single(capture.LongMeasurements, x => x.Name == "firstblick.outbox.transition");
        Assert.Equal(1, measurement.Value); Assert.Equal("entity.changed", measurement.Tags["job.type"]); Assert.Equal("completed", measurement.Tags["job.state"]);
    }

    private sealed record Measurement<T>(string Name, T Value, Dictionary<string, string?> Tags);
    private sealed class MetricCapture : IDisposable
    {
        private readonly MeterListener listener = new();
        public ConcurrentQueue<Measurement<long>> LongMeasurements { get; } = new();
        public ConcurrentQueue<Measurement<double>> DoubleMeasurements { get; } = new();
        public MetricCapture(IMeterFactory factory)
        {
            listener.InstrumentPublished = (instrument, source) =>
            {
                if (instrument.Meter.Name == FirstBlickTelemetry.MeterName && ReferenceEquals(instrument.Meter.Scope, factory)) source.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => LongMeasurements.Enqueue(new(instrument.Name, value, tags.ToArray().ToDictionary(x => x.Key, x => x.Value?.ToString()))));
            listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => DoubleMeasurements.Enqueue(new(instrument.Name, value, tags.ToArray().ToDictionary(x => x.Key, x => x.Value?.ToString()))));
            listener.Start();
        }
        public void Dispose() => listener.Dispose();
    }
}
