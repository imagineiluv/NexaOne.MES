namespace NexaOne.UnitTests.Architecture;

/// <summary>ADR-006 모듈 경계 회귀 가드 — EST 인프라(EquipmentAlarmRepository)가 타 모듈(MDM)의 물리 테이블을
/// 자신의 SQL에 다시 박지 못하게 한다. 과거 GetActiveAlarmsAsync가 EST_EQUIPMENT_ALARM ⋈ MDM_EQUIPMENT 조인으로
/// plantId를 풀었으나(EST.csproj는 MDM 미참조 → 런타임 무음 결합), 이제 MDM IEquipmentDirectory가 plantId→설비
/// ID를 풀어 넘긴다. 이 테스트는 그 결합이 소스에 다시 들어오면 즉시 빨갛게 만든다(소스 텍스트에 "MDM_" 부재 단언).</summary>
public sealed class EstModuleBoundaryTests
{
    [Fact]
    public void EquipmentAlarmRepository_source_contains_no_foreign_MDM_table()
    {
        var path = RepositorySource.GetFile(
            "src", "04.Modules", "NexaOne.EST", "Infrastructure", "EquipmentAlarmRepository.cs");
        var source = File.ReadAllText(path);

        source.Should().NotContain("MDM_",
            "ADR-006: EST 인프라는 MDM 물리 스키마를 자신의 SQL에 박지 않는다 — plantId→설비 ID는 호스트 "
            + "IEquipmentDirectory가 푼다. \"MDM_\" 재등장은 교차 모듈 무음 결합 회귀를 뜻한다.");
    }

    [Fact]
    public void Est_module_sources_contain_no_foreign_MDM_or_POM_physical_tables()
    {
        var estRoot = RepositorySource.GetDirectory("src", "04.Modules", "NexaOne.EST");
        var violations = Directory.GetFiles(estRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file =>
            {
                var source = File.ReadAllText(file);
                return source.Contains("MDM_", StringComparison.OrdinalIgnoreCase)
                       || source.Contains("POM_", StringComparison.OrdinalIgnoreCase);
            })
            .Select(file => Path.GetRelativePath(estRoot, file))
            .OrderBy(static file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        violations.Should().BeEmpty(
            "EST는 자기 스키마와 Common IOeeEvidenceSource seam만 소유해야 하며, "
            + "MDM/POM 물리 테이블은 호스트 orchestration adapter 뒤에 있어야 합니다");
    }

}
