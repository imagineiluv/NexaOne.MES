using System.Diagnostics;

namespace NexaOne.UnitTests.Persistence;

public sealed class MssqlMigrationRunnerTests
{
    private static string RunnerPath
        => RepositorySource.GetFile("tools", "ops", "Apply-MssqlMigrations.ps1");

    private static string PerformanceBaselinePath
        => RepositorySource.GetFile("tools", "ops", "Get-MssqlPerformanceBaseline.ps1");

    [Fact]
    public void ValidateOnly_orders_migrations_by_numeric_version_without_opening_a_database()
    {
        using var fixture = new MigrationFixture();
        fixture.Write("V010__EMS_TEN.sql");
        fixture.Write("V002__MDM_TWO.sql");
        fixture.Write("V001__SYS_ONE.sql");

        var result = Run(fixture.Path, validateOnly: true);

        result.ExitCode.Should().Be(0, result.Output);
        var first = result.Output.IndexOf("V001__SYS_ONE.sql", StringComparison.Ordinal);
        var second = result.Output.IndexOf("V002__MDM_TWO.sql", StringComparison.Ordinal);
        var tenth = result.Output.IndexOf("V010__EMS_TEN.sql", StringComparison.Ordinal);
        first.Should().BeGreaterThanOrEqualTo(0);
        second.Should().BeGreaterThan(first);
        tenth.Should().BeGreaterThan(second);
    }

    [Fact]
    public void Invalid_filename_fails_before_the_runner_can_use_its_connection_string()
    {
        using var fixture = new MigrationFixture();
        fixture.Write("V1__SYS_INVALID_WIDTH.sql");

        var result = Run(fixture.Path, validateOnly: false);

        result.ExitCode.Should().NotBe(0);
        result.Output.Should().Contain("invalid migration file 'V1__SYS_INVALID_WIDTH.sql'");
        result.Output.Should().Contain("expected V###__UPPER_SNAKE_DESCRIPTION.sql");
        result.Output.Should().NotContain("SqlException");
    }

    [Fact]
    public void Duplicate_numeric_version_fails_before_database_access()
    {
        using var fixture = new MigrationFixture();
        fixture.Write("V001__SYS_ONE.sql");
        fixture.Write("V001__MDM_OTHER.sql");

        var result = Run(fixture.Path, validateOnly: false);

        result.ExitCode.Should().NotBe(0);
        result.Output.Should().Contain("duplicate migration version 1");
        result.Output.Should().Contain("V001__MDM_OTHER.sql");
        result.Output.Should().Contain("V001__SYS_ONE.sql");
        result.Output.Should().NotContain("SqlException");
    }

    [Fact]
    public void Runner_serializes_schema_changes_and_rejects_applied_filename_or_content_drift()
    {
        var source = File.ReadAllText(RunnerPath);

        source.Should().Contain("sys.sp_getapplock");
        source.Should().Contain("@LockOwner = N'Session'");
        source.Should().Contain("$appliedByVersion");
        source.Should().Contain("$localByVersion");
        source.Should().Contain("database contains migration absent from this source");
        source.Should().Contain("migration history is not a contiguous source prefix");
        source.Should().Contain("Refuse out-of-order replay");
        source.Should().Contain("if ($DryRun) { $pending | ForEach-Object");
        source.Should().Contain("read-only DryRun treats every local migration as pending");
        source.Should().Contain("migration history drift at version");
        source.Should().Contain("Get-MigrationHash");
        source.Should().Contain("Get-MigrationSqlBatches");
        source.Should().Contain("ADD\\s+CONSTRAINT");
        source.Should().Contain("ConstraintSqls");
        source.Should().Contain("same transaction");
        source.Should().Contain("CONTENT_SHA256");
        source.Should().Contain("migration content drift");
        source.Should().Contain("AdoptMissingChecksums");
        source.Should().Contain("ApproveHighImpactMigrations");
        source.Should().Contain("$highImpactVersions = @(142, 144, 146, 147, 148, 150, 151, 152, 153)");
        source.Should().Contain("high-impact migration approval is required");
        var historyMutation = source.IndexOf(
            "ALTER TABLE SYS_SCHEMA_MIGRATION ADD CONTENT_SHA256",
            StringComparison.Ordinal);
        source.IndexOf("database contains migration absent from this source", StringComparison.Ordinal)
            .Should().BeLessThan(historyMutation,
                "a downlevel runner must reject DB-only versions before changing migration history");
        source.IndexOf("migration history is not a contiguous source prefix", StringComparison.Ordinal)
            .Should().BeLessThan(historyMutation,
                "an out-of-order history must be rejected before changing migration history");
        source.IndexOf("$highImpactVersions = @(142, 144, 146, 147, 148, 150, 151, 152, 153)", StringComparison.Ordinal)
            .Should().BeLessThan(historyMutation,
                "the explicit production approval gate must run before migration-history DDL");
        source.IndexOf("$migrationNamePattern.Match($file.Name)", StringComparison.Ordinal)
            .Should().BeLessThan(source.IndexOf("$conn.Open()", StringComparison.Ordinal),
                "local migration validation must finish before SQL Server access");
    }

    [Fact]
    public async Task Performance_baseline_script_parses_and_fails_closed_on_incomplete_observability()
    {
        var source = File.ReadAllText(PerformanceBaselinePath);
        source.Should().Contain("Query Store must be READ_WRITE");
        source.Should().Contain("metadata-visibility-prerequisite");
        source.Should().Contain("AND i.type IN (1, 2)");
        source.Should().Contain("$serverDataSource = $connection.DataSource");
        source.Should().Contain("statistics-freshness");
        source.Should().Contain("sys.dm_db_stats_properties");
        source.Should().Contain("sys.stats_columns");
        source.Should().Contain("stats_column_id");
        source.Should().Contain("STATISTICS_COLUMNS");
        source.Should().Contain("AUTO_CREATE_STATISTICS_ON");
        source.Should().Contain("AUTO_UPDATE_STATISTICS_ON");
        source.Should().Contain("AUTO_UPDATE_STATISTICS_ASYNC_ON");
        source.Should().Contain("statistics-options-prerequisite");
        source.Should().Contain("query-store-window");
        source.Should().Contain("FIRST_INTERVAL_START");
        source.Should().Contain("LAG(rsi.end_time)");
        source.Should().Contain("GAP_COUNT");
        source.Should().Contain("COVERAGE_COMPLETE");
        source.Should().Contain("query-store-window-coverage-prerequisite");
        source.Should().Contain("does not cover the complete requested");
        source.Should().Contain("QUERY_STORE_DESIRED_STATE");
        source.Should().Contain("QUERY_STORE_CAPTURE_MODE");
        source.Should().Contain("database-compatibility-prerequisite");
        source.Should().Contain("compatibility level 130 or later");
        source.Should().Contain("query-store-capture-prerequisite");
        source.Should().Contain("capture mode NONE");
        source.Should().Contain("query-store-retention-prerequisite");
        source.Should().Contain("stale-query retention");
        source.Should().Contain("$staleQueryDays -ne 0 -and $staleQueryDays -lt $LookbackDays");
        source.Should().Contain("CapturedQueriesAndPlansWithinContinuousRuntimeIntervals");
        source.Should().Contain("ProvesExhaustiveWorkloadCapture = $false");
        source.Should().Contain("QUERY_STORE_INTERVAL_MINUTES");
        source.Should().Contain("QUERY_STORE_MAX_PLANS_PER_QUERY");
        source.Should().Contain("query-store-plan-logical-reads");
        source.Should().Contain("p.plan_id AS PLAN_ID");
        source.Should().Contain("p.query_plan_hash");
        source.Should().Contain("p.is_forced_plan AS IS_FORCED_PLAN");
        source.Should().Contain("p.force_failure_count AS FORCE_FAILURE_COUNT");
        source.Should().Contain("p.last_force_failure_reason AS LAST_FORCE_FAILURE_REASON");
        source.Should().Contain("AVG_LOGICAL_READS");
        source.Should().Contain("MAX(rs.max_logical_io_reads) AS MAX_LOGICAL_READS");
        source.Should().Contain("MAX_DURATION_MS");
        source.Should().Contain("QUERY_TEXT_LENGTH");
        source.Should().MatchRegex(
            @"(?s)query-store-plan-logical-reads.*?GROUP BY\s+q\.query_id,\s+p\.plan_id",
            "plan statistics must not silently collapse back to one row per query");
        source.Should().NotContain("AS QUERY_SQL_TEXT");
        source.Should().NotMatchRegex(@"(?im)\bp\.query_plan\s+AS\s+QUERY_PLAN\b");
        source.Should().NotContain(".sqlplan");
        source.Should().Contain("view-inventory");
        source.Should().Contain("view-dependencies");
        source.Should().Contain("d.referencing_id = v.object_id");
        source.Should().Contain("DEFINITION_SHA256");
        source.Should().Contain("USES_ANSI_NULLS");
        source.Should().Contain("USES_QUOTED_IDENTIFIER");
        source.Should().NotContain("AS VIEW_DEFINITION");
        source.Should().Contain("indexed-view-index-definition");
        source.Should().Contain("IS_SCHEMA_BOUND");
        source.Should().Contain("FOR XML PATH(''), TYPE");
        source.Should().NotContain("STRING_AGG");
        source.Should().Contain("ic.key_ordinal > 0");
        source.Should().Contain("ic.partition_ordinal > 0");
        source.Should().Contain("PARTITION_COLUMNS");
        source.Should().Contain("if ($IncludePhysicalStats)");
        source.Should().Contain("PhysicalStatsMinPageCount");
        source.Should().Contain("sys.dm_db_index_physical_stats");
        source.Should().Contain("candidate.object_id, candidate.index_id, NULL, 'LIMITED'");
        source.Should().NotMatchRegex(@"(?im)^\s*(CREATE|ALTER|DROP)\s+(INDEX|STATISTICS)\b");
        source.Should().NotMatchRegex(@"(?im)^\s*(UPDATE\s+STATISTICS|DBCC\b)");

        var startInfo = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment["NEXA_TEST_PS_SCRIPT"] = PerformanceBaselinePath;
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            "$tokens=$null; $errors=$null; " +
            "[void][System.Management.Automation.Language.Parser]::ParseFile(" +
            "$env:NEXA_TEST_PS_SCRIPT,[ref]$tokens,[ref]$errors); " +
            "if($errors.Count){$errors | ForEach-Object Message; exit 1}");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start pwsh for baseline-script validation.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Baseline PowerShell parser validation did not finish within 30 seconds.");
        }
        var standardOutput = await output;
        var standardError = await error;
        process.ExitCode.Should().Be(0, standardOutput + Environment.NewLine + standardError);
    }

    private static ProcessResult Run(string migrationsPath, bool validateOnly)
    {
        var startInfo = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(RunnerPath);
        startInfo.ArgumentList.Add("-MigrationsPath");
        startInfo.ArgumentList.Add(migrationsPath);
        if (validateOnly)
        {
            startInfo.ArgumentList.Add("-ValidateOnly");
        }
        else
        {
            // If validation were accidentally moved after Open(), this endpoint would fail with a
            // connection error. A one-second timeout keeps that regression test bounded.
            startInfo.ArgumentList.Add("-ConnectionString");
            startInfo.ArgumentList.Add(
                "Server=127.0.0.1,1;Database=NeverOpened;User Id=invalid;Password=invalid;" +
                "Connect Timeout=1;Encrypt=False");
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start pwsh for migration-runner validation.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Migration-runner validation did not finish within 30 seconds.");
        }

        Task.WaitAll(output, error);
        return new ProcessResult(process.ExitCode, output.Result + Environment.NewLine + error.Result);
    }

    private sealed class MigrationFixture : IDisposable
    {
        public MigrationFixture()
        {
            Path = Directory.CreateTempSubdirectory("nexa-migration-runner-").FullName;
        }

        public string Path { get; }

        public void Write(string fileName)
            => File.WriteAllText(System.IO.Path.Combine(Path, fileName), "-- test migration");

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* best-effort temporary test cleanup */ }
        }
    }

    private sealed record ProcessResult(int ExitCode, string Output);
}
