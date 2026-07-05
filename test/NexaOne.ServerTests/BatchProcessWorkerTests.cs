using FluentAssertions;
using NexaOne.Server.Gateway;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>배치 재조정 워커의 순수 부분(BuildDesired) — 정의 목록을 Quartz 잡 시그니처 집합으로
/// 변환하는 유형 판별·Interval 초 검증·Quartz cron 사전 검증을 격리 검증한다(스케줄러/DB 무관).</summary>
public sealed class BatchProcessWorkerTests
{
    private static Dictionary<string, object?> Def(string id, string type, string options) => new()
    {
        ["BATCH_ID"] = id,
        ["BATCH_TYPE"] = type,
        ["BATCH_OPTIONS"] = options,
    };

    [Fact]
    public void BuildDesired_maps_interval_and_cron_to_signatures()
    {
        var defs = new List<Dictionary<string, object?>>
        {
            Def("A", "Interval", "86400"),        // 일 1회
            Def("B", "Cron", "0 0 2 * * ?"),      // 매일 02:00(Quartz 네이티브)
        };

        var desired = BatchProcessWorker.BuildDesired(defs, out var unsupported);

        desired.Should().HaveCount(2);
        desired["A"].Should().Be("I:86400", "Interval은 'I:{초}' 시그니처");
        desired["B"].Should().Be("C:0 0 2 * * ?", "Cron은 'C:{식}' 시그니처");
        unsupported.Should().BeEmpty();
    }

    [Fact]
    public void BuildDesired_rejects_bad_interval_bad_cron_and_unknown_type()
    {
        var defs = new List<Dictionary<string, object?>>
        {
            Def("BadInt", "Interval", "0"),           // 0초 — 무효
            Def("NonNum", "Interval", "abc"),         // 비숫자
            Def("BadCron", "Cron", "0 0 2 * * *"),    // dom·dow 양쪽 지정 — Quartz 거부
            Def("Manual", "OnDemand", ""),            // 미지원 유형
            Def("", "Interval", "60"),                // BATCH_ID 없음 — 건너뜀
        };

        var desired = BatchProcessWorker.BuildDesired(defs, out var unsupported);

        desired.Should().BeEmpty("유효 스케줄이 없어야 한다");
        unsupported.Select(u => u.BatchId).Should().BeEquivalentTo(new[] { "BadInt", "NonNum", "BadCron", "Manual" },
            "BATCH_ID 없는 정의는 미지원 목록에도 넣지 않고 조용히 건너뛴다");
    }
}
