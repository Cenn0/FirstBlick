using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Core.Technical;
using FirstBlick.Infrastructure.Persistence;
using FirstBlick.Infrastructure.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static FirstBlick.Api.Tests.PostgresApiFixture;

namespace FirstBlick.Api.Tests;

/// <summary>Regressions identified by an independent review: current authority applies to the cached result and to long requests.</summary>
public sealed class AuthorizationRegressionTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    [PostgresApiFact]
    public async Task Site_manager_cannot_remove_an_existing_staff_account_link()
    {
        var tenant = await fixture.CreateTenant();
        var director = await fixture.CreateUser(tenant.Id);
        var person = await fixture.CreateUser(tenant.Id, BusinessRole.Worker);
        var manager = await fixture.CreateUser(tenant.Id, BusinessRole.SiteManager);
        var employee = await fixture.Save(new Employee { TenantId = tenant.Id, Name = "Linked synthetic staff", UserId = person.UserId }, director.UserId);
        var client = await fixture.Login(manager);

        var response = await Send(client, HttpMethod.Put, $"/api/v1/employees/{employee.Id}",
            new { name = employee.Name, userId = (Guid?)null, active = true, weeklyHours = 40 }, employee.Revision);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var stored = await fixture.Read<Employee>(tenant.Id, employee.Id);
        Assert.Equal(person.UserId, stored.UserId);
        Assert.Equal(employee.Revision, stored.Revision);
    }

    [PostgresApiFact]
    public async Task Removing_a_project_assignment_also_blocks_replaying_its_cached_defect_response()
    {
        var work = await AssignedWork(BusinessRole.Worker);
        var client = await fixture.Login(work.Person);
        var key = Guid.NewGuid().ToString("N");
        var input = new { projectId = work.Project.Id, title = "Synthetic inspection observation", description = "Private project observation" };
        var defect = await Ok<Defect>(Send(client, HttpMethod.Post, "/api/v1/defects", input, key: key));

        work.Assignment.Cancelled = true;
        await SaveExisting(work.Assignment, work.Director.UserId);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/defects/{defect.Id}")).StatusCode);

        var replay = await Send(client, HttpMethod.Post, "/api/v1/defects", input, key: key);
        Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
        Assert.False(replay.Headers.Contains("Idempotency-Replayed"));
        Assert.DoesNotContain(input.description, await replay.Content.ReadAsStringAsync());
        await AssertOneReceipt(work.Person, key);
    }

    [PostgresApiFact]
    public async Task Deactivating_the_staff_profile_also_blocks_replaying_its_cached_diary_response()
    {
        var work = await AssignedWork(BusinessRole.Worker);
        var client = await fixture.Login(work.Person);
        var key = Guid.NewGuid().ToString("N");
        var input = new { projectId = work.Project.Id, employeeId = work.Employee.Id, date = "2026-10-20", work = "Synthetic private diary evidence" };
        var entry = await Ok<DiaryEntry>(Send(client, HttpMethod.Post, "/api/v1/diary-entries", input, key: key));

        work.Employee.Active = false;
        await SaveExisting(work.Employee, work.Director.UserId);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/diary-entries/{entry.Id}")).StatusCode);

        var replay = await Send(client, HttpMethod.Post, "/api/v1/diary-entries", input, key: key);
        Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
        Assert.False(replay.Headers.Contains("Idempotency-Replayed"));
        Assert.DoesNotContain(input.work, await replay.Content.ReadAsStringAsync());
        await AssertOneReceipt(work.Person, key);
    }

    [PostgresApiFact]
    public async Task A_former_director_cannot_replay_an_invitation_response_after_role_demotion()
    {
        var tenant = await fixture.CreateTenant();
        var director = await fixture.CreateUser(tenant.Id);
        var client = await fixture.Login(director);
        var key = Guid.NewGuid().ToString("N");
        var input = new { email = $"synthetic-invitation-{Guid.NewGuid():N}@example.test", role = "Worker" };
        var original = await Send(client, HttpMethod.Post, "/api/v1/members/invitations", input, key: key);
        Assert.True(original.IsSuccessStatusCode, await original.Content.ReadAsStringAsync());
        using var originalJson = JsonDocument.Parse(await original.Content.ReadAsStringAsync());
        var token = originalJson.RootElement.GetProperty("token").GetString()!;

        // Retain a director in the business; only this actor loses the privileged operation.
        _ = await fixture.CreateUser(tenant.Id);
        await ChangeRole(director, BusinessRole.Office);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/members")).StatusCode);

        var replay = await Send(client, HttpMethod.Post, "/api/v1/members/invitations", input, key: key);
        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        Assert.False(replay.Headers.Contains("Idempotency-Replayed"));
        Assert.DoesNotContain(token, await replay.Content.ReadAsStringAsync());
        await AssertOneReceipt(director, key);
    }

    [PostgresApiFact]
    public async Task Upload_rechecks_ownership_after_body_transfer_and_role_demotion()
    {
        var work = await AssignedWork(BusinessRole.Office);
        var bytes = Encoding.UTF8.GetBytes("%PDF-1.7\nSynthetic authorization race fixture.\n");
        var doc = new DocumentObject
        {
            TenantId = work.Person.TenantId, UploadedBy = work.Director.UserId,
            Name = "synthetic-authorization.pdf", ContentType = "application/pdf", Size = bytes.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), ProjectId = work.Project.Id, ProjectKind = "projects"
        };
        doc.ObjectKey = $"{doc.TenantId:N}/{doc.Id:N}/original";
        await fixture.InScope(async services =>
        {
            services.GetRequiredService<FirstBlickDbContext>().Documents.Add(doc);
            await services.GetRequiredService<FirstBlickDbContext>().SaveChangesAsync();
        });
        var gate = new BodyReadGate($"/api/v1/documents/{doc.Id}/content");
        var objects = new CountingObjectStore();
        await using var factory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IObjectStore>(); services.AddSingleton<IObjectStore>(objects);
            services.AddSingleton<IStartupFilter>(new GateStartupFilter(gate));
        }));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });
        await LoginOn(client, work.Person);
        using var request = new HttpRequestMessage(HttpMethod.Put, gate.Path) { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        request.Headers.IfMatch.ParseAdd($"\"{doc.Revision}\"");
        var key = Guid.NewGuid().ToString("N"); request.Headers.Add("Idempotency-Key", key);

        var pending = client.SendAsync(request);
        try
        {
            // The server reached Request.Body.ReadAsync after the initial ownership check.
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await ChangeRole(work.Person, BusinessRole.Worker);
        }
        finally { gate.Release.TrySetResult(); }
        var response = await pending;

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, objects.PutCount);
        await fixture.InScope(async services =>
        {
            services.GetRequiredService<TenantScope>().TenantId = doc.TenantId;
            var db = services.GetRequiredService<FirstBlickDbContext>();
            var stored = await db.Documents.SingleAsync(x => x.Id == doc.Id);
            Assert.Equal("Staged", stored.State); Assert.Null(stored.VersionId); Assert.Equal(doc.Revision, stored.Revision);
            Assert.False(await db.Receipts.AnyAsync(x => x.ActorId == work.Person.UserId && x.Key == key));
        });
    }

    [PostgresApiFact]
    public async Task A_project_cannot_move_a_published_roof_and_its_measurement_to_another_building()
    {
        var tenant = await fixture.CreateTenant(); var director = await fixture.CreateUser(tenant.Id);
        var project = await ProjectFor(tenant.Id, director.UserId);
        var anotherBuilding = await fixture.Save(new Building
        { TenantId = tenant.Id, CustomerId = project.CustomerId, Name = "Different synthetic building", Address = "Synthetic Street 2" }, director.UserId);
        var roof = new RoofRevision
        {
            TenantId = tenant.Id, BuildingId = project.BuildingId, ProjectId = project.Id, Title = "Synthetic published roof",
            Surfaces = [new() { Name = "Actual plane", Geometry = new PolygonRoofInput([new(0, 0), new(10, 0), new(10, 8), new(0, 8)], [], "Synthetic 10 by 8 metre rectangle") }]
        };
        RoofWorkflow.Publish(roof, null, DateTimeOffset.UtcNow);
        await fixture.Save(roof, director.UserId);
        var measurement = await fixture.Save(new Measurement
        {
            TenantId = tenant.Id, ProjectId = project.Id, RoofRevisionId = roof.Id, Title = "Synthetic measurement",
            Lines = [new() { Description = "Roof area", MeasurementSource = "Published synthetic roof", Input = new SurfaceNetArea(roof.Surfaces[0].Id) }]
        }, director.UserId);
        var client = await fixture.Login(director);

        var response = await Send(client, HttpMethod.Put, $"/api/v1/projects/{project.Id}",
            new { project.CustomerId, buildingId = anotherBuilding.Id, project.Name, type = "Renovation", status = "Planning" }, project.Revision);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(project.BuildingId, (await fixture.Read<Project>(tenant.Id, project.Id)).BuildingId);
        var frozen = await Ok<Measurement>(Send(client, HttpMethod.Post, $"/api/v1/measurements/{measurement.Id}/freeze", revision: measurement.Revision));
        Assert.Equal(roof.Snapshot!.Digest, frozen.Snapshot!.RoofDigest);
        Assert.Equal(80m, Assert.Single(frozen.Snapshot.Lines).Quantity);
    }

    [PostgresApiFact]
    public async Task Persisted_technical_evidence_survives_JSONB_normalization_of_exponential_numbers()
    {
        var tenant = await fixture.CreateTenant(); var director = await fixture.CreateUser(tenant.Id);
        var project = await ProjectFor(tenant.Id, director.UserId);
        var client = await fixture.Login(director);
        var created = await Ok<TechnicalRecord>(Send(client, HttpMethod.Post, "/api/v1/technical/runs", new
        {
            projectId = project.Id, title = "Synthetic thin layer evidence", method = "thermal",
            input = new
            {
                insideTemperatureC = 20, outsideTemperatureC = 0,
                insideSurfaceResistanceM2KW = 0.13, outsideSurfaceResistanceM2KW = 0.04,
                layers = new[] { new { name = "Synthetic thin conductor", thicknessM = 0.000001, conductivityWmK = 500, valueSource = "Elementary reference fixture" } }
            }
        }));

        // PostgreSQL JSONB renders 1E-06 as 0.000001 and 2E-09 as 0.000000002.
        // Equivalent numeric spelling must not invalidate independently calculated evidence.
        var stored = await fixture.Read<TechnicalRecord>(tenant.Id, created.Id);
        Assert.Equal(created.Digest, stored.Digest);
        stored.Validate();
        var resistance = stored.Result.GetProperty("layers")[0].GetProperty("resistanceM2KW").GetDouble();
        Assert.Equal(0.000000002, resistance, 15);
    }

    [PostgresApiFact]
    public async Task Concurrent_recovery_requests_enqueue_only_one_message_for_the_account_cooldown()
    {
        var tenant = await fixture.CreateTenant();
        var person = await fixture.CreateUser(tenant.Id, BusinessRole.Worker);
        using var client = fixture.Client(); await RefreshCsrf(client);

        var replies = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            Send(client, HttpMethod.Post, "/api/v1/auth/recover", new { person.Email })));

        foreach (var reply in replies) Assert.Equal(HttpStatusCode.Accepted, reply.StatusCode);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            Assert.Equal(1, await db.Jobs.IgnoreQueryFilters().CountAsync(x => x.Type == "identity.recovery" && x.EntityId == person.UserId));
        });
    }

    [PostgresApiFact]
    public async Task Equivalent_signed_zero_survives_persisted_technical_provenance_validation()
    {
        var tenant = await fixture.CreateTenant(); var director = await fixture.CreateUser(tenant.Id);
        var project = await ProjectFor(tenant.Id, director.UserId);
        var client = await fixture.Login(director);
        var created = await Ok<TechnicalRecord>(Send(client, HttpMethod.Post, "/api/v1/technical/runs", new
        {
            projectId = project.Id, title = "Synthetic zero boundary evidence", method = "thermal",
            input = new
            {
                insideTemperatureC = BitConverter.Int64BitsToDouble(long.MinValue), outsideTemperatureC = 0,
                insideSurfaceResistanceM2KW = 0.13, outsideSurfaceResistanceM2KW = 0.04,
                layers = new[] { new { name = "Synthetic layer", thicknessM = 0.2, conductivityWmK = 0.04, valueSource = "Elementary reference fixture" } }
            }
        }));

        var stored = await fixture.Read<TechnicalRecord>(tenant.Id, created.Id);
        Assert.Equal(created.Digest, stored.Digest);
        stored.Validate();
        Assert.Equal(0, stored.Result.GetProperty("heatFluxWm2").GetDouble());
    }

    private async Task<AssignedContext> AssignedWork(BusinessRole role)
    {
        var tenant = await fixture.CreateTenant();
        var director = await fixture.CreateUser(tenant.Id);
        var person = await fixture.CreateUser(tenant.Id, role);
        var project = await ProjectFor(tenant.Id, director.UserId);
        var employee = await fixture.Save(new Employee
        { TenantId = tenant.Id, Name = "Assigned synthetic staff", UserId = person.UserId }, director.UserId);
        var assignment = await fixture.Save(new ScheduleAssignment
        {
            TenantId = tenant.Id, ProjectId = project.Id, EmployeeId = employee.Id, Title = "Synthetic assignment",
            Start = new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero), End = new DateTimeOffset(2026, 10, 20, 16, 0, 0, TimeSpan.Zero)
        }, director.UserId);
        return new(director, person, project, employee, assignment);
    }

    private async Task<Project> ProjectFor(Guid tenantId, Guid actorId)
    {
        var customer = await fixture.Save(new Customer { TenantId = tenantId, Name = "Synthetic customer" }, actorId);
        var building = await fixture.Save(new Building
        { TenantId = tenantId, CustomerId = customer.Id, Name = "Synthetic building", Address = "Synthetic Street 1" }, actorId);
        return await fixture.Save(new Project
        { TenantId = tenantId, CustomerId = customer.Id, BuildingId = building.Id, Name = "Synthetic project", Status = ProjectStatus.Planning }, actorId);
    }

    private async Task SaveExisting(TenantEntity entity, Guid actorId) => await fixture.InScope(async services =>
    {
        var scope = services.GetRequiredService<TenantScope>(); scope.TenantId = entity.TenantId; scope.UserId = actorId; scope.Role = BusinessRole.ManagingDirector;
        var db = services.GetRequiredService<FirstBlickDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({entity.TenantId.ToString()}, 0))");
        await services.GetRequiredService<EntityStore>().Save(entity, "test.authority.change", false);
        await db.SaveChangesAsync(); await transaction.CommitAsync();
    });

    private async Task ChangeRole(ApiTestUser person, BusinessRole role) => await fixture.InScope(async services =>
    {
        var db = services.GetRequiredService<FirstBlickDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({person.TenantId.ToString()}, 0))");
        var member = await db.Memberships.SingleAsync(x => x.TenantId == person.TenantId && x.UserId == person.UserId);
        member.Role = role; member.Revision++; await db.SaveChangesAsync(); await transaction.CommitAsync();
    });

    private async Task AssertOneReceipt(ApiTestUser person, string key) => await fixture.InScope(async services =>
    {
        services.GetRequiredService<TenantScope>().TenantId = person.TenantId;
        Assert.Equal(1, await services.GetRequiredService<FirstBlickDbContext>().Receipts.CountAsync(x => x.ActorId == person.UserId && x.Key == key));
    });

    private static async Task LoginOn(HttpClient client, ApiTestUser user)
    {
        client.DefaultRequestHeaders.Add("X-Tenant-Id", user.TenantId.ToString());
        await RefreshCsrf(client);
        var response = await Send(client, HttpMethod.Post, "/api/v1/auth/login", new { user.Email, user.Password });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        await RefreshCsrf(client);
    }

    private static async Task<T> Ok<T>(Task<HttpResponseMessage> pending)
    {
        var response = await pending; var json = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, json); return AppJson.Read<T>(json);
    }

    private sealed record AssignedContext(ApiTestUser Director, ApiTestUser Person, Project Project, Employee Employee, ScheduleAssignment Assignment);

    private sealed class BodyReadGate(string path)
    {
        public string Path { get; } = path;
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task Wait(CancellationToken ct)
        {
            Reached.TrySetResult(); await Release.Task.WaitAsync(ct);
        }
    }

    private sealed class GateStartupFilter(BodyReadGate gate) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                if (context.Request.Method == "PUT" && context.Request.Path == gate.Path)
                    context.Request.Body = new GatedReadStream(context.Request.Body, gate);
                await continuation(context);
            });
            next(app);
        };
    }

    private sealed class GatedReadStream(Stream source, BodyReadGate gate) : Stream
    {
        public override bool CanRead => source.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("The endpoint must read its request asynchronously.");
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { await gate.Wait(ct); return await source.ReadAsync(buffer, ct); }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        { await gate.Wait(ct); return await source.ReadAsync(buffer.AsMemory(offset, count), ct); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CountingObjectStore : IObjectStore
    {
        public bool Configured => true;
        public int PutCount { get; private set; }
        private long size;
        public async Task<string?> Put(string key, Stream body, string contentType, CancellationToken ct)
        { PutCount++; using var destination = new MemoryStream(); await body.CopyToAsync(destination, ct); size = destination.Length; return "synthetic-v1"; }
        public Task<ObjectMetadata?> Head(string key, string? version, CancellationToken ct) => Task.FromResult<ObjectMetadata?>(new(size, "synthetic-v1"));
        public Task<(Stream Body, string ContentType)> Get(string key, string? version, CancellationToken ct) => throw new NotSupportedException();
    }
}
