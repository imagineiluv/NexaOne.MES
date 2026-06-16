# ADR-006 — 웹/워커 분리: API는 웹 전용, 비웹 워커는 모듈 소유 + NexaOne.Server 호스팅

- **Status**: Accepted (채택 — 단계별 구현; Phase 1 완료, Phase 2~3은 Kafka 환경에서 런타임 검증)
- **Date**: 2026-06-16
- **관련**: [ADR-002](ADR-002-event-bus.md)(Event Bus/Outbox), [ADR-005](ADR-005-server-service-container.md)(Server=서비스 빈 컨테이너), 설계문서 §6.1·§8·§10.4
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

**(4) cross-process 전송 = Kafka 백본.** 워커 호스트(Server)가 outbox→Kafka 발행, API가 Kafka→SignalR 구독.

**(5) API는 웹 전용으로 축소.** 워커를 Server로 이전한 뒤 API에서는 비활성하고, 버스 구독자→SignalR만 유지.

## 단계별 구현 (각 단계 비파괴·검증)

- **Phase 1 (완료)**: NexaOne.Server에 .NET Generic Host 추가(Spring 빈 컨테이너·SQLite 스키마 부트스트랩·AddService 보존). 워커 호스팅 토대 마련 — 아직 워커 미이전(API 그대로). SQLite 부팅 + 전체 스위트로 검증.
- **Phase 2**: FDC 수집 오케스트레이션(`FdcCollectorHostedService`)을 FDC 모듈 소유 워커로 이전 + 도메인 이벤트 발생으로 전환. Server가 실행, API 구독자가 Kafka→SignalR. (런타임 검증 Kafka 필요.)
- **Phase 3**: Outbox 디스패처를 Server로 이전, API에서 비활성. (Kafka 필요.)
- **Phase 4 (보류)**: 워크플로 엔진 — `WorkflowController`(웹)가 직접 호출하므로 분리 시 웹→엔진도 cross-process가 돼 비용이 큼. 가치 대비 비용 재평가 후 결정.

## 결과

- **장점**: 모듈이 자기 background 작업을 소유(응집), API는 순수 웹으로 단순화, Server가 비웹 실행을 담당(역할 명확), 새 프로젝트 불필요.
- **비용/위험**: Phase 2~3은 Kafka 인프라 필수 + 이 환경 런타임 검증 불가. Server가 두 호스팅 모델(Spring 컨테이너 + Generic Host) 공존. cross-process 지연(수초).
- **비채택**: 신규 Worker 프로젝트(모듈 소유로 불필요), 인메모리 버스 유지 분리(불가), 현행 유지(지시와 배치).
