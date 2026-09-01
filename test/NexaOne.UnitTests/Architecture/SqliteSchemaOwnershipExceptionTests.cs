using System.Text.RegularExpressions;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.UnitTests.Architecture;

public sealed class SqliteSchemaOwnershipExceptionTests
{
    private static readonly Regex ModuleSchemaIdentifier = new(
        @"\b(?:MDM|EST|FDC|RMS|QMS|EMS|IVT|POM|MRP|SHP|SYS|SLS|PRC)_[A-Z0-9_]+\b",
        RegexOptions.CultureInvariant);

    // Frozen legacy debt. A module identifier can leave this list when its reconciliation moves
    // behind a module contribution, but no new identifier may enter the common initializer.
    private static readonly string[] FrozenLegacyModuleIdentifiers =
    [
        "EMS_EQUIPMENT_PART_BOM",
        "EMS_MAINTENANCE_PLAN",
        "EMS_SPARE_MASTER_COMMAND",
        "EMS_SPARE_PART_INOUT",
        "EMS_SPARE_PART_STOCK_POLICY",
        "EMS_SPARE_PART_SUPPLIER",
        "EMS_SPARE_PART_USAGE",
        "EMS_TOOL",
        "EMS_TOOL_MOUNT_HISTORY",
        "EMS_TOOL_SAVE_COMMAND",
        "EMS_TOOL_USAGE_HISTORY",
        "EMS_WORK_ORDER",
        "EMS_WORK_ORDER_CREATE_COMMAND",
        "EST_EQUIPMENT_OUTPUT_EVENT",
        "EST_OEE_LOSS",
        "EST_OEE_SUMMARY",
        "EST_TAKT_SUMMARY",
        "EST_UTILITY_METER",
        "EST_UTILITY_METER_CONFIG_HISTORY",
        "EST_UTILITY_METER_EVENT",
        "EST_UTILITY_READING",
        "FDC_ALARM_HISTORY",
        "FDC_COLLECT_DATA",
        "FDC_EQUIPMENT_ENDPOINT",
        "FDC_INTERLOCK_HISTORY",
        "FDC_PARAMETER_ID",
        "FDC_RUNTIME_OWNERSHIP",
        "FDC_TRACE_RETENTION_STATE",
        "IVT_FEED_SESSION_COMMAND",
        "IVT_MATERIAL_CONSUMPTION_HISTORY",
        "IVT_MATERIAL_FEED_SESSION",
        "IVT_MATERIAL_LOT",
        "IVT_TRACE_BINDING_COMMAND",
        "IVT_TRACE_CONSUMPTION_BINDING",
        "IVT_TRACE_INGESTION_CURSOR",
        "IVT_TRACE_PROJECTION_INBOX",
        "MDM_CARRIER",
        "MDM_EQUIPMENT",
        "MDM_EQUIPMENT_CHANGE_HISTORY",
        "POM_LOT",
        "POM_LOT_DEFECT_EXECUTION",
        "POM_LOT_DISPOSITION",
        "POM_LOT_EXECUTION",
        "POM_LOT_HISTORY",
        "POM_LOT_MIXING_RELATION",
        "POM_PRODUCTION_ORDER",
        "POM_PRODUCTION_PLAN",
        "POM_ROUTE_EXCEPTION",
        "POM_WORK_ORDER",
        "POM_WORK_ORDER_EXECUTION",
        "POM_WORK_SCOPE",
        "POM_WORK_SCOPE_EXECUTION",
        "POM_WORK_SCOPE_MEMBER",
        "QMS_AI_INFERENCE",
        "QMS_AI_MODEL_VERSION",
        "QMS_AI_REVIEW",
        "QMS_INSPECTION",
        "QMS_INSPECTION_EVENT",
        "QMS_INSPECTION_RESULT",
        "QMS_INSPECTION_RESULT_V1",
        "QMS_INSPECTION_SPEC",
        "QMS_SAMPLING_PLAN_REVISION",
        "RMS_RECIPE_APPROVAL_HISTORY",
        "RMS_RECIPE_COMMAND",
        "RMS_RECIPE_EQUIPMENT_ASSIGNMENT",
        "RMS_RECIPE_PARAM_COMMAND",
        "SYS_MULTI_LANGUAGE_RESOURCE",
        "SYS_ROLE",
        "SYS_SQLITE_RECONCILIATION",
        "SYS_USER",
    ];

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
            "FDC_COLLECT_DATA",
            "FDC_INTERLOCK_HISTORY",
            "FDC_RUNTIME_OWNERSHIP",
            "FDC_TRACE_RETENTION_STATE",
            "IVT_FEED_SESSION_COMMAND",
            "IVT_MATERIAL_CONSUMPTION_HISTORY",
            "IVT_MATERIAL_FEED_SESSION",
            "IVT_MATERIAL_LOT",
            "IVT_TRACE_BINDING_COMMAND",
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

    [Fact]
    public void Pom_projection_sqlite_reconciliation_is_module_owned_across_the_entire_common_initializer()
    {
        var initializer = File.ReadAllText(RepositorySource.GetFile(
            "src", "02.Backend", "NexaOne.Common", "Infrastructure", "Persistence",
            "SqliteSchemaInitializer.cs"));
        initializer.Should().NotContain("POM_WORK_SCOPE_PROJECTION_INBOX");
        initializer.Should().NotContain("POM_WORK_SCOPE_PROJECTION_CURRENT");
        initializer.Should().NotContain("EnsurePomWorkScopeProjectionIntegrity");

        var contribution = File.ReadAllText(RepositorySource.GetFile(
            "src", "04.Modules", "NexaOne.POM", "Infrastructure",
            "PomWorkScopeProjectionSqliteSchemaContribution.cs"));
        contribution.Should().Contain("ISqliteSchemaContribution");
        contribution.Should().Contain("POM_WORK_SCOPE_PROJECTION_INBOX");
        contribution.Should().Contain("POM_WORK_SCOPE_PROJECTION_CURRENT");

        var pomSpring = File.ReadAllText(RepositorySource.GetFile(
            "src", "00.Main", "NexaOne.Server", "config", "modules", "pom.xml"));
        pomSpring.Should().Contain("pomWorkScopeProjectionSqliteSchemaContribution");
        pomSpring.Should().Contain("factory-object=\"pomModule\"");
        pomSpring.Should().Contain("factory-method=\"GetWorkScopeProjectionSqliteSchemaContribution\"");
        pomSpring.Should().NotContain("PomWorkScopeProjectionSqliteSchemaContribution, NexaOne.POM");

        var module = File.ReadAllText(RepositorySource.GetFile(
            "src", "04.Modules", "NexaOne.POM", "Module.cs"));
        module.Should().Contain("GetWorkScopeProjectionSqliteSchemaContribution");
        module.Should().Contain("new PomWorkScopeProjectionSqliteSchemaContribution()");
    }

    [Fact]
    public void Trusted_authority_sqlite_reconciliation_is_owned_by_rms_and_sys_modules()
    {
        var initializer = File.ReadAllText(RepositorySource.GetFile(
            "src", "02.Backend", "NexaOne.Common", "Infrastructure", "Persistence",
            "SqliteSchemaInitializer.cs"));
        initializer.Should().NotContain("RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE");
        initializer.Should().NotContain("SYS_RELEASED_PROGRAM_ARTIFACT");
        initializer.Should().NotContain("EnsureTrustedAuthorityEvidenceIntegrity");

        AssertContributionExport(
            moduleName: "RMS",
            contributionFile: "RmsTrustedAuthoritySqliteSchemaContribution.cs",
            contributionType: "RmsTrustedAuthoritySqliteSchemaContribution",
            springBean: "rmsTrustedAuthoritySqliteSchemaContribution",
            ownedTable: "RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE");
        AssertContributionExport(
            moduleName: "SYS",
            contributionFile: "SysTrustedAuthoritySqliteSchemaContribution.cs",
            contributionType: "SysTrustedAuthoritySqliteSchemaContribution",
            springBean: "sysTrustedAuthoritySqliteSchemaContribution",
            ownedTable: "SYS_RELEASED_PROGRAM_ARTIFACT");

        var sysContribution = File.ReadAllText(RepositorySource.GetFile(
            "src", "04.Modules", "NexaOne.SYS", "Infrastructure",
            "SysTrustedAuthoritySqliteSchemaContribution.cs"));
        sysContribution.Should().Contain("UX_SYS_RELEASED_PROGRAM_ARTIFACT_COORDINATE");
        sysContribution.Should().Contain("TR_SYS_RELEASED_PROGRAM_ARTIFACT_COORDINATE_BI");
        sysContribution.Should().Contain("PROGRAM_SCHEMA COLLATE BINARY");
        sysContribution.Should().NotContain("PROGRAM_HASH COLLATE BINARY,");
    }

    [Fact]
    public void Entire_common_initializer_cannot_acquire_new_module_schema_identifiers()
    {
        var initializer = File.ReadAllText(RepositorySource.GetFile(
            "src", "02.Backend", "NexaOne.Common", "Infrastructure", "Persistence",
            "SqliteSchemaInitializer.cs"));
        var identifiers = ModuleSchemaIdentifier.Matches(initializer)
            .Select(static match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        identifiers.Should().Equal(FrozenLegacyModuleIdentifiers,
            "the full common initializer is a frozen legacy boundary; new module reconciliation " +
            "must use ISqliteSchemaContribution");
    }

    [Theory]
    [InlineData(nameof(SqliteSchemaInitializer.EnsureSchema))]
    [InlineData(nameof(SqliteSchemaInitializer.Apply))]
    [InlineData(nameof(SqliteSchemaInitializer.CreateMissingTables))]
    public void Legacy_one_argument_sqlite_initializer_binary_contract_is_preserved(string methodName)
    {
        typeof(SqliteSchemaInitializer).GetMethod(methodName, [typeof(string)])
            .Should().NotBeNull("already-compiled hosts and plugins bind the exact one-argument signature");
    }

    private static void AssertContributionExport(
        string moduleName,
        string contributionFile,
        string contributionType,
        string springBean,
        string ownedTable)
    {
        var contribution = File.ReadAllText(RepositorySource.GetFile(
            "src", "04.Modules", $"NexaOne.{moduleName}", "Infrastructure", contributionFile));
        contribution.Should().Contain("ISqliteSchemaContribution");
        contribution.Should().Contain(ownedTable);

        var module = File.ReadAllText(RepositorySource.GetFile(
            "src", "04.Modules", $"NexaOne.{moduleName}", "Module.cs"));
        module.Should().Contain("GetTrustedAuthoritySqliteSchemaContribution");
        module.Should().Contain($"new {contributionType}()");

        var spring = File.ReadAllText(RepositorySource.GetFile(
            "src", "00.Main", "NexaOne.Server", "config", "modules",
            $"{moduleName.ToLowerInvariant()}.xml"));
        spring.Should().Contain(springBean);
        spring.Should().Contain($"factory-object=\"{moduleName.ToLowerInvariant()}Module\"");
        spring.Should().Contain("factory-method=\"GetTrustedAuthoritySqliteSchemaContribution\"");
        spring.Should().NotContain($"{contributionType}, NexaOne.{moduleName}");
    }
}
