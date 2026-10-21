using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using FirstBlick.Infrastructure.Persistence;
using FirstBlick.Infrastructure.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using static FirstBlick.Api.Tests.PostgresApiFixture;

namespace FirstBlick.Api.Tests;

[CollectionDefinition("Official XML engine", DisableParallelization = true)]
public sealed class OfficialXmlEngineCollection;

/// <summary>Actual HTTP, Identity, PostgreSQL and original validator; only S3 is a deterministic versioned test double.</summary>
internal sealed class ExchangeApiHarness : IAsyncDisposable
{
    public VersionedExchangeStore Store { get; } = new();
    public WebApplicationFactory<Program> Factory { get; }
    public ExchangeApiHarness(PostgresApiFixture fixture)
    {
        Factory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        { services.RemoveAll<IObjectStore>(); services.AddSingleton<IObjectStore>(Store); }));
    }
    public async Task<HttpClient> Login(ApiTestUser user)
    {
        var client = Factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Tenant-Id", user.TenantId.ToString());
        await RefreshCsrf(client);
        await Ok<JsonElement>(Send(client, HttpMethod.Post, "/api/v1/auth/login", new { user.Email, user.Password }));
        await RefreshCsrf(client); return client;
    }
    public static async Task<T> Ok<T>(Task<HttpResponseMessage> pending)
    {
        using var response = await pending; var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {text}");
        return AppJson.Read<T>(text);
    }
    public static async Task<string> Error(Task<HttpResponseMessage> pending)
    {
        using var response = await pending; var text = await response.Content.ReadAsStringAsync();
        Assert.False(response.IsSuccessStatusCode, text);
        using var body = JsonDocument.Parse(text); return body.RootElement.GetProperty("code").GetString()!;
    }
    public static async Task<byte[]> Download(HttpClient client, JsonElement document)
    {
        using var response = await client.GetAsync($"/api/v1/documents/{document.GetProperty("id").GetGuid()}/download");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(document.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
        Assert.Equal(document.GetProperty("size").GetInt64(), bytes.LongLength);
        return bytes;
    }
    public ValueTask DisposeAsync() => Factory.DisposeAsync();
}

internal sealed class VersionedExchangeStore : IObjectStore
{
    private readonly ConcurrentDictionary<(string Key, string Version), (byte[] Bytes, string Type)> data = new();
    private int puts;
    public bool Configured => true;
    public bool CorruptReads { get; set; }
    public bool Unversioned { get; set; }
    public int PutCount => puts;
    public async Task<string?> Put(string key, Stream body, string contentType, CancellationToken ct)
    {
        Interlocked.Increment(ref puts); using var buffer = new MemoryStream(); await body.CopyToAsync(buffer, ct);
        var version = Guid.NewGuid().ToString("N"); data[(key, version)] = (buffer.ToArray(), contentType);
        return Unversioned ? null : version;
    }
    public Task<ObjectMetadata?> Head(string key, string? version, CancellationToken ct)
        => Task.FromResult<ObjectMetadata?>(version is not null && data.TryGetValue((key, version), out var value)
            ? new ObjectMetadata(value.Bytes.Length, version) : null);
    public Task<(Stream Body, string ContentType)> Get(string key, string? version, CancellationToken ct)
    {
        var value = data[(key, version!)]; var bytes = (byte[])value.Bytes.Clone();
        if (CorruptReads) bytes[0] ^= 1;
        return Task.FromResult<(Stream, string)>((new MemoryStream(bytes, writable: false), value.Type));
    }
}
