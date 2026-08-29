using Dapper;
using Microsoft.Data.SqlClient;
using NexaDB.Data.MsSql;
using NexaOne.Application.Query;
using NexaOne.Infrastructure.Persistence;
using System.Security.Cryptography;
using System.Text;
using Xunit.Abstractions;

namespace NexaOne.ServerTests;

/// <summary>
/// Shared live-SQL-Server harness for runtime contract tests. Ordinary local runs remain
/// self-contained when no connection is configured; the dedicated CI job sets the required flag
/// so a missing connection is a hard failure rather than a soft pass.
/// </summary>
internal sealed class MssqlContractDatabase
{
    internal const string ConnectionEnvironmentVariable = "NEXAONE_MSSQL_TEST_CONN";
    internal const string RequiredEnvironmentVariable = "NEXAONE_MSSQL_CONTRACT_REQUIRED";

    private static readonly SemaphoreSlim ValidationGate = new(1, 1);
    private static string? _validatedConnectionString;

    private MssqlContractDatabase(string connectionString)
    {
        ConnectionString = connectionString;
        DataSource = new EesDataSource
        {
            Provider = new MsSqlProvider(),
            ConnectionString = connectionString,
            QueryGatewayOptions = new DapperQueryGatewayOptions
            {
                CommandTimeoutSeconds = 60,
                Module = "MssqlContract",
            },
        };
    }

    public string ConnectionString { get; }
    public EesDataSource DataSource { get; }

    public static async Task<MssqlContractDatabase?> TryCreateAsync(ITestOutputHelper output)
    {
        var connectionString = GetConnectionStringOrThrowIfRequired();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            output.WriteLine(
                $"Soft skip: {ConnectionEnvironmentVariable} is not configured. " +
                $"The dedicated CI job sets {RequiredEnvironmentVariable}=true and cannot soft-skip.");
            return null;
        }

        await ValidateFullMigrationSetAsync(connectionString);
        return new MssqlContractDatabase(connectionString);
    }

    public async Task ExecuteAsync(string sql, object? parameters = null)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            parameters,
            commandTimeout: 60));
    }

    public async Task<T> ScalarAsync<T>(string sql, object? parameters = null)
        where T : notnull
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        var value = await connection.ExecuteScalarAsync<T>(new CommandDefinition(
            sql,
            parameters,
            commandTimeout: 60));
        return value ?? throw new InvalidOperationException("The MSSQL contract scalar query returned no value.");
    }

    public async Task<IReadOnlyList<IDictionary<string, object>>> QueryNamedAsync(
        string queryId,
        object? parameters = null)
    {
        var registry = FileQueryRegistry.Load(
            "mssql",
            RepositorySource.GetDirectory("src/00.Main/NexaOne.Server/config/db/queries"));
        if (!registry.TryGet(queryId, out var definition) || definition is null)
            throw new InvalidOperationException($"MSSQL named query '{queryId}' is not registered.");

        var catalog = new InMemoryQueryCatalog();
        catalog.Register(queryId, definition.Sql);
        var rows = await new DapperQueryGateway(DataSource, catalog)
            .QueryNamedAsync<dynamic>(queryId, parameters);
        return rows.Cast<IDictionary<string, object>>().ToList();
    }

    internal static string? GetConnectionStringOrThrowIfRequired()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString)
            && string.Equals(
                Environment.GetEnvironmentVariable(RequiredEnvironmentVariable),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{ConnectionEnvironmentVariable} is required for the MSSQL contract gate.");
        }

        return connectionString;
    }

    internal static async Task ValidateFullMigrationSetAsync(string connectionString)
    {
        await ValidationGate.WaitAsync();
        try
        {
            if (string.Equals(
                    _validatedConnectionString,
                    connectionString,
                    StringComparison.Ordinal))
                return;

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            var lockResult = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                """
                DECLARE @result INT;
                EXEC @result = sys.sp_getapplock
                    @Resource = N'NexaOne.SchemaMigrations',
                    @LockMode = N'Shared',
                    @LockOwner = N'Session',
                    @LockTimeout = 60000;
                SELECT @result;
                """,
                commandTimeout: 70));
            if (lockResult < 0)
                throw new InvalidOperationException("Could not acquire the SQL Server migration lock.");

            try
            {
                var migrationsPath = RepositorySource.GetDirectory(
                    "src/00.Main/NexaOne.Server/config/db/migrations");
                var migrationFiles = SqliteSchemaInitializer.GetOrderedMigrationFiles(migrationsPath);
                var expectedMigrations = migrationFiles
                    .Select(path => new MigrationLedgerRow(
                        Path.GetFileName(path),
                        ComputeMigrationHash(path)))
                    .ToArray();
                var expectedVersions = expectedMigrations.Select(row => row.VersionId).ToArray();
                var ledgerExists = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT CASE WHEN OBJECT_ID(N'SYS_SCHEMA_MIGRATION', N'U') IS NULL THEN 0 ELSE 1 END;",
                    commandTimeout: 60));
                if (ledgerExists != 1)
                {
                    throw new InvalidOperationException(
                        "The MSSQL migration ledger is missing. Run tools/ops/Apply-MssqlMigrations.ps1 before the contract tests.");
                }

                var checksumColumnExists = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT CASE WHEN COL_LENGTH(N'SYS_SCHEMA_MIGRATION', N'CONTENT_SHA256') IS NULL THEN 0 ELSE 1 END;",
                    commandTimeout: 60));
                if (checksumColumnExists != 1)
                {
                    throw new InvalidOperationException(
                        "The MSSQL migration ledger has no CONTENT_SHA256 column. Run the current migration runner first.");
                }

                var appliedMigrations = (await connection.QueryAsync<MigrationLedgerRow>(new CommandDefinition(
                        "SELECT VERSION_ID AS VersionId, CONTENT_SHA256 AS ContentSha256 FROM SYS_SCHEMA_MIGRATION;",
                        commandTimeout: 60)))
                    .ToArray();
                var appliedVersions = appliedMigrations.Select(row => row.VersionId).ToArray();
                var duplicateVersions = appliedVersions
                    .GroupBy(version => version, StringComparer.Ordinal)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key)
                    .ToArray();
                var missingVersions = expectedVersions
                    .Except(appliedVersions, StringComparer.Ordinal)
                    .ToArray();
                var unexpectedVersions = appliedVersions
                    .Except(expectedVersions, StringComparer.Ordinal)
                    .ToArray();
                if (duplicateVersions.Length > 0
                    || missingVersions.Length > 0
                    || unexpectedVersions.Length > 0)
                {
                    throw new InvalidOperationException(
                        "MSSQL migration history does not match the checked-out migration set. "
                        + $"Missing=[{string.Join(", ", missingVersions)}]; "
                        + $"Unexpected=[{string.Join(", ", unexpectedVersions)}]; "
                        + $"Duplicate=[{string.Join(", ", duplicateVersions)}].");
                }


                var appliedByVersion = appliedMigrations.ToDictionary(
                    row => row.VersionId,
                    StringComparer.Ordinal);
                var missingChecksums = expectedMigrations
                    .Where(expected => string.IsNullOrWhiteSpace(appliedByVersion[expected.VersionId].ContentSha256))
                    .Select(expected => expected.VersionId)
                    .ToArray();
                var contentDrift = expectedMigrations
                    .Where(expected =>
                        !string.IsNullOrWhiteSpace(appliedByVersion[expected.VersionId].ContentSha256)
                        && !string.Equals(
                            expected.ContentSha256,
                            appliedByVersion[expected.VersionId].ContentSha256,
                            StringComparison.OrdinalIgnoreCase))
                    .Select(expected => expected.VersionId)
                    .ToArray();
                if (missingChecksums.Length > 0 || contentDrift.Length > 0)
                {
                    throw new InvalidOperationException(
                        "MSSQL migration checksums do not match the checked-out migration set. "
                        + $"MissingChecksum=[{string.Join(", ", missingChecksums)}]; "
                        + $"ContentDrift=[{string.Join(", ", contentDrift)}].");
                }
            }
            finally
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    EXEC sys.sp_releaseapplock
                        @Resource = N'NexaOne.SchemaMigrations',
                        @LockOwner = N'Session';
                    """,
                    commandTimeout: 60));
            }

            _validatedConnectionString = connectionString;
        }
        finally
        {
            ValidationGate.Release();
        }
    }


    private static string ComputeMigrationHash(string path)
    {
        var text = File.ReadAllText(path, new UTF8Encoding(false, true));
        var canonical = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private sealed record MigrationLedgerRow(string VersionId, string? ContentSha256);
}
