using System.Text.Json;
using NexaOne.Web.Services.Meta;

namespace NexaOne.UnitTests.Web;

/// <summary>TS↔C# 화면정의 계약 드리프트 가드 — 공유 픽스처(test/contract/screen-definition-contract.json)와
/// C# record 속성을 리플렉션으로 대조한다. SPA 쪽은 vitest(contract-drift.test.ts)가 같은 픽스처와
/// layout.ts SCREEN_DEFINITION_KEYS(타입 완전성 체크 포함)를 대조 — 어느 한쪽만 속성을 추가하면
/// 반대편 스위트가 실패한다(DeleteQueryId SPA 미러 누락 실사고 재발 방지).</summary>
public sealed class ScreenContractDriftTests
{
    private static JsonDocument LoadFixture()
    {
        // 리포 루트 탐색 — 테스트 실행 위치(bin) 무관하게 NexaOne.sln 기준으로 픽스처를 찾는다.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NexaOne.sln")))
            dir = dir.Parent;
        dir.Should().NotBeNull("리포 루트(NexaOne.sln)를 찾아야 픽스처를 읽을 수 있다");
        var path = Path.Combine(dir!.FullName, "test", "contract", "screen-definition-contract.json");
        File.Exists(path).Should().BeTrue($"공유 계약 픽스처가 있어야 한다: {path}");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static string[] FixtureNames(string key)
    {
        using var doc = LoadFixture();
        return doc.RootElement.GetProperty(key).EnumerateArray().Select(e => e.GetString()!).ToArray();
    }

    private static string[] RecordPropertyNames(Type t)
        => t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(n => n != "EqualityContract")
            .ToArray();

    [Fact]
    public void ScreenDefinition_properties_match_shared_contract_fixture()
    {
        var actual = RecordPropertyNames(typeof(ScreenDefinition));
        var expected = FixtureNames("screenDefinition");
        actual.Should().BeEquivalentTo(expected,
            "C# ScreenDefinition에 속성을 추가/삭제하면 공유 픽스처와 SPA 미러(layout.ts DTO+KEYS, mapping, api extras, ScreenEditor)를 함께 갱신해야 한다 — 픽스처의 _comment 절차 참조");
    }

    [Fact]
    public void BulkCommandDefinition_properties_match_shared_contract_fixture()
    {
        var actual = RecordPropertyNames(typeof(BulkCommandDefinition));
        var expected = FixtureNames("bulkCommandDefinition");
        actual.Should().BeEquivalentTo(expected,
            "BulkCommandDefinition 변경 시 SPA layout.ts BulkCommandDefinition 인터페이스와 픽스처를 함께 갱신해야 한다");
    }
}
