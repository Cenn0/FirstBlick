using System.Data;

namespace FirstBlick.Operations;

internal static class Backup
{
    public static async Task<BackupManifest> Create(Settings settings, string output)
    {
        using var certificate = settings.Certificate();
        Require.That(settings.Local || settings.Release != "unversioned-local", "Production backup requires FIRSTBLICK_RELEASE identifying its exact deployable application image/commit.");
        var probe = await KeyProtection.CreateProbe(settings, certificate);
        var dumpVersion = await PostgresFiles.ToolVersion("pg_dump");
        var directory = BundleFiles.NewDirectory(output);
        var manifest = new BackupManifest { Release = settings.Release, SourceBucket = settings.Bucket, SourceEndpoint = settings.Endpoint, Region = settings.Region, PgDumpVersion = dumpVersion, KeyProbe = probe,
            DotnetRuntime = Environment.Version.ToString(), RuntimeConfiguration = CaptureConfiguration() };
        if (certificate is not null)
        {
            Directory.CreateDirectory(Path.Combine(directory, "certificates"));
            await File.WriteAllBytesAsync(Path.Combine(directory, "certificates/data-protection.pfx"), certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, settings.CertificatePassword));
            manifest.Certificate = new("certificates/data-protection.pfx", certificate.Thumbprint);
            manifest.Files.Add(await BundleFiles.Proof(directory, manifest.Certificate.File));
        }
        await using (var connection = await PostgresFiles.Open(settings))
        await using (var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead))
        {
            await PostgresFiles.Scalar(connection, "SET TRANSACTION READ ONLY", transaction);
            manifest.DatabaseVersion = await PostgresFiles.Scalar(connection, "SHOW server_version", transaction);
            manifest.DatabaseMajor = PostgresFiles.Major(manifest.DatabaseVersion);
            Require.That(PostgresFiles.Major(dumpVersion) >= manifest.DatabaseMajor, "pg_dump must be at least as new as the source PostgreSQL major version.");
            var snapshot = await PostgresFiles.Scalar(connection, "SELECT pg_export_snapshot()", transaction);
            manifest.SnapshotAt = DateTimeOffset.UtcNow;
            manifest.Objects = await PostgresFiles.ObjectReferences(connection, transaction);
            manifest.Tables = await PostgresFiles.TableProofs(connection, transaction);
            await PostgresFiles.Dump(settings, snapshot, Path.Combine(directory, "database.dump"));
            await transaction.CommitAsync();
        }
        manifest.Files.Add(await BundleFiles.Proof(directory, "database.dump"));
        using var client = settings.S3();
        foreach (var obj in manifest.Objects)
        {
            await StorageFiles.Download(client, settings.Bucket, obj, BundleFiles.SafePath(directory, obj.File));
            manifest.Files.Add(await BundleFiles.Proof(directory, obj.File));
        }
        await BundleFiles.Complete(directory, manifest);
        await BundleFiles.Verify(directory);
        return manifest;
    }

    private static Dictionary<string, string?> CaptureConfiguration()
    {
        // Deliberate allowlist: never serialize the environment, connection string, passwords or provider credentials.
        string[] names = ["DOTNET_ENVIRONMENT", "ASPNETCORE_ENVIRONMENT", "TZ", "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT", "App__BaseUrl",
            "Smtp__Host", "Smtp__From", "Smtp__Port", "Smtp__EnableSsl", "Outbox__LeaseSeconds", "Outbox__SendTimeoutSeconds", "Outbox__MaximumAttempts",
            "Authentication__RequestsPerMinute", "FIRSTBLICK_FONT_DIRECTORY", "Kestrel__Endpoints__Https__Url", "Kestrel__Endpoints__Https__Certificate__Path"];
        return names.ToDictionary(name => name, name =>
        {
            var values = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .Where(x => string.Equals((string)x.Key, name, StringComparison.OrdinalIgnoreCase)).Select(x => (string?)x.Value).Distinct(StringComparer.Ordinal).ToArray();
            Require.That(values.Length <= 1, $"Conflicting environment values for allowlisted configuration {name}.");
            return values.SingleOrDefault();
        }, StringComparer.Ordinal);
    }
}
