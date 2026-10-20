using System.IO.Compression;
using System.Security.Cryptography;
using FirstBlick.Core.Business;

namespace FirstBlick.Infrastructure.Integrations;

/// <summary>Only the original pinned archives are trusted, not a mutable extracted-file manifest.</summary>
public sealed class OfficialValidationBundle
{
    public const string XRechnungSha256 = "2530cd107c414511c5d0462ec10f886910395abfca820db82e83d70bf01221a8";
    public const string ValidatorSha256 = "799e64befca97d4080e03608c80b85dd5a5ecc5f4ae4f35d1116ec2855b9a7c9";
    public const string GaebSha256 = "d4f19a79c46119ba6a7bbca1067bc2a26ccadb60d1b5fd8a82725f45b491e203";
    private readonly Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
    public OfficialValidationBundle(string directory)
    {
        LoadArchive(directory, "xrechnung", XRechnungSha256);
        LoadArchive(directory, "gaeb", GaebSha256);
        files.Add("validator.jar", ReadPinned(Path.Combine(directory, "validator.jar"), ValidatorSha256, 16 * 1024 * 1024));
    }
    private static byte[] ReadPinned(string path, string expected, int maximum)
    {
        Guard.Require(File.Exists(path), "validation_assets_missing", "Install the pinned official validation resources before using this operation.");
        var info = new FileInfo(path);
        Guard.Require(info.Length is > 0 && info.Length <= maximum, "validation_asset_integrity", "The validation resource size is invalid.");
        var bytes = File.ReadAllBytes(path);
        Guard.Require(Convert.ToHexStringLower(SHA256.HashData(bytes)) == expected,
            "validation_asset_integrity", "The official validation asset checksum differs from its pinned release.");
        return bytes;
    }
    private void LoadArchive(string directory, string name, string digest)
    {
        using var stream = new MemoryStream(ReadPinned(Path.Combine(directory, name + ".zip"), digest, 16 * 1024 * 1024), false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        Guard.Require(zip.Entries.Count <= 2000 && zip.Entries.Sum(x => x.Length) <= 64 * 1024 * 1024,
            "validation_asset_integrity", "The validation archive exceeds the approved budget.");
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            Guard.Require(!Path.IsPathRooted(entry.FullName) && !entry.FullName.Contains('\\') &&
                !entry.FullName.Split('/').Any(x => x is ".." or "." or ""),
                "validation_asset_integrity", "The archive contains an unsafe resource name.");
            using var input = entry.Open(); using var content = new MemoryStream(); input.CopyTo(content);
            files.Add(name + "/" + entry.FullName, content.ToArray());
        }
    }
    public Stream Open(string resource)
    {
        Guard.Require(files.TryGetValue(resource, out var bytes), "validation_resource_unknown", "A schema attempted to resolve a resource outside the pinned bundle.");
        return new MemoryStream(bytes!, writable: false);
    }
    public void MaterializeKosit(string target)
    {
        Directory.CreateDirectory(target);
        foreach (var entry in files.Where(x => x.Key == "validator.jar" || x.Key.StartsWith("xrechnung/", StringComparison.Ordinal)))
        {
            var path = Path.Combine(target, entry.Key.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, entry.Value);
        }
    }
}
