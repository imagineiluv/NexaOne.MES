using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace NexaOne.Common.Telemetry;

/// <summary>
/// §17.5 <c>nexames_active_users</c> — 최근 N분(기본 5분) 내 요청 또는 SignalR 연결이 있은 사용자를
/// Plant 단위로 집계한다. API 미들웨어와 Hub의 OnConnected가 <see cref="Touch"/>로 활동을 갱신하고,
/// OpenTelemetry ObservableGauge가 <see cref="Observe"/>로 윈도 내 고유 사용자 수를 수집한다.
/// </summary>
/// <remarks>
/// <c>uiId</c> 라벨은 화면 메타데이터가 아직 도입되지 않아 보류한다(§17.3 동일 관례). 윈도 밖 항목은
/// 수집 시점(<see cref="Observe"/>)에 정리한다. 시각은 테스트 가능하도록 <see cref="TimeProvider"/>로 주입한다.
/// </remarks>
public sealed class ActiveUserTracker
{
    private readonly TimeProvider _clock;
    private readonly long _windowTicks;

    // (plantId, userId) → 마지막 활동 UtcTicks
    private readonly ConcurrentDictionary<(string Plant, string User), long> _last = new();

    public ActiveUserTracker(TimeProvider? clock = null, TimeSpan? window = null)
    {
        _clock = clock ?? TimeProvider.System;
        _windowTicks = (window ?? TimeSpan.FromMinutes(5)).Ticks;
    }

    /// <summary>사용자 활동을 기록한다. 익명/빈 사용자는 무시한다.</summary>
    public void Touch(string? plantId, string? userId)
    {
        if (string.IsNullOrEmpty(userId) || userId == "anonymous") return;
        _last[(plantId ?? "GLOBAL", userId)] = _clock.GetUtcNow().UtcTicks;
    }

    /// <summary>Plant별 윈도 내 고유 활동 사용자 수 스냅샷. 윈도를 벗어난 항목은 정리한다.</summary>
    public IEnumerable<Measurement<long>> Observe()
    {
        var cutoff = _clock.GetUtcNow().UtcTicks - _windowTicks;
        var counts = new Dictionary<string, int>();
        var expired = new List<KeyValuePair<(string Plant, string User), long>>();

        // 1패스: 집계 + 만료 후보 수집(열거와 구조 변경을 분리)
        foreach (var entry in _last)
        {
            if (entry.Value < cutoff)
            {
                expired.Add(entry);
                continue;
            }
            counts[entry.Key.Plant] = counts.GetValueOrDefault(entry.Key.Plant) + 1;
        }

        // 2패스: 만료 항목 정리 — 값이 그대로일 때만 제거(정리 사이 재활동한 항목은 보존)
        foreach (var entry in expired)
            _last.TryRemove(entry);

        var result = new List<Measurement<long>>(counts.Count);
        foreach (var c in counts)
            result.Add(new Measurement<long>(c.Value, new KeyValuePair<string, object?>("plantId", c.Key)));
        return result;
    }
}
