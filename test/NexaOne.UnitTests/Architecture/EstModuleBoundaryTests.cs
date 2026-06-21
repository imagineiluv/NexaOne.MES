namespace NexaOne.UnitTests.Architecture;

/// <summary>ADR-006 모듈 경계 회귀 가드 — EST 인프라(EquipmentAlarmRepository)가 타 모듈(MDM)의 물리 테이블을
/// 자신의 SQL에 다시 박지 못하게 한다. 과거 GetActiveAlarmsAsync가 EST_EQUIPMENT_ALARM ⋈ MDM_EQUIPMENT 조인으로
/// plantId를 풀었으나(EST.csproj는 MDM 미참조 → 런타임 무음 결합), 이제 호스트 IEquipmentDirectory가 plantId→설비
/// ID를 풀어 넘긴다. 이 테스트는 그 결합이 소스에 다시 들어오면 즉시 빨갛게 만든다(소스 텍스트에 "MDM_" 부재 단언).</summary>
public sealed class EstModuleBoundaryTests
{
    [Fact]
    public void EquipmentAlarmRepository_source_contains_no_foreign_MDM_table()
    {
        var path = ResolveRepoFile(
            "src", "04.Modules", "NexaOne.EST", "Infrastructure", "EquipmentAlarmRepository.cs");
        var source = File.ReadAllText(path);

        source.Should().NotContain("MDM_",
            "ADR-006: EST 인프라는 MDM 물리 스키마를 자신의 SQL에 박지 않는다 — plantId→설비 ID는 호스트 "
            + "IEquipmentDirectory가 푼다. \"MDM_\" 재등장은 교차 모듈 무음 결합 회귀를 뜻한다.");
    }

    // ResolveHostBinDir(ServerTests)와 동일하게 AppContext.BaseDirectory에서 위로 올라가 리포 루트(NexaOne.sln)를 찾는다.
    private static string ResolveRepoFile(params string[] relativeSegments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NexaOne.sln")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException("리포 루트(NexaOne.sln) 미발견 — 소스 파일 해석 실패.");

        var path = Path.Combine(new[] { dir.FullName }.Concat(relativeSegments).ToArray());
        File.Exists(path).Should().BeTrue($"검사 대상 소스가 존재해야 한다: {path}");
        return path;
    }
}
