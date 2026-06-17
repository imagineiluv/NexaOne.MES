using System.Diagnostics.Metrics;

namespace NexaOne.Common.Telemetry;

/// <summary>
/// §17.5 비즈니스 Metrics — 단일 <c>"NexaMes"</c> <see cref="Meter"/>로 커스텀 계측을 노출한다.
/// OpenTelemetry는 <c>AddMeter(<see cref="MeterName"/>)</c>로 이 Meter를 구독하고, 운영은 OTLP Collector를
/// 통해 Prometheus/Azure Monitor 등 표준 형식으로 변환한다(§17.4 exporter 전략과 일치).
/// </summary>
/// <remarks>
/// 본 절은 §17.3 로그 적응과 같은 관례로 적응한다. 즉 해당 개념이 아직 도입되지 않은 지표
/// (<c>nexames_rule_duration_ms</c>의 RuleId/MenuId, <c>nexames_query_duration_ms</c>의 QueryId,
/// WinForms 클라이언트가 없는 <c>nexames_client_deploy_version</c>)는 해당 개념 도입 시점으로 보류한다.
/// 캐시 계층(<c>nexames_cache_requests_total</c>)은 FDC/MDM 캐시 도입으로 소비처가 생겨 활성화했다.
/// </remarks>
public static class NexaMesMetrics
{
    /// <summary>OpenTelemetry가 구독하는 Meter 이름.</summary>
    public const string MeterName = "NexaMes";

    private static readonly Meter Meter = new(MeterName, "2.0");

    /// <summary>nexames_api_request_duration_ms — API 응답시간(ms). Tags: route, method, plantId, resultCode.</summary>
    public static readonly Histogram<double> ApiRequestDuration =
        Meter.CreateHistogram<double>("nexames_api_request_duration_ms", unit: "ms",
            description: "API 요청 처리 시간(ms)");

    /// <summary>nexames_api_error_rate — 오류 Count. 오류율은 본 카운터 ÷ 요청 수로 산정한다.
    /// Tags: route, errorCode, plantId.</summary>
    public static readonly Counter<long> ApiErrors =
        Meter.CreateCounter<long>("nexames_api_error_rate", unit: "{error}",
            description: "API 오류 건수(상태코드 ≥ 400)");

    /// <summary>nexames_fdc_collection_rate 적응 — FDC 수집 적재 건수. Tags: equipmentId, quality.
    /// 기대 대비 수집률은 본 카운터와 엔드포인트 SamplingIntervalMs로 대시보드에서 산정한다.
    /// (plantId는 FDC 수집 컨텍스트에 없어 보류 — §17.3 동일 관례.)</summary>
    public static readonly Counter<long> FdcCollected =
        Meter.CreateCounter<long>("nexames_fdc_collected_total", unit: "{point}",
            description: "FDC 수집 데이터 적재 건수");

    /// <summary>nexames_outbox_published_total — outbox 이벤트 발행 성공 건수(ADR-002). Tags: eventType.</summary>
    public static readonly Counter<long> OutboxPublished =
        Meter.CreateCounter<long>("nexames_outbox_published_total", unit: "{event}",
            description: "Outbox 이벤트 발행 성공 건수");

    /// <summary>nexames_outbox_deadlettered_total — outbox 데드레터 전이 건수(ADR-002). Tags: eventType.</summary>
    public static readonly Counter<long> OutboxDeadLettered =
        Meter.CreateCounter<long>("nexames_outbox_deadlettered_total", unit: "{event}",
            description: "Outbox 메시지 데드레터 전이 건수");

    /// <summary>nexames_cache_requests_total — 캐시 조회 건수. Tags: result(hit|miss), keyspace(키 앞 2세그먼트).
    /// 적중률은 hit ÷ (hit+miss)로 산정한다. ICacheService 구현(MemoryCacheService/RedisCacheService)이 발행한다.</summary>
    public static readonly Counter<long> CacheRequests =
        Meter.CreateCounter<long>("nexames_cache_requests_total", unit: "{request}",
            description: "캐시 조회 건수(result=hit|miss, keyspace 태그로 캐시별 적중률 산정)");

    /// <summary>캐시 적중 1건을 계측한다(ICacheService 구현이 호출).</summary>
    public static void RecordCacheHit(string key) =>
        CacheRequests.Add(1,
            new KeyValuePair<string, object?>("result", "hit"),
            new KeyValuePair<string, object?>("keyspace", KeySpace(key)));

    /// <summary>캐시 미스 1건을 계측한다(ICacheService 구현이 호출).</summary>
    public static void RecordCacheMiss(string key) =>
        CacheRequests.Add(1,
            new KeyValuePair<string, object?>("result", "miss"),
            new KeyValuePair<string, object?>("keyspace", KeySpace(key)));

    // 키의 앞 2개 ':' 세그먼트를 keyspace 태그로 쓴다(예 "fdc:param", "fdc:alarm", "mdm:codes") —
    // 캐시 종류별 적중률을 보면서도 키 값(설비/파라미터 id 등)에 의한 카디널리티 폭주를 막는다.
    private static string KeySpace(string key)
    {
        int first = key.IndexOf(':');
        if (first < 0) return key;
        int second = key.IndexOf(':', first + 1);
        return second < 0 ? key : key[..second];
    }

    private static readonly object BindLock = new();
    private static bool _bound;
    private static bool _outboxBound;

    /// <summary>
    /// ObservableGauge(<c>nexames_active_users</c>, <c>nexames_equipment_channel_status</c>)를
    /// 트래커 싱글톤에 1회만 바인딩한다. 앱 시작 시 호출하며, 중복 호출(다중 호스트 빌드)은 무시한다.
    /// </summary>
    public static void BindObservableGauges(ActiveUserTracker activeUsers, EquipmentChannelStatusRegistry channels)
    {
        ArgumentNullException.ThrowIfNull(activeUsers);
        ArgumentNullException.ThrowIfNull(channels);

        lock (BindLock)
        {
            if (_bound) return;

            // nexames_active_users — 최근 5분 내 활동 사용자 수. Tags: plantId (uiId 보류).
            Meter.CreateObservableGauge("nexames_active_users", activeUsers.Observe,
                unit: "{user}", description: "최근 5분 내 요청/연결이 있은 사용자 수");

            // nexames_equipment_channel_status — 설비 채널 연결 상태(정상 1/비정상 0).
            // Tags: equipmentId, channelType, plantId.
            Meter.CreateObservableGauge("nexames_equipment_channel_status", channels.Observe,
                description: "설비 채널 연결 상태(정상 1/비정상 0)");

            _bound = true;
        }
    }

    /// <summary>
    /// Outbox ObservableGauge(<c>nexames_outbox_pending</c>, <c>nexames_outbox_deadlettered</c>)를 1회만
    /// 바인딩한다(ADR-002). 값 제공자(<paramref name="pendingCount"/>/<paramref name="deadLetteredCount"/>)는
    /// 호출부가 IServiceScopeFactory로 스코프드 IOutboxRepository를 해석해 CountPending/CountDeadLettered를
    /// 호출하도록 구성한다(레이어 분리 — Common은 Infrastructure를 참조하지 않음). outbox 비활성 시 본 메서드는
    /// 호출되지 않으므로 게이지도 노출되지 않는다.
    /// </summary>
    public static void BindOutboxGauges(Func<int> pendingCount, Func<int> deadLetteredCount)
    {
        ArgumentNullException.ThrowIfNull(pendingCount);
        ArgumentNullException.ThrowIfNull(deadLetteredCount);

        lock (BindLock)
        {
            if (_outboxBound) return;

            // nexames_outbox_pending — 발행 대기(미발행·비데드레터) 건수.
            Meter.CreateObservableGauge("nexames_outbox_pending",
                () => new Measurement<long>(pendingCount()),
                unit: "{event}", description: "Outbox 발행 대기 건수");

            // nexames_outbox_deadlettered — 데드레터(수동 조치 대기) 건수.
            Meter.CreateObservableGauge("nexames_outbox_deadlettered",
                () => new Measurement<long>(deadLetteredCount()),
                unit: "{event}", description: "Outbox 데드레터 건수");

            _outboxBound = true;
        }
    }
}
