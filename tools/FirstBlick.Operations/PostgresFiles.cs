using System.Buffers.Binary;
using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

namespace FirstBlick.Operations;

internal static partial class PostgresFiles
{
    [GeneratedRegex("^[a-z][a-z0-9_]{1,62}$", RegexOptions.CultureInvariant)] private static partial Regex DatabaseName();
    public static async Task<NpgsqlConnection> Open(Settings settings, string? database = null)
    {
        var connection = new NpgsqlConnection(settings.Connection(database));
        try
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SET TIME ZONE 'UTC'; SET DateStyle = 'ISO, YMD'; SET extra_float_digits = 3", connection);
            await command.ExecuteNonQueryAsync();
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }
    public static async Task<string> Scalar(NpgsqlConnection connection, string sql, NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 0 };
        return (await command.ExecuteScalarAsync())?.ToString() ?? "";
    }
    public static string Identifier(string identifier) => '"' + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    public static async Task<List<TableProof>> TableProofs(NpgsqlConnection connection, NpgsqlTransaction? transaction = null)
    {
        var tables = new List<string>();
        await using (var command = new NpgsqlCommand("SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename COLLATE \"C\"", connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync()) while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        var result = new List<TableProof>();
        foreach (var table in tables)
        {
            // Stable order and length framing preserve duplicate rows and distinguish embedded newlines.
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); long count = 0;
            await using var command = new NpgsqlCommand($"SELECT to_jsonb(t)::text FROM public.{Identifier(table)} t ORDER BY to_jsonb(t)::text COLLATE \"C\"", connection, transaction) { CommandTimeout = 0 };
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
            while (await reader.ReadAsync())
            {
                var bytes = Encoding.UTF8.GetBytes(reader.GetString(0)); var length = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(length, bytes.LongLength);
                hash.AppendData(length); hash.AppendData(bytes); count++;
            }
            result.Add(new(table, count, Convert.ToHexStringLower(hash.GetHashAndReset())));
        }
        return result;
    }
    public static async Task<List<ObjectProof>> ObjectReferences(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        var result = new List<ObjectProof>();
        await using var command = new NpgsqlCommand("SELECT \"Id\", \"TenantId\", \"ObjectKey\", \"VersionId\", \"Size\", \"Sha256\", \"ContentType\", \"State\" FROM \"Documents\" WHERE \"State\" IN ('Ready','Archived') ORDER BY \"Id\"", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            Require.That(!reader.IsDBNull(3) && reader.GetString(3).Length > 0 && reader.GetString(3) != "null", "A sealed document lacks an immutable S3 version. Backup cannot continue.");
            var id = reader.GetGuid(0);
            result.Add(new(id, reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetString(5).ToLowerInvariant(), reader.GetString(6), reader.GetString(7), $"objects/{id:N}.bin"));
        }
        return result;
    }
    public static async Task<string> ToolVersion(string tool) => (await RunTool(tool, ["--version"], null)).Trim();
    public static int Major(string version)
    {
        var match = Regex.Match(version, "(?:PostgreSQL\\)?\\s+)?(?<major>[0-9]+)(?:\\.[0-9]+)", RegexOptions.CultureInvariant);
        return match.Success ? int.Parse(match.Groups["major"].Value, System.Globalization.CultureInfo.InvariantCulture) : throw new OperationsException("Cannot determine PostgreSQL tool/server major version.");
    }
    public static Task<string> Dump(Settings settings, string snapshot, string path) => RunTool("pg_dump",
        ["--format=custom", "--no-owner", "--no-privileges", "--snapshot", snapshot, "--file", path], settings);
    public static Task<string> Restore(Settings settings, string database, string path) => RunTool("pg_restore",
        ["--single-transaction", "--exit-on-error", "--no-owner", "--no-privileges", "--dbname", database, path], settings with { ConnectionString = settings.Connection(database) });
    private static async Task<string> RunTool(string tool, string[] arguments, Settings? settings)
    {
        var executable = Environment.GetEnvironmentVariable(tool == "pg_dump" ? "FIRSTBLICK_PG_DUMP" : "FIRSTBLICK_PG_RESTORE") ?? tool;
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (settings is not null)
        {
            var b = new NpgsqlConnectionStringBuilder(settings.ConnectionString);
            info.Environment["PGHOST"] = b.Host; info.Environment["PGPORT"] = b.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            info.Environment["PGDATABASE"] = b.Database; info.Environment["PGUSER"] = b.Username; info.Environment["PGPASSWORD"] = b.Password;
            info.Environment["PGSSLMODE"] = b.SslMode switch { SslMode.VerifyCA => "verify-ca", SslMode.VerifyFull => "verify-full", _ => b.SslMode.ToString().ToLowerInvariant() };
            if (!string.IsNullOrEmpty(b.RootCertificate)) info.Environment["PGSSLROOTCERT"] = b.RootCertificate;
        }
        info.Environment["LC_ALL"] = "C";
        using var process = Process.Start(info) ?? throw new OperationsException($"Could not start {tool}.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); var output = await stdout; _ = await stderr;
        Require.That(process.ExitCode == 0, $"{tool} exited unsuccessfully (code {process.ExitCode}). No backup/restore completion is claimed; verify client version, PostgreSQL access and available disk space.");
        return output;
    }
    public static async Task CreateDatabase(Settings settings, string database, bool drillSource = false)
    {
        Require.That(DatabaseName().IsMatch(database) && (database.StartsWith("firstblick_restore_", StringComparison.Ordinal) || drillSource && database.StartsWith("firstblick_drill_", StringComparison.Ordinal)), "Restore requires a new database named firstblick_restore_NAME.");
        await using var connection = await Open(settings, "postgres");
        await using var check = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_database WHERE datname = @name)", connection);
        check.Parameters.AddWithValue("name", database);
        Require.That(!(bool)(await check.ExecuteScalarAsync())!, "Target database already exists; no existing database will be overwritten.");
        await using var command = new NpgsqlCommand($"CREATE DATABASE {Identifier(database)} TEMPLATE template0 ENCODING 'UTF8'", connection);
        await command.ExecuteNonQueryAsync();
    }
    public static async Task DropDrillDatabase(Settings settings, string database, string nonce)
    {
        Require.That(database is var name && (name == $"firstblick_drill_{nonce}" || name == $"firstblick_restore_{nonce}"), "Refusing cleanup of a database not created by this drill.");
        await using var connection = await Open(settings, "postgres");
        await using var command = new NpgsqlCommand($"DROP DATABASE {Identifier(database)} WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }
}
