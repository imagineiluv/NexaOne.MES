using System.Diagnostics;
using System.Text.RegularExpressions;

namespace NexaOne.UnitTests.Persistence;

public sealed class TrustedAuthoritySecurityContractTests
{
    private static string MigrationPath => RepositorySource.GetFile(
        "src", "00.Main", "NexaOne.Server", "config", "db", "migrations",
        "V160__TRUSTED_AUTHORITY_WRITER_SECURITY.sql");

    private static string CommissioningPath => RepositorySource.GetFile(
        "tools", "ops", "Set-TrustedAuthoritySecurity.ps1");

    [Fact]
    public void V160_defines_a_static_same_owner_writer_boundary_and_sqlite_no_op()
    {
        var sql = File.ReadAllText(MigrationPath);

        File.ReadLines(MigrationPath).First().Should()
            .Be("-- Owner: POM/RMS/SYS integration boundary (ADR-0005).");
        Regex.Matches(sql, @"-- SQLITE-OMIT-BEGIN").Should().HaveCount(1);
        Regex.Matches(sql, @"-- SQLITE-OMIT-END").Should().HaveCount(1);
        sql.IndexOf("-- SQLITE-OMIT-BEGIN", StringComparison.Ordinal).Should().BeLessThan(
            sql.IndexOf("CREATE ROLE NexaOneProjectionRuntime", StringComparison.Ordinal));
        sql.IndexOf("-- SQLITE-OMIT-END", StringComparison.Ordinal).Should().BeGreaterThan(
            sql.IndexOf("DENY EXECUTE ON OBJECT::dbo.POM_INSERT_WORK_SCOPE_PROJECTION_AUTHORITY", StringComparison.Ordinal));

        sql.Should().Contain("CREATE ROLE NexaOneProjectionRuntime AUTHORIZATION dbo");
        sql.Should().Contain("CREATE ROLE NexaOneRmsEvidenceWriter AUTHORIZATION dbo");
        sql.Should().Contain("CREATE ROLE NexaOneSysReleaseWriter AUTHORIZATION dbo");
        sql.Should().Contain("CREATE PROCEDURE dbo.RMS_CAPTURE_CANONICAL_RECIPE_EXECUTION_EVIDENCE");
        sql.Should().Contain("CREATE PROCEDURE dbo.SYS_RELEASE_PROGRAM_ARTIFACT");
        sql.Should().Contain("CREATE PROCEDURE dbo.SYS_REVOKE_PROGRAM_ARTIFACT");
        sql.Should().Contain("CREATE PROCEDURE dbo.POM_INSERT_WORK_SCOPE_PROJECTION_AUTHORITY");
        sql.Should().Contain("CREATE PROCEDURE dbo.POM_GET_ACTIVE_PROJECTION_AUTHORITY_FOR_UPDATE");
        sql.Should().Contain("CREATE PROCEDURE dbo.POM_ADVANCE_WORK_SCOPE_PROJECTION_AUTHORITY_LINEAGE");
        sql.Should().Contain("SET ANSI_NULLS ON;");
        sql.Should().Contain("SET QUOTED_IDENTIFIER ON;");
        sql.Should().Contain("SET ANSI_PADDING ON;");
        sql.Should().Contain("SET ANSI_WARNINGS ON;");
        sql.Should().Contain("SET ARITHABORT ON;");
        sql.Should().Contain("SET CONCAT_NULL_YIELDS_NULL ON;");
        sql.Should().Contain("SET NUMERIC_ROUNDABORT OFF;");
        sql.Should().NotContain("GRANT UPDATE (LAST_APPLIED_VERSION_NO, LAST_APPLIED_AT)");
        sql.Should().NotContain("sp_executesql");
        sql.Should().NotMatchRegex(@"(?i)\bCREATE\s+(?:OR\s+ALTER\s+)?(?:LOGIN|USER)\b");
        sql.Should().NotMatchRegex(@"(?i)\bPASSWORD\s*=");

        sql.Should().Contain("CREATE TABLE dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING");
        sql.Should().Contain("DATABASE_PRINCIPAL_SID");
        sql.Should().Contain("ARTIFACT_ID");
        sql.Should().Contain("PRODUCT_PROFILE_ID");
        sql.Should().Contain("PLUGIN_ID");
        sql.Should().Contain("PRODUCT_DEFINITION_VERSION");
        sql.Should().Contain("PROGRAM_VERSION");
        sql.Should().Contain("PROGRAM_SCHEMA");
        sql.Should().Contain("PROGRAM_HASH");
        sql.Should().Contain("BOUND_RECIPE_SNAPSHOT_HASH");
        sql.Should().Contain("CAPTURED_DATABASE_PRINCIPAL_SID");
        sql.Should().Contain("RELEASED_DATABASE_PRINCIPAL_SID");
        sql.Should().Contain("REVOKED_DATABASE_PRINCIPAL_SID");
        sql.Should().Contain("PROVISIONED_DATABASE_PRINCIPAL_SID");
        sql.Should().Contain("TR_POM_WORK_SCOPE_PROJECTION_AUTHORITY_PRINCIPAL_PROVENANCE");
        sql.Should().Contain("DENY SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING TO public");
        sql.Should().Contain("DENY SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.POM_WORK_SCOPE_PROJECTION_AUTHORITY TO public");
        sql.Should().Contain("CREATE VIEW dbo.POM_ACTIVE_PROJECTION_RUNTIME_AUTHORITY");
        sql.Should().Contain("CREATE VIEW dbo.POM_PROJECTION_AUTHORITY_SCOPE_FENCE");
        sql.Should().Contain("GRANT SELECT ON OBJECT::dbo.POM_ACTIVE_PROJECTION_RUNTIME_AUTHORITY TO NexaOneProjectionRuntime");
        sql.Should().Contain("GRANT SELECT ON OBJECT::dbo.POM_PROJECTION_AUTHORITY_SCOPE_FENCE TO NexaOneProjectionRuntime");
        Regex.Matches(sql, @"(?m)^\s+@\w+ (?:N?VARCHAR)\(MAX\)").Count.Should().BeGreaterThan(20,
            "external text inputs must not be truncated by procedure parameter binding");
        sql.Should().Contain("PATINDEX(N'%[' + NCHAR(1) + N'-' + NCHAR(31) + NCHAR(127) + N']%'");
        var lineageStart = sql.IndexOf(
            "CREATE PROCEDURE dbo.POM_ADVANCE_WORK_SCOPE_PROJECTION_AUTHORITY_LINEAGE",
            StringComparison.Ordinal);
        var lineageEnd = sql.IndexOf(
            "CREATE VIEW dbo.POM_ACTIVE_PROJECTION_RUNTIME_AUTHORITY",
            lineageStart,
            StringComparison.Ordinal);
        var lineage = sql[lineageStart..lineageEnd];
        lineage.Should().Contain("IF @@TRANCOUNT = 0");
        lineage.Should().Contain("BEGIN TRY");
        lineage.Should().Contain("IF @StartedTransaction = 1 COMMIT TRANSACTION");
        lineage.Should().Contain("ROLLBACK TRANSACTION");
    }

    [Fact]
    public void POM_procedure_owns_one_global_lock_order_and_uses_server_derived_policy()
    {
        var sql = File.ReadAllText(MigrationPath);
        var start = sql.IndexOf(
            "CREATE PROCEDURE dbo.POM_INSERT_WORK_SCOPE_PROJECTION_AUTHORITY",
            StringComparison.Ordinal);
        var end = sql.IndexOf(
            "CREATE VIEW dbo.POM_ACTIVE_PROJECTION_RUNTIME_AUTHORITY",
            start,
            StringComparison.Ordinal);
        var procedure = sql[start..end];

        procedure.Should().NotContain("@ProductProfileId");
        procedure.Should().NotContain("@PluginId");
        procedure.Should().NotContain("@ProductDefinitionVersion");
        procedure.Should().NotContain("@ProgramVersion");
        procedure.Should().NotContain("@BaselineVersionNo");
        procedure.Should().Contain("USER_NAME()");
        procedure.Should().Contain("DATABASE_PRINCIPAL_ID(@RuntimePrincipalName)");

        var authorityLock = procedure.IndexOf(
            "FROM dbo.POM_WORK_SCOPE_PROJECTION_AUTHORITY A WITH (UPDLOCK, HOLDLOCK)",
            StringComparison.Ordinal);
        var rmsLock = procedure.IndexOf(
            "FROM dbo.RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE R WITH (UPDLOCK, HOLDLOCK)",
            StringComparison.Ordinal);
        var artifactLock = procedure.IndexOf(
            "FROM dbo.SYS_RELEASED_PROGRAM_ARTIFACT A WITH (UPDLOCK, HOLDLOCK)",
            StringComparison.Ordinal);
        var revocationLock = procedure.IndexOf(
            "FROM dbo.SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION V WITH (UPDLOCK, HOLDLOCK)",
            StringComparison.Ordinal);
        var bindingLock = procedure.IndexOf(
            "FROM dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING B WITH (UPDLOCK, HOLDLOCK)",
            StringComparison.Ordinal);
        authorityLock.Should().BePositive().And.BeLessThan(rmsLock);
        rmsLock.Should().BeLessThan(artifactLock);
        artifactLock.Should().BeLessThan(revocationLock);
        revocationLock.Should().BeLessThan(bindingLock);
    }

    [Fact]
    public void Writer_procedures_bound_MAX_inputs_before_XML_or_control_character_work()
    {
        var sql = File.ReadAllText(MigrationPath);
        var procedureNames = new[]
        {
            "dbo.RMS_CAPTURE_CANONICAL_RECIPE_EXECUTION_EVIDENCE",
            "dbo.SYS_RELEASE_PROGRAM_ARTIFACT",
            "dbo.SYS_REVOKE_PROGRAM_ARTIFACT",
            "dbo.POM_INSERT_WORK_SCOPE_PROJECTION_AUTHORITY",
            "dbo.POM_GET_ACTIVE_PROJECTION_AUTHORITY_FOR_UPDATE",
            "dbo.POM_ADVANCE_WORK_SCOPE_PROJECTION_AUTHORITY_LINEAGE",
        };

        foreach (var procedureName in procedureNames)
        {
            var start = sql.IndexOf("CREATE PROCEDURE " + procedureName, StringComparison.Ordinal);
            start.Should().BePositive();
            var next = sql.IndexOf("\nCREATE ", start + 1, StringComparison.Ordinal);
            var body = sql[start..(next < 0 ? sql.Length : next)];
            var firstThrow = body.IndexOf("THROW 516", StringComparison.Ordinal);
            var xmlProbe = body.IndexOf("DECLARE @InputCharacterProbe XML", StringComparison.Ordinal);
            firstThrow.Should().BePositive().And.BeLessThan(xmlProbe,
                $"{procedureName} must reject oversized MAX values before XML serialization");
        }
    }

    [Fact]
    public void SqlServer_runtime_paths_use_active_authority_while_scope_mutation_keeps_a_lifetime_fence()
    {
        var infrastructure = RepositorySource.GetDirectory(
            "src", "04.Modules", "NexaOne.POM", "Infrastructure");
        var workScopes = File.ReadAllText(Path.Combine(infrastructure, "WorkScopeRepository.cs"));
        var authority = File.ReadAllText(Path.Combine(
            infrastructure, "WorkScopeProjectionAuthorityRepository.cs"));
        var ingestion = File.ReadAllText(Path.Combine(
            infrastructure, "WorkScopeProjectionRepository.cs"));
        var store = File.ReadAllText(Path.Combine(
            infrastructure, "WorkScopeProjectionStore.cs"));

        workScopes.Should().Contain(
            "FROM POM_PROJECTION_AUTHORITY_SCOPE_FENCE WITH (UPDLOCK, HOLDLOCK)");
        authority.Should().Contain("FROM POM_ACTIVE_PROJECTION_RUNTIME_AUTHORITY");
        authority.Should().Contain("EXEC dbo.POM_INSERT_WORK_SCOPE_PROJECTION_AUTHORITY");
        authority.Should().NotContain("@BaselineVersionNo = @BaselineVersionNo");
        ingestion.Should().Contain(
            "EXEC dbo.POM_GET_ACTIVE_PROJECTION_AUTHORITY_FOR_UPDATE");
        store.Should().Contain(
            "EXEC dbo.POM_GET_ACTIVE_PROJECTION_AUTHORITY_FOR_UPDATE");
        store.Should().Contain(
            "EXEC dbo.POM_ADVANCE_WORK_SCOPE_PROJECTION_AUTHORITY_LINEAGE");
        store.Should().Contain(
            "_isSqlServer ? ReadinessSelectSqlSqlServer : ReadinessSelectSql");
        store.Should().Contain("ReadinessSelectSql.Replace(");
        store.Should().Contain("\"POM_WORK_SCOPE_PROJECTION_AUTHORITY U\"");
        store.Should().Contain("\"POM_ACTIVE_PROJECTION_RUNTIME_AUTHORITY U\"");
    }

    [Fact]
    public void Commissioning_writer_membership_keys_are_independent_array_items()
    {
        var source = File.ReadAllText(CommissioningPath);

        source.Should().MatchRegex(
            @"(?s)foreach \(\$key in @\(\s*" +
            @"\('NexaOneRmsEvidenceWriter\|' \+ \$RmsWriterDatabaseUser\)\s*" +
            @"\('NexaOneSysReleaseWriter\|' \+ \$SysWriterDatabaseUser\)\s*\)\)");
    }

    [Fact]
    public void Commissioning_only_exempts_the_exact_sql_server_policy_certificate_grant()
    {
        var source = File.ReadAllText(CommissioningPath);

        source.Should().MatchRegex(
            @"(?s)AND NOT \(\s*D\.permission_name COLLATE Latin1_General_100_BIN2\s*" +
            @"=N'CONTROL SERVER' COLLATE Latin1_General_100_BIN2\s*" +
            @"AND D\.grantor_principal_id=1\s*" +
            @"AND G\.type COLLATE Latin1_General_100_BIN2\s*" +
            @"=N'C' COLLATE Latin1_General_100_BIN2\s*" +
            @"AND G\.name COLLATE Latin1_General_100_BIN2\s*" +
            @"=N'##MS_PolicySigningCertificate##' COLLATE Latin1_General_100_BIN2\s*" +
            @"AND DATALENGTH\(CONVERT\(NVARCHAR\(MAX\), G\.name\)\)\s*" +
            @"=DATALENGTH\(N'##MS_PolicySigningCertificate##'\)\s*\)");
        source.Should().NotMatchRegex(@"(?i)LIKE\s+N?'##MS_[^']*'");
        Regex.Matches(
                source,
                @"D\.state COLLATE Latin1_General_100_BIN2 IN \(N'G',N'W'\)")
            .Should().HaveCount(2);
        Regex.Matches(
                source,
                @"(?s)G\.name COLLATE Latin1_General_100_BIN2\s*" +
                @"<>N'sysadmin' COLLATE Latin1_General_100_BIN2\s*" +
                @"OR DATALENGTH\(CONVERT\(NVARCHAR\(MAX\), G\.name\)\)" +
                @"<>DATALENGTH\(N'sysadmin'\)")
            .Should().HaveCount(2);
        source.Should().MatchRegex(
            @"D\.permission_name COLLATE Latin1_General_100_BIN2\s*" +
            @"<>A\.ALLOWED_PERMISSION COLLATE Latin1_General_100_BIN2");
    }

    [Fact]
    public async Task Commissioning_is_parser_valid_explicit_atomic_and_credential_free()
    {
        var source = File.ReadAllText(CommissioningPath);

        source.Should().Contain("[switch]$ValidateOnly");
        source.Should().Contain("[switch]$Apply");
        source.Should().Contain("[switch]$WriterBootstrapOnly");
        source.Should().Contain("[switch]$Decommission");
        source.Should().Contain("[string]$ApprovedReleasePrincipalSidSha256");
        source.Should().Contain("-cnotmatch '^[0-9A-F]{64}$'");
        source.Should().Contain(
            "ApprovedReleasePrincipalSidSha256 is valid only for a full Apply.");
        source.Should().Contain("BeginTransaction([System.Data.IsolationLevel]::Serializable)");
        source.Should().Contain("Get-ReleaseProvenanceForBinding");
        source.Should().Contain("Set-RuntimeProductBinding");
        source.Should().Contain("EXECUTE AS USER");
        source.Should().Contain("EXECUTE AS LOGIN");
        Regex.Matches(source, @"EXECUTE AS (?:USER|LOGIN)").Should().HaveCount(3);
        Regex.Matches(
                source,
                @"BEGIN CATCH\s+SELECT @ErrorMessage=ERROR_MESSAGE\(\)," +
                @" @ErrorSeverity=ERROR_SEVERITY\(\),\s+@ErrorState=ERROR_STATE\(\);" +
                @"\s+END CATCH;\s+REVERT;")
            .Should().HaveCount(3,
                "every commissioning impersonation must restore the session before reporting an error");
        Regex.Matches(
                source,
                @"RAISERROR\(N''%s'', @ErrorSeverity, @ErrorState, @ErrorMessage\);")
            .Should().HaveCount(3);
        Regex.Matches(
                source,
                @"(?m)^[ \t]*BEGIN\r?\n[ \t]+;THROW\s+516\d{2},[^\r\n]+;\r?\n[ \t]*END;")
            .Should().HaveCount(9,
                "every conditional commissioning THROW must remain inside an explicit block");
        var conditionalThrowPatterns = new[]
        {
            @"(?s)IF @runtimeSid IS NULL\s+BEGIN\s+;THROW 51620, 'Runtime database principal disappeared during commissioning', 1;\s+END;",
            @"(?s)IF @sysWriterSid IS NULL\s+BEGIN\s+;THROW 51620, 'SYS writer database principal disappeared during commissioning', 1;\s+END;",
            @"(?s)IF @@ROWCOUNT<>1\s+BEGIN\s+;THROW 51622, 'Commissioned product coordinate is not an exact released artifact', 1;\s+END;",
            @"(?s)IF EXISTS \(\s+SELECT 1 FROM dbo\.SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION.*?\)\s+BEGIN\s+;THROW 51623, 'A revoked program artifact cannot be commissioned', 1;\s+END;",
            @"(?s)IF @sid IS NULL\s+BEGIN\s+;THROW 51620, 'Runtime database principal disappeared during commissioning', 1;\s+END;",
            @"(?s)IF @remove=0 AND NOT EXISTS \(\s+SELECT 1\s+FROM dbo\.SYS_RELEASED_PROGRAM_ARTIFACT.*?\)\s+BEGIN\s+;THROW 51622, 'Commissioned product coordinate is not an exact released artifact', 1;\s+END;",
            @"(?s)IF @remove=0 AND EXISTS \(\s+SELECT 1 FROM dbo\.SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION.*?\)\s+BEGIN\s+;THROW 51623, 'A revoked program artifact cannot be commissioned', 1;\s+END;",
            @"(?s)IF NOT EXISTS \(\s+SELECT 1 FROM dbo\.POM_PROJECTION_RUNTIME_PRODUCT_BINDING.*?\)\s+BEGIN\s+;THROW 51624, 'Existing runtime artifact binding conflicts with the requested exact coordinate', 1;\s+END;",
            @"(?s)IF ISNULL\(IS_SRVROLEMEMBER\(N'sysadmin'\), 0\)<>1.*?BEGIN\s+;THROW 51629, 'Commissioning principal cannot audit server impersonation permissions completely', 1;\s+END;",
        };
        foreach (var pattern in conditionalThrowPatterns)
        {
            source.Should().MatchRegex(pattern,
                "each security failure must remain guarded by its commissioning condition");
        }
        Regex.Matches(
                source,
                @"DATALENGTH\(CONVERT\(NVARCHAR\(MAX\), @artifact\)\)\)\s+BEGIN\s+;THROW 51623,")
            .Should().HaveCount(2,
                "both revocation EXISTS predicates must close before entering their THROW block");

        source.Should().NotMatchRegex(@"(?m)^\s*THROW\s+516\d{2},");
        source.Should().Contain("IMPERSONATE ANY USER");
        source.Should().Contain("IMPERSONATE ANY LOGIN");
        source.Should().Contain("FROM sys.server_permissions D");
        source.Should().Contain("D.class=4");
        source.Should().Contain("D.class=101");
        source.Should().Contain("UNSAFE_SECURITY_SCOPE");
        source.Should().Contain("Assert-AuthorityExecuteAcl");
        source.Should().Contain("Assert-AuthorityDatabaseBoundary");
        source.Should().Contain("is_trustworthy_on");
        source.Should().Contain("is_db_chaining_on");
        source.Should().Contain("cross db ownership chaining");
        source.Should().Contain("FROM sys.synonyms S");
        source.Should().Contain("SynonymReach");
        source.Should().Contain("Assert-AuthorityModuleClosure");
        source.Should().Contain("sys.sql_expression_dependencies");
        source.Should().Contain("execute_as_principal_id");
        source.Should().Contain("NonStaticModule");
        source.Should().Contain("ExternallyExecutableModule");
        source.Should().Contain("ReachableModule");
        source.Should().Contain("FROM ReachableModule R");
        source.Should().Contain("sys.crypt_properties");
        source.Should().Contain("OPTION (MAXRECURSION 64)");
        source.Should().Contain("S.principal_id=DATABASE_PRINCIPAL_ID(N'dbo')");
        source.Should().Contain("COALESCE(O.principal_id, S.principal_id)=DATABASE_PRINCIPAL_ID(N'dbo')");
        source.Should().Contain("unexpected-executable:");
        source.Should().Contain("unexpected-reachable:");
        source.Should().Contain(
            "WHERE NOT EXISTS (SELECT 1 FROM @AllowedModules A WHERE A.MODULE_ID=R.MODULE_ID)");
        source.Should().Contain("JOIN sys.sql_modules RM ON RM.object_id=D.referencing_id");
        source.Should().Contain("Assert-AuthorityRoleMemberships");
        source.Should().Contain("Assert-DistinctPrincipalSids");
        source.Should().Contain("DISTINCT_SID_COUNT -ne 3");
        source.Should().Contain("Broad or unexpected EXECUTE/IMPERSONATE GRANT");
        source.Should().Contain("$driftIdentifiers -join ', '");
        source.Should().Contain("Nested roles are forbidden");
        source.Should().Contain("POM_PROJECTION_AUTHORITY_SCOPE_FENCE");
        source.Should().Contain("POM_GET_ACTIVE_PROJECTION_AUTHORITY_FOR_UPDATE");
        source.Should().Contain("POM_ADVANCE_WORK_SCOPE_PROJECTION_AUTHORITY_LINEAGE");
        source.Should().Contain("CoordinateSha256");
        source.Should().Contain("ReleaseProvenance");
        source.Should().Contain("PrincipalSidSha256 = $releaseSidDigest");
        source.Should().Contain("HistoricalApprovalRequired");
        source.Should().Contain("HistoricalApprovalMatched");
        source.Should().Contain(
            "A new binding for historical release provenance requires the exact ApprovedReleasePrincipalSidSha256.");
        source.Should().Contain("RemovedBindings");
        source.Should().Contain("Get-RuntimePrincipalBinding `");
        source.Should().Contain("$DecommissionAllBindings $true");
        source.Should().Contain("[DBNull]::Value");
        source.Should().Contain("[System.IO.FileMode]::CreateNew");
        source.Should().NotContain("File]::WriteAllText");
        source.Should().NotMatchRegex(@"(?i)\bCREATE\s+(?:LOGIN|USER)\b");
        source.Should().NotMatchRegex(@"(?i)\bPASSWORD\s*=");
        source.Should().NotMatchRegex(
            @"(?s)\[Parameter\(Mandatory\s*=\s*\$true\)\]\s*\[string\]\$RmsWriterDatabaseUser");
        source.Should().NotMatchRegex(
            @"(?s)\[Parameter\(Mandatory\s*=\s*\$true\)\]\s*\[string\]\$SysWriterDatabaseUser");

        var assertions = source.IndexOf("Assert-Matrix 'SYS writer'", StringComparison.Ordinal);
        var commit = source.IndexOf("$securityTransaction.Commit()", StringComparison.Ordinal);
        assertions.Should().BePositive().And.BeLessThan(commit,
            "membership, active binding, and effective permissions must be one atomic Apply");
        source.IndexOf("Set-RuntimeProductBinding $connection $securityTransaction $false", StringComparison.Ordinal)
            .Should().BeLessThan(commit);
        var releaseValidation = source.IndexOf(
            "$release = Get-ReleaseProvenanceForBinding $connection $securityTransaction",
            StringComparison.Ordinal);
        var bindingMutation = source.IndexOf(
            "Set-RuntimeProductBinding $connection $securityTransaction $false",
            StringComparison.Ordinal);
        releaseValidation.Should().BePositive().And.BeLessThan(bindingMutation);
        source.Should().Contain(
            "# No mode switch and explicit ValidateOnly are the same activation gate.");
        source.Should().Contain(
            "$securityTransaction = $connection.BeginTransaction([System.Data.IsolationLevel]::Serializable)");
        source.Should().Contain(
            "$release = Get-ReleaseProvenanceForBinding $connection $securityTransaction");

        var releaseFunctionStart = source.IndexOf(
            "function Get-ReleaseProvenanceForBinding", StringComparison.Ordinal);
        var bindingFunctionStart = source.IndexOf(
            "function Set-RuntimeProductBinding", releaseFunctionStart, StringComparison.Ordinal);
        var bindingFunctionEnd = source.IndexOf(
            "function Get-RuntimeProductBinding", bindingFunctionStart, StringComparison.Ordinal);
        var releaseFunction = source[releaseFunctionStart..bindingFunctionStart];
        var bindingFunction = source[bindingFunctionStart..bindingFunctionEnd];
        releaseFunction.Should().Contain("RELEASED_DATABASE_PRINCIPAL_NAME");
        releaseFunction.Should().Contain("RELEASED_DATABASE_PRINCIPAL_SID");
        releaseFunction.Should().Contain("CURRENT_WRITER_MATCH");
        releaseFunction.Should().Contain("EXACT_BINDING_EXISTS");
        releaseFunction.Should().Contain("SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION V WITH (UPDLOCK, HOLDLOCK)");
        bindingFunction.Should().NotContain("RELEASED_DATABASE_PRINCIPAL_NAME");
        bindingFunction.Should().NotContain("@sysWriterSid");

        var startInfo = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment["NEXA_TEST_PS_SCRIPT"] = CommissioningPath;
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
            ?? throw new InvalidOperationException("Unable to start pwsh for commissioning validation.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        var stdout = await output;
        var stderr = await error;
        var combined = stdout + Environment.NewLine + stderr;
        process.ExitCode.Should().Be(0, combined);
    }

    [Fact]
    public async Task Commissioning_refuses_to_overwrite_an_existing_evidence_file_before_DB_access()
    {
        var evidencePath = Path.Combine(
            Path.GetTempPath(), $"nexa-v160-evidence-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(evidencePath, "immutable-evidence");
        try
        {
            var startInfo = new ProcessStartInfo("pwsh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in new[]
                     {
                         "-NoLogo", "-NoProfile", "-NonInteractive", "-File", CommissioningPath,
                         "-ConnectionString", "Server=127.0.0.1;Database=must-not-connect;Integrated Security=true",
                         "-RuntimeDatabaseUser", "runtime_test", "-RmsWriterDatabaseUser", "rms_test",
                         "-SysWriterDatabaseUser", "sys_test", "-EquipmentId", "EQ", "-OperationKey", "OP",
                         "-ArtifactId", "ART", "-ProductProfileId", "PROFILE", "-PluginId", "PLUGIN",
                         "-ProductDefinitionVersion", "PRODUCT-V1", "-ProgramVersion", "PROGRAM-V1",
                         "-ProgramSchema", "program-v1", "-ProgramHash", new string('A', 64),
                         "-BoundRecipeSnapshotSchema", "recipe-v1",
                         "-BoundRecipeSnapshotHash", new string('B', 64),
                         "-EvidencePath", evidencePath,
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Unable to start pwsh for evidence validation.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var output = stdout + stderr;

            process.ExitCode.Should().NotBe(0, output);
            (await File.ReadAllTextAsync(evidencePath)).Should().Be("immutable-evidence");
        }
        finally
        {
            File.Delete(evidencePath);
        }
    }
}
