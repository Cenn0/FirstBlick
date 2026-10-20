using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;

namespace FirstBlick.Operations;

internal static class StorageFiles
{
    public static async Task<object> Initialize(Settings settings)
    {
        using var client = settings.S3();
        await EnableVersioning(client, settings.Bucket, false);
        return new { bucket = settings.Bucket, versioning = "Enabled" };
    }
    public static async Task EnableVersioning(IAmazonS3 client, string bucket, bool mustBeNew)
    {
        var exists = false;
        try { await client.GetBucketLocationAsync(new GetBucketLocationRequest { BucketName = bucket }); exists = true; }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { }
        Require.That(!mustBeNew || !exists, "Target bucket already exists; no existing bucket will be overwritten.");
        if (!exists) await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        await client.PutBucketVersioningAsync(new PutBucketVersioningRequest { BucketName = bucket, VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled } });
        var actual = await client.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = bucket });
        Require.That(actual.VersioningConfig.Status == VersionStatus.Enabled, "S3 bucket versioning could not be enabled.");
    }
    public static async Task Download(IAmazonS3 client, string bucket, ObjectProof obj, string destination)
    {
        using var response = await client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = obj.Key, VersionId = obj.VersionId });
        Require.That(response.VersionId == obj.VersionId && response.ContentLength == obj.Size, "S3 returned a different version or length than the sealed document reference.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) await response.ResponseStream.CopyToAsync(output);
        Require.That(new FileInfo(destination).Length == obj.Size && await BundleFiles.Hash(destination) == obj.Sha256, "S3 object content does not match its stored SHA-256 and length.");
    }
    public static async Task<string> Upload(IAmazonS3 client, string bucket, ObjectProof obj, string source)
    {
        await using var input = File.OpenRead(source);
        var uploaded = await client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = obj.Key, InputStream = input, ContentType = obj.ContentType, AutoCloseStream = false });
        Require.That(!string.IsNullOrEmpty(uploaded.VersionId) && uploaded.VersionId != "null", "Restored S3 object lacks a real version identifier.");
        using var downloaded = await client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = obj.Key, VersionId = uploaded.VersionId });
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(downloaded.ResponseStream));
        Require.That(downloaded.VersionId == uploaded.VersionId && downloaded.ContentLength == obj.Size && hash == obj.Sha256, "Restored S3 version failed content verification.");
        return uploaded.VersionId;
    }
    public static async Task DeleteDrillBucket(IAmazonS3 client, string bucket, string nonce)
    {
        Require.That(bucket == $"firstblick-drill-{nonce}" || bucket == $"firstblick-restore-{nonce}", "Refusing cleanup of a bucket not created by this drill.");
        string? keyMarker = null, versionMarker = null;
        do
        {
            var list = await client.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, KeyMarker = keyMarker, VersionIdMarker = versionMarker });
            foreach (var version in list.Versions ?? []) await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = version.Key, VersionId = version.VersionId });
            keyMarker = list.IsTruncated == true ? list.NextKeyMarker : null; versionMarker = list.NextVersionIdMarker;
        } while (keyMarker is not null);
        await client.DeleteBucketAsync(new DeleteBucketRequest { BucketName = bucket });
    }
}
