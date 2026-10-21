using System.Text.Json;
using FirstBlick.Operations;

try
{
    if (args.Length == 0 || args[0] is "--help" or "help")
    {
        Console.WriteLine("""
            FirstBlick operations
              init-storage
              backup --output DIRECTORY
              verify --input DIRECTORY
              restore --input DIRECTORY --database firstblick_restore_NAME --bucket firstblick-restore-NAME --report FILE
              resume --input RECONCILIATION_JSON --report FILE
              drill --output DIRECTORY
              migration-check --input DIRECTORY --report FILE
              migration-import --input DIRECTORY --database firstblick_restore_NAME --bucket firstblick-restore-NAME --report FILE
              migration-compare --input DIRECTORY --database firstblick_restore_NAME --bucket firstblick-restore-NAME --report FILE
              migration-drill --output DIRECTORY
            Configuration: ConnectionStrings__Database, Storage__Endpoint/Bucket/AccessKey/SecretKey/Region,
            DataProtection__CertificatePath/CertificatePassword, FIRSTBLICK_RELEASE. Restore and migration-import
            also require Operations__OperatorEmail/OperatorPassword for a new identity.
            migration-check reads local files without database, storage or credential configuration.
            Exit codes: 0 completed, 2 blocked compatibility/mismatched comparison, 1 operation failed.
            See docs/operations.md and docs/migration.md.
            """);
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
        "migration-check" => await MigrationCheck.Run(options.Required("input"), options.Required("report")),
        "migration-import" => await MigrationImport.Run(Settings.FromEnvironment(), options.Required("input"), options.Required("database"), options.Required("bucket"), options.Required("report")),
        "migration-compare" => await MigrationCompare.Run(Settings.FromEnvironment(), options.Required("input"), options.Required("database"), options.Required("bucket"), options.Required("report")),
        "migration-drill" => await MigrationDrill.Run(Settings.FromEnvironment(), options.Required("output")),
        "drill" => await RestoreDrill.Run(Settings.FromEnvironment(), options.Required("output")),
        _ => throw new InvalidOperationException("Unknown command. Use --help.")
    };
    Console.WriteLine(JsonSerializer.Serialize(result, BundleFiles.Json));
    return result is MigrationCheckReport { State: "Blocked" } or MigrationCompareReport { State: "Mismatch" } ? 2 : 0;
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
