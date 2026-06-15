# ADR-002 — Event Bus (모든 이벤트가 단일 백본을 통과)

- **Status**: Accepted (채택)
- **Date**: 2026-06-13 (구현현황 갱신 2026-06-15)
- **구현현황**: 구현 완료(opt-in 기본 활성) — outbox+`IMessageBus`(인메모리/Kafka)+디스패처+`RealtimeNotificationCoordinator`로 실시간 알림 버스 일원화, 13개 lifecycle 애그리거트로 확산(GapAnalysis §7 Phase 1 참조). 3체계(NexusFramework/NexusCom) 어댑팅은 잔여.
- **관련**: [Frontend-Coexistence-GapAnalysis.md](../Frontend-Coexistence-GapAnalysis.md) §2.5, Phase 1C
- **결정자**: 사용자 승인

## 컨텍스트

비전은 "모든 이벤트가 Event Bus(Kafka)를 통과"한다. 현재:
- NexusCom `KafkaDriver`(성숙) + NexaMes `KafkaMessageBus`/`KafkaConsumerService`(설계 성숙)가 있으나 **DI/HostedService 미등록 = 죽은 코드**.
- 실시간 알림은 컨트롤러/HostedService가 `IEesHubNotifier`를 **직접 호출**해 SignalR로 즉시 푸시 — 버스 우회.
- `AggregateRoot.RaiseDomainEvent`/`IDomainEvent`는 **골격만**(구현·발행·소비 0건).
- 3중 단절 이벤트 체계(NexaMes `IDomainEvent` / NexusFramework `IExecutionEvent` / NexusCom `ChangeEvent`).

### 설계 분기
NexaMes는 **EF Core가 아니라 Dapper** — "SaveChanges 인터셉터 outbox"가 그대로 적용되지 않는다. 그러나 쓰기 경로 `ServiceObjectProcessor`는 이미 `ITransactionManager.ExecuteInTransactionAsync`로 **트랜잭션을 감싼다.** 이 지점이 outbox 기록의 자연스러운 자리다.

## 결정

**Transactional Outbox 패턴을 채택한다.** 데이터 변경과 도메인 이벤트 기록을 **동일 DB 트랜잭션**에 묶고, 백그라운드 디스패처가 outbox를 폴링해 Kafka로 발행하며, **SignalR은 버스의 구독자**가 된다.

흐름:
```
도메인(AggregateRoot.RaiseDomainEvent) → ServiceObjectProcessor가 같은 트랜잭션에
  EES_OUTBOX 행 기록(원자성) → OutboxDispatcher(BackgroundService)가 미발행 행을
  KafkaMessageBus로 발행 → KafkaConsumerService가 구독 → IEesHubNotifier(SignalR 푸시)
```

## 접근(구현 범위)

- 신규 마이그레이션: `EES_OUTBOX`(Id, EventType, AggregateId, Module, Payload, OccurredAt, PublishedAt NULL, Attempts).
- 신규: `IOutboxWriter`(트랜잭션 내 기록) — `ServiceObjectProcessor`가 처리 후 `AggregateRoot.DomainEvents`를 같은 커넥션/트랜잭션으로 기록·`ClearDomainEvents`. `OutboxDispatcherService`(BackgroundService, opt-in `Events:Outbox:Enabled`) → `KafkaMessageBus`.
- Kafka 글루 DI/HostedService 등록(opt-in `Kafka:Enabled` — 브로커 없는 dev/CI 보호, FDC 컬렉터와 동일 패턴).
- outbox 테이블 + IOutboxWriter + 디스패처 + 도메인 이벤트(설비 상태 변경)를 outbox→발행으로 연결 + 단위 테스트. *(구현현황: 완료 후 전 lifecycle 확산 — 13개 상태전이 애그리거트(EST/POM/QMS/MDM/CMMS/RMS/SHP/SYS/FDC)에 적용. 컨트롤러 직접 SignalR 호출은 `RealtimeNotificationCoordinator`로 버스 활성 시 생략·비활성 시 폴백.)*
- `DomainEventMessage`를 공통 봉투로 채택. *(구현현황: NexaMes 내부 수렴 완료, NexusFramework `IExecutionEvent`/NexusCom `ChangeEvent` 3체계 어댑팅은 잔여.)*

## 결과

- **장점**: DB 커밋과 이벤트 발행의 **원자성**(부분 발행 제거), 단일 백본, "UI 갱신 = 버스 소비" 구조, opt-in으로 무중단.
- **비용/위험**: outbox 폴링 지연(수초), 디스패처 운영 필요. Kafka 미가동 dev에서는 opt-in off(이벤트는 outbox에 쌓이되 미발행).
- **비채택**: 직접 발행(원자성 없음), EF SaveChanges 인터셉터(Dapper 부적합).
