# ADR-007 — 주기 스케줄러(Quartz) + 모듈 소유 스케줄 워커

- **Status**: Accepted (채택 — 구현 완료, 게이트 OFF 기본)
- **Date**: 2026-06-17
- **구현현황**: NexusFramework 실 Quartz 스케줄러 + 스케줄러 구동 워커 4종 구현. 전부 게이트 기본 OFF(추가형, 회귀 0).
  SQLite로 스케줄러 반복 발화·워커 자동발견·신규 SQL 무오류 실행 검증. 신규 리포 메서드 통합 테스트 3종 추가(통합 263).
- **관련**: [ADR-002](ADR-002-event-bus.md)(Outbox), [ADR-005](ADR-005-server-service-container.md)(서비스 빈 컨테이너), [ADR-006](ADR-006-web-worker-separation.md)(웹/워커 분리), 설계문서 §8·§10.4
- **결정자**: 사용자 승인

## 컨텍스트

주기 실행(Outbox 폴링, 데이터 보존정리, 예방정비 도래 점검 등)이 필요하나, 기존엔 각 워커가 `BackgroundService` + `Task.Delay` 루프로 제각각 구현했다. NexusFramework에는 **실 스케줄러가 없었다** — `QuartzExecutor`는 이름만 Quartz인 placeholder(IExecutionExecutor, cron/트리거 없음)였다. 재사용 가능한 시간 기반 스케줄러가 필요했다.

이점: **Quartz.NET 기본 RAMJobStore는 외부 인프라 없이 in-process로 동작**해, Kafka/OPC-UA와 달리 이 환경에서 런타임 검증이 된다.

## 결정

**(1) NexusFramework에 실 Quartz 반복 스케줄러를 둔다.** `IRecurringScheduler`(StartAsync / ScheduleRecurringAsync(간격) / ScheduleRecurringCronAsync(cron) / StopAsync) + `QuartzScheduler` 구현(Quartz.NET StdSchedulerFactory 기본 IScheduler, RAMJobStore). 델리게이트 잡은 `DelegateJob` + 인스턴스 레지스트리 + 커스텀 `IJobFactory`로 JobKey 이름 조회·실행한다. 무인자 ctor(Spring 등 DI 컨테이너 zero-arg). 잡 예외는 삼켜 트리거가 지속된다. `quartzScheduler`를 server.xml의 **공통 서버 빈**으로 둔다(opcUaDriver/messageBus와 동일 위상).

**(2) 모듈 소유 스케줄 워커 패턴.** 주기 작업이 필요한 모듈은 자기 `BackgroundService` 워커를 소유하고, `quartzScheduler`(부모 server.xml 빈)와 자기 리포지토리를 cross-context `ref`로 주입받아 `ScheduleRecurringAsync(간격, 델리게이트)`로 주기 작업을 등록한다. **게이트**(enabled 생성자 인자, 모듈 xml에서 제어, 기본 false)로 켜고 끈다. `Program.cs`가 부모·자식 컨텍스트에서 `IHostedService`를 자동발견해 Generic Host에 등록한다(인스턴스 참조 기준 Distinct로 상속 빈 중복 제거).

**(3) 적용한 스케줄 워커 4종(전부 게이트 OFF 기본).**
- **ScheduledOutboxDispatchWorker**(NexaOne.Server/인프라) — outbox 미발행분을 주기 폴링→messageBus 발행→표시. ADR-006 Phase 3(Outbox 디스패처를 Server로)을 스케줄러로 실현.
- **MaintenanceDueCheckWorker**(CMMS) — `MaintenancePlanRepository.GetDueAsync(asOf)`로 SCHEDULED_DATE 도래 + 미완료 계획 조회 → `MaintenanceDue` 이벤트 발행(예방정비).
- **FdcCollectDataRetentionWorker**(FDC) — `FdcCollectDataRepository.DeleteOlderThanAsync(cutoff)`로 COLLECTED_AT 오래된 수집데이터 정리(시계열 적체 방지).
- **LoginFailureRetentionWorker**(SYS) — `LoginFailureHistoryRepository.DeleteOlderThanAsync(cutoff)`로 OCCURRED_AT 오래된 로그인실패이력 정리.

**(4) 적용 원칙 — 죽은 설정 회피.** 주기 작업이 **표준 MES 관행이고 실재 테이블/컬럼으로 근거되는** 모듈에만 워커를 둔다. RMS/MDM/QMS/POM/SHP/EST는 현재 명확한 주기 작업이 없어 적용하지 않았다(필요해지면 동일 패턴으로 추가). 기준시각(asOf/cutoff)은 C#에서 산정해 파라미터로 전달한다(MSSQL/SQLite 방언 분기 회피).

## 결과

- **장점**: 재사용 가능한 시간 기반 스케줄러 확보, 주기 작업이 모듈 기능에 응집, 게이트로 무중단 도입, RAMJobStore라 외부 인프라 불필요(검증 용이).
- **비용/위험**: Quartz가 NexusFramework 의존에 추가됨(전이 참조). 모듈 워커가 messageBus로 발행한 이벤트가 다중 프로세스에서 웹 SignalR에 닿으려면 Kafka 백본 필요(ADR-006). 비수집형 ALC라 워커가 plugin ALC에서 조립되고 Program.cs는 IHostedService(Default ALC)로만 접촉.
- **비채택**: `QuartzExecutor` placeholder를 그대로 스케줄러로 오용(실제 스케줄링 불가), 모듈별 Task.Delay 루프 난립(재사용·일관성 부족), 모든 모듈에 일괄 워커(죽은 설정).

## 검증

- NexusFramework 단위 테스트: 간격 반복 발화(fireCount ≥ 2).
- SQLite 부팅: 워커 5개 자동발견(중복 제거 후), 게이트 동작(disabled skip), 활성화 시 CMMS GetDueAsync(SELECT)·SYS/FDC DeleteOlderThanAsync(DELETE)가 SQLite에서 무오류 실행("purged 0 row(s)" 반복 발화).
- 통합 테스트 3종(GetDueAsync·DeleteOlderThanAsync×2) 추가 — 시드→호출→단언. 전체 그린(단위 1067, 통합 263/1스킵).
