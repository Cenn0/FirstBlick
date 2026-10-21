using System.Security.Cryptography.X509Certificates;
using Amazon.Runtime;
using Amazon.S3;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FirstBlick.Operations;

internal sealed class OperationsException(string message) : Exception(message);
internal static class Require
{
    public static void That(bool condition, string message) { if (!condition) throw new OperationsException(message); }
}

internal sealed record Settings(string ConnectionString, string Endpoint, string Bucket, string AccessKey, string SecretKey,
    string Region, string? CertificatePath, string? CertificatePassword, string Release, bool Local,
    string? OperatorEmail = null, string? OperatorPassword = null, string OperatorDisplayName = "Recovery operator")
{
    public static Settings FromEnvironment()
    {
        static string Required(string key) => Environment.GetEnvironmentVariable(key) is { Length: > 0 } s ? s : throw new OperationsException($"Missing configuration: {key}.");
        var endpoint = Required("Storage__Endpoint");
        Require.That(Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0 && uri.Query.Length == 0,
            "Storage endpoint must be an absolute HTTP(S) URL without credentials or query parameters.");
        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
        return new(Required("ConnectionStrings__Database"), endpoint, Required("Storage__Bucket"), Required("Storage__AccessKey"), Required("Storage__SecretKey"),
            Environment.GetEnvironmentVariable("Storage__Region") ?? "us-east-1", Environment.GetEnvironmentVariable("DataProtection__CertificatePath"),
            Environment.GetEnvironmentVariable("DataProtection__CertificatePassword"), Environment.GetEnvironmentVariable("FIRSTBLICK_RELEASE") ?? "unversioned-local", environment is "Development" or "Testing",
            Environment.GetEnvironmentVariable("Operations__OperatorEmail"), Environment.GetEnvironmentVariable("Operations__OperatorPassword"), Environment.GetEnvironmentVariable("Operations__OperatorDisplayName") ?? "Recovery operator");
    }

    public AmazonS3Client S3() => new(new BasicAWSCredentials(AccessKey, SecretKey), new AmazonS3Config
    { ServiceURL = Endpoint, AuthenticationRegion = Region, ForcePathStyle = true, MaxErrorRetry = 2 });
    public FirstBlickDbContext Db(string? database = null) => new(new DbContextOptionsBuilder<FirstBlickDbContext>().UseNpgsql(Connection(database)).Options, new TenantScope());
    public string Connection(string? database = null)
    {
        var b = new NpgsqlConnectionStringBuilder(ConnectionString) { Pooling = false, IncludeErrorDetail = false, ApplicationName = "FirstBlick.Operations" };
        if (database is not null) b.Database = database;
        return b.ConnectionString;
    }
    public X509Certificate2? Certificate(string? overridePath = null)
    {
        var path = overridePath ?? CertificatePath;
        Require.That(Local || !string.IsNullOrEmpty(path), "Encrypted Data Protection certificate is required outside Development/Testing.");
        if (string.IsNullOrEmpty(path)) return null;
        Require.That(!string.IsNullOrEmpty(CertificatePassword), "DataProtection__CertificatePassword is required so recovery certificates are encrypted in the backup.");
        var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, CertificatePassword, X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
        using var rsa = certificate.GetRSAPrivateKey();
        Require.That(rsa is not null, "Data Protection certificate must contain its RSA private key.");
        return certificate;
    }
}

internal sealed record Arguments(Dictionary<string, string> Values)
{
    public static Arguments Parse(string[] args)
    {
        Require.That(args.Length % 2 == 0, "Options use --name VALUE pairs.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            Require.That(args[i].StartsWith("--", StringComparison.Ordinal) && !args[i + 1].StartsWith("--", StringComparison.Ordinal), "Options use --name VALUE pairs.");
            Require.That(values.TryAdd(args[i][2..], args[i + 1]), "Duplicate option.");
        }
        Require.That(values.Keys.All(x => x is "input" or "output" or "database" or "bucket" or "report"), "Unknown option.");
        return new(values);
    }
    public string Required(string key) => Values.TryGetValue(key, out var value) && value.Length > 0 ? value : throw new OperationsException($"Missing --{key}.");
}
