using System.Diagnostics;

namespace NexaOne.UnitTests.Persistence;

public sealed class MssqlMigrationRunnerTests
{
    private static string RunnerPath
        => RepositorySource.GetFile("tools", "ops", "Apply-MssqlMigrations.ps1");

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
    public void Runner_serializes_schema_changes_and_rejects_applied_filename_drift()
    {
        var source = File.ReadAllText(RunnerPath);

        source.Should().Contain("sys.sp_getapplock");
        source.Should().Contain("@LockOwner = N'Session'");
        source.Should().Contain("$appliedByVersion");
        source.Should().Contain("migration history drift at version");
        source.IndexOf("$migrationNamePattern.Match($file.Name)", StringComparison.Ordinal)
            .Should().BeLessThan(source.IndexOf("$conn.Open()", StringComparison.Ordinal),
                "local migration validation must finish before SQL Server access");
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
