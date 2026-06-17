# ADR-006 — 웹/워커 분리: API는 웹 전용, 비웹 워커는 모듈 소유 + NexaOne.Server 호스팅

- **Status**: Accepted (채택 — Phase 1~3 구현 완료; 다중 프로세스 cross-process 통지만 Kafka 환경 검증 잔여)
- **Date**: 2026-06-16 (구현현황 갱신 2026-06-17)
- **관련**: [ADR-002](ADR-002-event-bus.md)(Event Bus/Outbox), [ADR-005](ADR-005-server-service-container.md)(Server=서비스 빈 컨테이너), [ADR-007](ADR-007-recurring-scheduler.md)(주기 스케줄러·스케줄 워커), 설계문서 §6.1·§8·§10.4
- **결정자**: 사용자 승인

## 컨텍스트

`NexaOne.API`는 웹 통신(REST·SignalR·JWT) 외에 비웹 워크로드도 호스팅한다(Program.cs 확인): 워크플로 엔진(§8), FDC 실시간 수집(`FdcCollectorHostedService`, §10.4), 이벤트 버스/Outbox 워커(`OutboxDispatcherService` 등, ADR-002). 이들이 API에 모인 이유는 BackgroundService를 돌릴 .NET Generic Host가 API뿐이고, 다수가 SignalR로 웹에 실시간 갱신을 푸시하는 게 목적이라 웹에 인접하기 때문이다.

사용자 지시: **API는 웹 전용으로, 비웹 워커는 분리한다.** 추가 논의로 **"워커는 각 모듈이 소유"**가 합의됐다.

## 핵심 제약 (분리 방식을 좌우 — 검증됨)

비웹 워커가 웹에 결과를 전달하는 경로가 **인메모리 버스 + SignalR 허브(둘 다 API 프로세스 내)**다. 워커를 별도 프로세스로 분리하면 `이벤트 → SignalR`가 프로세스 경계를 넘어야 하므로 **cross-process 전송이 필수**다(인메모리 버스는 단일 프로세스 전용). → **Kafka 백본**(이미 opt-in 코드 존재, `Kafka:Enabled`)으로 워커 호스트가 발행하고 API가 구독→SignalR. 이 저장소 환경엔 Kafka가 없어 다중 프로세스 런타임은 검증 불가(사용자 환경 필요).

## 결정

**(1) 워커는 각 모듈이 코드 소유, NexaOne.Server가 실행.** 모듈(클래스 라이브러리)은 스스로 못 돌므로 "모듈이 워커를 *정의·소유* + 호스트가 *발견·실행*"한다. 호스트는 새 Worker 프로젝트가 아니라 **NexaOne.Server**(이미 비웹 호스트, ADR-005)에 .NET Generic Host를 더해 맡는다(역할: 컨테이너 + 워커 호스트). **신규 `NexaOne.Worker` 프로젝트는 비채택**(모듈이 소유하므로 불필요, 프로젝트 증가 회피).

**(2) 모듈 워커는 도메인 이벤트만 발생(웹 미의존).** SignalR(`IEesHubNotifier`)를 직접 호출하지 않고 도메인 이벤트를 발생시킨다(`FdcCollectorService`는 이미 `InterlockTriggered`/`AlarmRaised`를 발생). 웹 호스트(API)가 버스를 구독해 SignalR로 변환한다(ADR-002 패턴). 이로써 모듈이 웹에 의존하지 않는다.

**(3) 인프라 워커(Outbox 디스패처)는 Infrastructure 소유, Server가 실행.** 특정 모듈이 아닌 공유 `EES_OUTBOX` 관심사이므로 모듈이 아니라 인프라가 가진다.

**(4) cross-process 전송 = Kafka 백본 — 단, 메시징도 "서버 빈"으로 둔다.** `messageBus`(IMessageBus)를 드라이버(dbProvider/opcUaDriver)와 동일하게 **server.xml의 전환형 빈**으로 두고(InMemory ↔ Kafka, dbProvider와 같은 1파일 전환), 워커·Outbox 디스패처·구독자가 `GetBean`으로 당겨 쓴다. 메시징 타입에 로거-옵셔널/무인자 ctor를 추가해 Spring이 직접 생성하게 한다(SqliteProvider/OpcUaDriver와 동일 패턴). 워커 호스트(Server)가 발행, API가 구독→SignalR. 주의: 빈 등록은 배선을 통일할 뿐, 다중 프로세스 실제 전달은 **실행 중인 Kafka 브로커(외부 인프라)**가 필요하다(InMemory 빈은 단일 프로세스 전용).

**(5) API는 웹 전용으로 축소.** 워커를 Server로 이전한 뒤 API에서는 비활성하고, 버스 구독자→SignalR만 유지.

## 단계별 구현 (각 단계 비파괴·검증)

- **Phase 1 (완료)**: NexaOne.Server에 .NET Generic Host 추가(Spring 빈 컨테이너·SQLite 스키마 부트스트랩·AddService 보존). 워커 호스팅 토대.
- **Phase 2 (완료)**: FDC 수집 오케스트레이션을 FDC 모듈 소유 워커(`FdcCollectionWorker`)로 이전 + SignalR 직접 호출 대신 messageBus로 도메인 이벤트 발행. Server가 게이트(기본 OFF)로 호스팅. SQLite 부팅(게이트 ON 빈 엔드포인트 우아한 no-op)·전체 스위트로 검증. 실 OPC-UA 연결 + cross-process SignalR은 Kafka/OPC-UA 환경 검증 잔여.
- **Phase 3 (완료 — [ADR-007])**: Outbox 디스패처를 Server로 이전, 실 Quartz 스케줄러(`ScheduledOutboxDispatchWorker`)로 주기 구동. 게이트 기본 OFF(API 디스패처와 동시가동 회피). 다중 프로세스 실제 전달은 Kafka 환경 검증 잔여.
- **Phase 4 (보류)**: 워크플로 엔진 — `WorkflowController`(웹)가 직접 호출하므로 분리 시 웹→엔진도 cross-process가 돼 비용이 큼. 가치 대비 비용 재평가 후 결정.

### 구조 변경 — 모듈별 독립 구성(2026-06-17, [ADR-005] 후속)
워커 호스팅과 함께 Spring 구성을 **모듈 독립**으로 재편했다: `app.xml`이 모듈당 Service(모듈 DLL 1개 + 모듈 xml 1개)를 등록하고, 단일 `nexaone.xml`을 모듈별 xml(mdm/est/fdc/rms/qms/cmms/pom/shp/sys.xml) 9개로 분할했다. 각 모듈 xml은 자기 서비스·리포·(스케줄)워커 빈만 담고 공통 서버 빈(eesDataSource·appConfiguration·eesDialect·opcUaDriver·messageBus·plantController·quartzScheduler)은 server.xml(부모)을 `ref`로 호출한다. 모듈당 1 plugin ALC. 모듈 워커의 enable은 그 모듈 xml이 제어한다(ADR-007).

## 결과

- **장점**: 모듈이 자기 background 작업을 소유(응집), API는 순수 웹으로 단순화, Server가 비웹 실행을 담당(역할 명확), 새 프로젝트 불필요.
- **비용/위험**: Phase 2~3은 Kafka 인프라 필수 + 이 환경 런타임 검증 불가. Server가 두 호스팅 모델(Spring 컨테이너 + Generic Host) 공존. cross-process 지연(수초).
- **비채택**: 신규 Worker 프로젝트(모듈 소유로 불필요), 인메모리 버스 유지 분리(불가), 현행 유지(지시와 배치).
