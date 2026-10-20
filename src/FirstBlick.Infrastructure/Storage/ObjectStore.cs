using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;

namespace FirstBlick.Infrastructure.Storage;

public sealed record ObjectMetadata(long Size, string? VersionId);
public interface IObjectStore
{
    bool Configured { get; }
    Task<string?> Put(string key, Stream body, string contentType, CancellationToken ct);
    Task<ObjectMetadata?> Head(string key, string? version, CancellationToken ct);
    Task<(Stream Body, string ContentType)> Get(string key, string? version, CancellationToken ct);
}

public sealed class UnconfiguredObjectStore : IObjectStore
{
    public bool Configured => false;
    public Task<string?> Put(string key, Stream body, string contentType, CancellationToken ct) => throw new Core.Business.DomainException("storage_unconfigured", "Object storage is not configured.");
    public Task<ObjectMetadata?> Head(string key, string? version, CancellationToken ct) => throw new Core.Business.DomainException("storage_unconfigured", "Object storage is not configured.");
    public Task<(Stream Body, string ContentType)> Get(string key, string? version, CancellationToken ct) => throw new Core.Business.DomainException("storage_unconfigured", "Object storage is not configured.");
}

public sealed class S3ObjectStore(IAmazonS3 client, IConfiguration config) : IObjectStore
{
    private readonly string bucket = config["Storage:Bucket"] ?? throw new InvalidOperationException("Storage:Bucket is required.");
    public bool Configured => true;
    public async Task<string?> Put(string key, Stream body, string contentType, CancellationToken ct)
    {
        var r = await client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = body,
            ContentType = contentType, AutoCloseStream = false }, ct);
        return r.VersionId;
    }
    public async Task<ObjectMetadata?> Head(string key, string? version, CancellationToken ct)
    {
        try
        {
            var r = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = key, VersionId = version }, ct);
            return new(r.ContentLength, r.VersionId);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }
    public async Task<(Stream Body, string ContentType)> Get(string key, string? version, CancellationToken ct)
    {
        var r = await client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, VersionId = version }, ct);
        return (r.ResponseStream, r.Headers.ContentType);
    }
}
