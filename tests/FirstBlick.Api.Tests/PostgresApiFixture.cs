using System.Text;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FirstBlick.Api.Tests;

public sealed record ApiTestUser(Guid TenantId, Guid UserId, string Email, string Password);

/// <summary>Real HTTP, Identity cookies, antiforgery, JSONB and tenant locks against a disposable PostgreSQL database.</summary>
public sealed class PostgresApiFixture : IAsyncLifetime
{
    private readonly string databaseName = "firstblick_test_api_" + Guid.NewGuid().ToString("N");
    private string adminConnectionString = "";
    private bool databaseCreated;
    private readonly List<HttpClient> clients = [];
    public string ConnectionString { get; private set; } = "";
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var source = Environment.GetEnvironmentVariable("FIRSTBLICK_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(source))
        {
            if (PostgresApiFactAttribute.Required) throw new InvalidOperationException("FIRSTBLICK_REQUIRE_POSTGRES is true but FIRSTBLICK_TEST_POSTGRES is missing.");
            return;
        }
        var settings = new NpgsqlConnectionStringBuilder(source) { Database = "postgres", Pooling = false };
        adminConnectionString = settings.ConnectionString;
        settings.Database = databaseName; ConnectionString = settings.ConnectionString;
        await using (var connection = new NpgsqlConnection(adminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
            await command.ExecuteNonQueryAsync(); databaseCreated = true;
        }
        try
        {
            // Create the schema before starting the host; its data-protection key ring may query PostgreSQL during startup.
            await using var db = new FirstBlickDbContext(new DbContextOptionsBuilder<FirstBlickDbContext>().UseNpgsql(ConnectionString).Options, new TenantScope());
            if (db.Database.GetMigrations().Any()) await db.Database.MigrateAsync();
            else await db.Database.EnsureCreatedAsync();
            Factory = new ApiFactory(ConnectionString);
        }
        catch { await DisposeAsync(); throw; }
    }

    public HttpClient Client()
    {
        var client = Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true });
        clients.Add(client); return client;
    }

    public async Task<BusinessTenant> CreateTenant(string? name = null, string[]? modules = null)
        => await InScope(async services =>
        {
            var tenant = new BusinessTenant { Name = name ?? "Synthetic API test business", Modules = modules ?? AccessRules.AllModules.ToArray(), TimeZone = "UTC" };
            var db = services.GetRequiredService<FirstBlickDbContext>(); db.Tenants.Add(tenant); await db.SaveChangesAsync(); return tenant;
        });

    public async Task<ApiTestUser> CreateUser(Guid tenantId, BusinessRole role = BusinessRole.ManagingDirector, string? email = null)
        => await InScope(async services =>
        {
            var id = Guid.NewGuid(); var address = email ?? $"synthetic-{id:N}@example.test";
            const string password = "Synthetic-Test-Password-2026!";
            var user = new ApplicationUser { Id = id, Email = address, UserName = address, EmailConfirmed = true, DisplayName = "Synthetic API test person" };
            var result = await services.GetRequiredService<UserManager<ApplicationUser>>().CreateAsync(user, password);
            Assert.True(result.Succeeded, string.Join(" ", result.Errors.Select(x => x.Description)));
            var db = services.GetRequiredService<FirstBlickDbContext>();
            db.Memberships.Add(new Membership { TenantId = tenantId, UserId = id, Role = role }); await db.SaveChangesAsync();
            return new ApiTestUser(tenantId, id, address, password);
        });

    public async Task<HttpClient> Login(ApiTestUser user)
    {
        var client = Client(); client.DefaultRequestHeaders.Add("X-Tenant-Id", user.TenantId.ToString());
        await RefreshCsrf(client);
        var login = await Send(client, HttpMethod.Post, "/api/v1/auth/login", new { user.Email, user.Password });
        Assert.True(login.IsSuccessStatusCode, await login.Content.ReadAsStringAsync());
        await RefreshCsrf(client); return client;
    }

    public static async Task RefreshCsrf(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/auth/csrf");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var cookie = response.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal));
        var token = Uri.UnescapeDataString(cookie["XSRF-TOKEN=".Length..].Split(';')[0]);
        client.DefaultRequestHeaders.Remove("X-XSRF-TOKEN"); client.DefaultRequestHeaders.Add("X-XSRF-TOKEN", token);
    }

    public static Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, object? body = null, long? revision = null, string? key = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(AppJson.Serialize(body), Encoding.UTF8, "application/json");
        if (revision.HasValue) request.Headers.IfMatch.ParseAdd($"\"{revision.Value}\"");
        if (method != HttpMethod.Get && method != HttpMethod.Head) request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        return client.SendAsync(request);
    }

    public async Task<T> Save<T>(T entity, Guid actorId) where T : TenantEntity
        => await InScope(async services =>
        {
            var scope = services.GetRequiredService<TenantScope>(); scope.TenantId = entity.TenantId; scope.UserId = actorId; scope.Role = BusinessRole.ManagingDirector;
            await services.GetRequiredService<EntityStore>().Save(entity, "test.seed", true);
            await services.GetRequiredService<FirstBlickDbContext>().SaveChangesAsync(); return entity;
        });

    public async Task<T> Read<T>(Guid tenantId, Guid id) where T : TenantEntity
        => await InScope(async services =>
        {
            services.GetRequiredService<TenantScope>().TenantId = tenantId;
            return await services.GetRequiredService<EntityStore>().Get<T>(id);
        });

    public async Task<T> InScope<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = Factory.Services.CreateAsyncScope(); return await action(scope.ServiceProvider);
    }
    public async Task InScope(Func<IServiceProvider, Task> action)
    {
        await using var scope = Factory.Services.CreateAsyncScope(); await action(scope.ServiceProvider);
    }

    public async Task DisposeAsync()
    {
        foreach (var client in clients) client.Dispose(); clients.Clear();
        if (Factory is not null) { await Factory.DisposeAsync(); Factory = null!; }
        if (!databaseCreated) return;
        if (!databaseName.StartsWith("firstblick_test_api_", StringComparison.Ordinal) || databaseName.Length != "firstblick_test_api_".Length + 32)
            throw new InvalidOperationException("Refusing to remove a database outside this API test fixture.");
        await using var connection = new NpgsqlConnection(adminConnectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync(); databaseCreated = false;
    }

    private sealed class ApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Database", connectionString);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = connectionString, ["Storage:Endpoint"] = "", ["App:BaseUrl"] = "https://localhost",
                ["Authentication:RequestsPerMinute"] = "1000",
                ["Logging:LogLevel:Default"] = "Warning", ["Logging:LogLevel:Microsoft.EntityFrameworkCore"] = "Warning"
            }));
        }
    }
}

public sealed class PostgresApiFactAttribute : FactAttribute
{
    public static bool Required => string.Equals(Environment.GetEnvironmentVariable("FIRSTBLICK_REQUIRE_POSTGRES"), "true", StringComparison.OrdinalIgnoreCase);
    public PostgresApiFactAttribute()
    {
        if (!Required && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FIRSTBLICK_TEST_POSTGRES")))
            Skip = "Set FIRSTBLICK_TEST_POSTGRES to run real PostgreSQL HTTP, concurrency and tenant-isolation tests.";
    }
}
