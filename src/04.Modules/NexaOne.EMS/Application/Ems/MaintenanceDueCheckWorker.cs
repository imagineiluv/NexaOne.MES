using Microsoft.Extensions.Hosting;
using NexaOne.Infrastructure.Messaging;
using NexusFramework.Scheduling;

namespace NexaOne.EMS.Application.Ems;

/// <summary>
/// 예방정비 도래 점검 워커 — 모듈 소유 주기 작업(BackgroundService). 실 반복 스케줄러
/// (<see cref="IRecurringScheduler"/>, server.xml의 quartzScheduler 빈)로 구동되며, 예정일이 도래한
/// (SCHEDULED_DATE &lt;= now, 완료/취소 제외) 정비 계획을 주기적으로 조회해 각 건을 <see cref="IMessageBus"/>로
/// "MaintenanceDue" 도메인 이벤트로 발행한다.
/// </summary>
/// <remarks>
/// 레퍼런스 패턴: ScheduledOutboxDispatchWorker(스케줄러 사용법·enabled 게이트) + FdcCollectionWorker
/// (모듈 소유 BackgroundService·messageBus 발행·웹 타입 미참조·enabled=false 즉시 no-op).
/// 게이트(<c>_enabled</c>) 기본 OFF. 중복 통지 방지는 두지 않는다(설계: 단순히 도래분 발행 — 과도구현 회피).
/// quartzScheduler/messageBus는 부모(server.xml) 컨텍스트 빈을 cross-context ref로 주입받는다.
/// </remarks>
public sealed class MaintenanceDueCheckWorker : BackgroundService
{
    private readonly IRecurringScheduler _scheduler;
    private readonly IMaintenancePlanRepository _planRepo;
    private readonly IMessageBus _bus;
    private readonly bool _enabled;
    private readonly int _intervalSeconds;
    private readonly string _topic;

    public MaintenanceDueCheckWorker(
        IRecurringScheduler scheduler,
        IMaintenancePlanRepository planRepo,
        IMessageBus bus,
        bool enabled,
        int intervalSeconds = 3600,
        string topic = "nexaone.events")
    {
        _scheduler = scheduler;
        _planRepo = planRepo;
        _bus = bus;
        _enabled = enabled;
        _intervalSeconds = intervalSeconds;
        _topic = topic;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            Console.WriteLine("[MaintenanceDueCheckWorker] disabled (enabled=false). Skipping startup.");
            return;
        }

        Console.WriteLine(
            $"[MaintenanceDueCheckWorker] started (topic={_topic}, interval={_intervalSeconds}s).");

        await _scheduler.StartAsync(stoppingToken);
        await _scheduler.ScheduleRecurringAsync(
            "ems-maintenance-due-check",
            TimeSpan.FromSeconds(_intervalSeconds),
            CheckDueAsync,
            stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _scheduler.StopAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    /// <summary>도래한 정비 계획을 조회해 각 건을 버스로 발행한다. 기준시각(now)은 C#에서 산정해 리포에 넘긴다
    /// (MSSQL/SQLite 날짜 방언 분기 회피). 발행 실패는 잡아 삼켜 다음 건·다음 주기를 막지 않는다(스케줄러 지속).</summary>
    private async Task CheckDueAsync(CancellationToken ct)
    {
        var due = await _planRepo.GetDueAsync(DateTime.UtcNow, ct);

        foreach (var plan in due)
        {
            try
            {
                // aggregateId=PlanId(계획 식별자). payload는 도래 설비 식별자(EquipmentId) — 구독자가 통지 라우팅에 사용.
                var message = DomainEventMessage.Create(
                    "MaintenanceDue", module: "EMS", aggregateId: plan.Id, payload: plan.EquipmentId);
                await _bus.PublishAsync(_topic, message, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch
            {
                // 1건 발행 실패는 다른 계획 통지·다음 주기를 막지 않는다(버스 미가용·구독자 예외 등).
            }
        }
    }
}
