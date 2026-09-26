namespace NexaOne.UnitTests.Architecture;

/// <summary>SYS boundary: COM_ID_RULE 채번은 SYS 소유 engine만 수행하고 다른 모듈은 COM 스키마를 직접 읽지 않는다.</summary>
public sealed class SysIdRuleBoundaryTests
{
    [Fact]
    public void Id_rule_engine_is_the_only_module_touching_com_id_rule()
    {
        var modulesRoot = Path.Combine(RepositorySource.Root, "src", "04.Modules");
        foreach (var file in Directory.GetFiles(modulesRoot, "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            if (!source.Contains("COM_ID_RULE", StringComparison.Ordinal))
                continue;
            file.Should().EndWith(
                Path.Combine("NexaOne.SYS", "Infrastructure", "IdRuleEngine.cs"),
                "COM_ID_RULE은 SYS의 IdRuleEngine만 접근해야 한다: " + file);
        }
    }

    [Fact]
    public void Id_rule_engine_claims_the_sequence_inside_one_transaction()
    {
        var source = File.ReadAllText(RepositorySource.GetFile(
            "src", "04.Modules", "NexaOne.SYS", "Infrastructure", "IdRuleEngine.cs"));

        source.Should().Contain("ExecuteInTransactionAsync",
            "SELECT→UPDATE를 한 트랜잭션으로 묶어야 동시 채번에서도 중복이 생기지 않는다");
        source.Should().Contain("COM_ID_RULE");
        source.Should().NotContain("INSERT INTO",
            "마스터 관리는 COM.IdRule 화면/쿼리 책임이며 engine은 기존 규칙 행만 갱신한다");
        source.Should().Contain("SEQ_PERIOD");
    }

    [Fact]
    public void Sys_module_exports_the_id_rule_engine_as_a_contract_product()
    {
        var module = File.ReadAllText(RepositorySource.GetFile(
            "src", "04.Modules", "NexaOne.SYS", "Module.cs"));

        module.Should().Contain("GetIdRuleEngine");
        module.Should().Contain("GetApprovalProcess");
        var sysXml = File.ReadAllText(RepositorySource.GetFile(
            "src", "00.Main", "NexaOne.Server", "config", "modules", "sys.xml"));
        sysXml.Should().Contain("idRuleEngine");
        sysXml.Should().Contain("approvalProcess");
    }

    [Fact]
    public void Approval_process_is_the_only_module_touching_com_approval_storage()
    {
        var modulesRoot = Path.Combine(RepositorySource.Root, "src", "04.Modules");
        foreach (var file in Directory.GetFiles(modulesRoot, "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            if (!source.Contains("COM_APPROVAL", StringComparison.Ordinal))
                continue;
            file.Should().EndWith(
                Path.Combine("NexaOne.SYS", "Infrastructure", "ApprovalProcess.cs"),
                "COM_APPROVAL*은 SYS의 ApprovalProcess만 접근해야 한다: " + file);
        }
    }

    [Fact]
    public void Approval_transitions_are_guarded_and_evidence_is_append_only()
    {
        var source = File.ReadAllText(RepositorySource.GetFile(
            "src", "04.Modules", "NexaOne.SYS", "Infrastructure", "ApprovalProcess.cs"));

        source.Should().Contain("ExecuteInTransactionAsync");
        source.Should().Contain("STATUS = 'Pending'",
            "결정·취소 UPDATE는 Pending 가드로 동시 전이를 한 명만 이기게 해야 한다");
        source.Should().Contain("IDEMPOTENCY_KEY");
        var ddl = File.ReadAllText(RepositorySource.GetFile(
            "src", "00.Main", "NexaOne.Server", "config", "db", "migrations",
            "V219__COM_APPROVAL.sql"));
        ddl.Should().Contain("append-only");
        ddl.Should().Contain("UNIQUE (DOC_KIND, DOC_ID)");
    }
}
