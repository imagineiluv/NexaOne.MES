using System.Text.RegularExpressions;

namespace NexaOne.UnitTests.Architecture;

public sealed class SqliteSchemaOwnershipExceptionTests
{
    private static readonly Regex ModuleSchemaIdentifier = new(
        @"\b(?:MDM|EST|FDC|RMS|QMS|EMS|IVT|POM|MRP|SHP|SYS|SLS|PRC)_[A-Z0-9_]+\b",
        RegexOptions.CultureInvariant);

    [Fact]
    public void Temporary_sqlite_reconciliation_exception_is_limited_to_adr_0004_targets()
    {
        var initializerPath = RepositorySource.GetFile(
            "src", "02.Backend", "NexaOne.Common", "Infrastructure", "Persistence",
            "SqliteSchemaInitializer.cs");
        var source = File.ReadAllText(initializerPath);
        var ensureSchema = source.IndexOf("public static void EnsureSchema", StringComparison.Ordinal);
        var traceStart = source.IndexOf(
            "private static void EnsureTraceProjectionPerformanceSchema", StringComparison.Ordinal);
        var fdcStart = source.IndexOf(
            "private static void EnsureFdcInterlockEffectLifecycleSchema", StringComparison.Ordinal);
        var fdcEnd = source.IndexOf(
            "private static void EnsureFdcOpenStateIndexes", fdcStart, StringComparison.Ordinal);

        ensureSchema.Should().BePositive();
        traceStart.Should().BeGreaterThan(ensureSchema);
        fdcStart.Should().BeGreaterThan(traceStart);
        fdcEnd.Should().BeGreaterThan(fdcStart);

        var exceptionSource = string.Concat(
            source.AsSpan(0, ensureSchema),
            source.AsSpan(traceStart, fdcStart - traceStart),
            source.AsSpan(fdcStart, fdcEnd - fdcStart));
        var identifiers = ModuleSchemaIdentifier.Matches(exceptionSource)
            .Select(static match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        identifiers.Should().BeEquivalentTo(
        [
            "FDC_INTERLOCK_HISTORY",
            "IVT_TRACE_CONSUMPTION_BINDING",
            "IVT_TRACE_INGESTION_CURSOR",
            "IVT_TRACE_PROJECTION_INBOX",
            "SYS_SQLITE_RECONCILIATION",
        ], options => options.WithStrictOrdering(),
            "ADR-0004 permits only the exact FDC/IVT reconciliation targets and the technical ledger");

        var adr = File.ReadAllText(RepositorySource.GetFile(
            "docs", "adr", "0004-temporary-sqlite-module-schema-bootstrap.md"));
        adr.Should().Contain("검토 기한: 2026-11-30");
        adr.Should().Contain("금지 동작");
        adr.Should().Contain("NexaFramework 이관과 Production release 승인 전에");
    }
}
