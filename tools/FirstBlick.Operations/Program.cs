using System.Text.Json;
using FirstBlick.Operations;

try
{
    if (args.Length == 0 || args[0] is "--help" or "help")
    {
        Console.WriteLine("FirstBlick operations\n  init-storage\n  backup --output DIRECTORY\n  verify --input DIRECTORY\n  restore --input DIRECTORY --database firstblick_restore_NAME --bucket firstblick-restore-NAME --report FILE\n  resume --input RECONCILIATION_JSON --report FILE\n  drill --output DIRECTORY\nConfiguration: ConnectionStrings__Database, Storage__Endpoint/Bucket/AccessKey/SecretKey/Region,\nDataProtection__CertificatePath/CertificatePassword, FIRSTBLICK_RELEASE. Restore also requires\nOperations__OperatorEmail/OperatorPassword for a new identity. See docs/operations.md.");
        return 0;
    }
    var options = Arguments.Parse(args.Skip(1).ToArray());
    object result = args[0] switch
    {
        "init-storage" => await StorageFiles.Initialize(Settings.FromEnvironment()),
        "backup" => Summary(await Backup.Create(Settings.FromEnvironment(), options.Required("output")), "backup_complete"),
        "verify" => Summary(await BundleFiles.Verify(options.Required("input")), "bundle_verified"),
        "restore" => await Restore.Run(Settings.FromEnvironment(), options.Required("input"), options.Required("database"), options.Required("bucket"), options.Required("report")),
        "resume" => await RecoveryResume.Run(Settings.FromEnvironment(), options.Required("input"), options.Required("report")),
        "drill" => await RestoreDrill.Run(Settings.FromEnvironment(), options.Required("output")),
        _ => throw new InvalidOperationException("Unknown command. Use --help.")
    };
    Console.WriteLine(JsonSerializer.Serialize(result, BundleFiles.Json));
    return 0;
}
catch (Exception ex)
{
    // Provider exceptions can include endpoints, SQL or configuration. Detailed exceptions stay off ordinary operator logs.
    Console.Error.WriteLine(ex is OperationsException ? ex.Message : $"Operation failed ({ex.GetType().Name}). No completion is claimed. Check the isolated target and configuration; see docs/operations.md.");
    if (Environment.GetEnvironmentVariable("FIRSTBLICK_OPS_TRACE") == "true") Console.Error.WriteLine(ex.StackTrace);
    return 1;
}

static object Summary(BackupManifest manifest, string status) => new { status, manifest.Release, manifest.SnapshotAt, manifest.CompletedAt,
    manifest.DatabaseVersion, tables = manifest.Tables.Count, rows = manifest.Tables.Sum(x => x.Rows), objectVersions = manifest.Objects.Count,
    files = manifest.Files.Count, encryptedCertificateIncluded = manifest.Certificate is not null, keyProbePresent = manifest.KeyProbe is not null };
