using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FirstBlick.Operations;

internal sealed record TableProof(string Name, long Rows, string Sha256);
internal sealed record ObjectProof(Guid DocumentId, Guid TenantId, string Key, string VersionId, long Size, string Sha256, string ContentType, string State, string File);
internal sealed record FileProof(string File, long Size, string Sha256);
internal sealed record CertificateProof(string File, string Thumbprint);
internal sealed record KeyProbe(string ProtectedValue, string PlaintextSha256);
internal sealed class BackupManifest
{
    public int Format { get; set; } = 1;
    public DateTimeOffset SnapshotAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public string Release { get; set; } = "";
    public string DatabaseVersion { get; set; } = "";
    public int DatabaseMajor { get; set; }
    public string PgDumpVersion { get; set; } = "";
    public string SourceBucket { get; set; } = "";
    public string SourceEndpoint { get; set; } = "";
    public string Region { get; set; } = "";
    public string DataProtectionApplication { get; set; } = "FirstBlick";
    public string DotnetRuntime { get; set; } = "";
    public Dictionary<string, string?> RuntimeConfiguration { get; set; } = [];
    public string[] ExternalSecretRequirements { get; set; } = ["Database credentials", "S3 credentials", "Data Protection PFX password", "SMTP credentials if enabled", "TLS certificate/private key if terminated in Kestrel"];
    public KeyProbe? KeyProbe { get; set; }
    public CertificateProof? Certificate { get; set; }
    public List<TableProof> Tables { get; set; } = [];
    public List<ObjectProof> Objects { get; set; } = [];
    public List<FileProof> Files { get; set; } = [];
}

internal static class BundleFiles
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static string NewDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        Require.That(!Path.Exists(full), "Output directory already exists; refusing to overwrite a backup or drill.");
        Directory.CreateDirectory(full);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(full, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return full;
    }
    public static string SafePath(string root, string relative)
    {
        Require.That(relative.Length > 0 && !Path.IsPathRooted(relative) && !relative.Contains('\\') && relative.Split('/').All(x => x.Length > 0 && x is not "." and not ".."), "Backup contains an unsafe relative path.");
        var current = Path.GetFullPath(root);
        Require.That((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0, "Backup directory may not be a symlink.");
        foreach (var part in relative.Split('/'))
        {
            current = Path.Combine(current, part);
            if (Path.Exists(current)) Require.That((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0, "Backup entries may not be symlinks.");
        }
        return current;
    }
    public static async Task<string> Hash(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
    }
    public static async Task<FileProof> Proof(string root, string file)
    {
        var path = SafePath(root, file);
        return new(file, new FileInfo(path).Length, await Hash(path));
    }
    public static async Task Complete(string directory, BackupManifest manifest)
    {
        manifest.CompletedAt = DateTimeOffset.UtcNow;
        await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest, Json));
        await File.WriteAllTextAsync(Path.Combine(directory, "COMPLETE"), await Hash(Path.Combine(directory, "manifest.json")) + "\n");
    }
    public static async Task<BackupManifest> Verify(string directory)
    {
        var manifestPath = SafePath(directory, "manifest.json"); var complete = SafePath(directory, "COMPLETE");
        Require.That(File.Exists(complete) && File.Exists(manifestPath), "Backup is incomplete: manifest.json and COMPLETE are required.");
        Require.That((await File.ReadAllTextAsync(complete)).Trim() == await Hash(manifestPath), "Backup manifest checksum does not match COMPLETE.");
        var manifest = JsonSerializer.Deserialize<BackupManifest>(await File.ReadAllTextAsync(manifestPath), Json) ?? throw new OperationsException("Backup manifest is empty.");
        Require.That(manifest.Format == 1 && manifest.DataProtectionApplication == "FirstBlick", "Unsupported backup format or Data Protection application.");
        Require.That(manifest.Files.Select(x => x.File).Distinct(StringComparer.Ordinal).Count() == manifest.Files.Count, "Backup lists duplicate file names.");
        Require.That(manifest.Files.Any(x => x.File == "database.dump") && manifest.Tables.Count > 0, "Backup is missing its database proof.");
        foreach (var file in manifest.Files)
        {
            var path = SafePath(directory, file.File);
            Require.That(File.Exists(path) && new FileInfo(path).Length == file.Size && await Hash(path) == file.Sha256, $"Backup file integrity check failed: {file.File}.");
        }
        foreach (var obj in manifest.Objects)
        {
            Require.That(obj.VersionId.Length > 0 && obj.VersionId != "null" && obj.State is "Ready" or "Archived", "Backup object lacks a sealed version.");
            Require.That(manifest.Files.Any(x => x.File == obj.File && x.Size == obj.Size && x.Sha256 == obj.Sha256), "Object metadata differs from its file proof.");
        }
        if (manifest.Certificate is { } cert) Require.That(manifest.Files.Any(x => x.File == cert.File), "Backup certificate is missing from integrity proof.");
        return manifest;
    }
}
