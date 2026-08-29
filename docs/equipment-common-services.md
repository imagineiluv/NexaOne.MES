# 설비 공통 서비스 경계와 운영 모델

## 원칙

설비별 PLC 태그, TRACE 의미, 소비량 계산, 상태·알람 해석은 플러그인이 담당한다. MES 공통 서비스는
로그인 작업자, 멱등성, 수량 원자성, 승인된 버전, append-only 이력과 조회 모델을 보장한다. 실제 설비와
MES 양쪽에서 검증되기 전에는 이 업무 테이블과 서비스를 NexaFramework로 옮기지 않는다.

## 기능별 소유권

| 기능 | 공통 소유 모듈 | 공통 서비스/원장 | 프로젝트 플러그인 책임 |
|---|---|---|---|
| PM/BM 보전 | EMS | 보전 계획·W/O·행동·체크·투입시간·작업자 매핑 | 고장 원인 분류, 설비별 자동 복구 판단 |
| PM 일정 | EMS | Calendar/Meter/Condition 정의와 다음 도래 상태 | 계기값·조건 규칙 사건 공급 |
| 예비부품 | EMS | 적정재고 정책, 공급처/리드타임, 설비 BOM, 수불·사용 원장 | 설비별 교체 판정 |
| 생산 툴 | EMS | 마스터, 장착/탈착, 사용, 수명, 점검/교정, 조건 스냅샷 | 툴 사용 TRACE와 조건 매핑 |
| 자재 LOT·소비 | IVT | 입고·이동·보류·해제·폐기·조정, 소비/반전, 재고 TX, TRACE projection | 펄스·카운터·유량 등 소비 정책 선택과 태그 바인딩 |
| 공정 LOT 처분 | POM | Scrap/Rework/Return/UseAsIs/Hold 원장과 불량수량 할당 | 불량 검출·판정 방식 |
| 이송용기(캐리어) | MDM·EST | 캐리어/등급 마스터, LOT 없는 `CarrierCleaned` 출력·OEE 이력 | RFID/바코드 판독, 세척 완료·불량 판정 |
| 설비 레시피 | RMS | 승인/Release, 설비 할당, 실행 시점 불변 스냅샷 | 설비 다운로드·적용 확인 어댑터 |
| 유틸리티 | EST | 전력·용수·가스·압축공기·증기 계량 및 기간 사용량 | 계기 프로토콜과 태그 매핑 |
| OEE | EST | 설비 상태시간과 표준화된 output event 집계 | 캐리어/LOT 등 설비 출력 사건 변환 |

영속 TRACE의 물리 테이블·보존정책·시간순 조회 인덱스는 FDC가 소유한다. IVT는 FDC 테이블을 JOIN하지 않고
Common `IFdcTraceSource` 범위/커서 계약으로 표본을 받은 뒤, 소비 바인딩 스냅샷과 재시작 가능한 inbox만
소유한다. 형제 Spring 컨텍스트 연결은 호스트 부모 프록시가 맡아 두 모듈 구현 DLL의 직접 참조를 만들지 않는다.

FDC 수집 워커는 `STOP` 같은 인터락 action key를 공통 코드에서 해석하지 않는다. 프로젝트가 구현하는 필수
`IFdcInterlockActionPort`가 stable `EffectId`를 멱등 키로 프로젝트별 동작을 수행하며, acknowledgement와 장치
readback을 모두 확인해야 적용 성공으로 인정한다. 이 운영 인터락 action은 collect/history DB보다 먼저 await하고,
그 뒤의 메시지 버스는 관제/UI 알림일 뿐 action 성공 판정에 참여하지 않는다. 한 입력에 여러 규칙이 동시에 맞으면
priority 순으로 각 rule/action을 모두 실행하고, 정상 범위로 돌아온 rule의 episode만 개별 해제한다. 여러 EffectId가
같은 STOP/STO 출력을 공유할 수 있으므로 adapter/controller는 출력별 활성 EffectId 집합을 영속 관리하고 마지막
소유자가 해제될 때만 출력을 deassert해야 하며, readiness에서 이 aggregate ownership을 확인하지 않으면 기동을 거부한다.

Worker 기동은 활성 endpoint/parameter topology, 활성 규칙의 불변 snapshot, DB의 전체 durable open effect와
프로젝트 adapter의 durable 미해제 EffectId inventory, open alarm을 먼저 preload한다. DB에는 없고 adapter에만 남은
effect도 같은 EffectId와 원 trigger 증거로 import한 뒤 reconcile하며, 양쪽 증거가 충돌하거나 inventory가 불완전하면
기동을 거부한다. 삭제된 설비·비활성 파라미터에 남은 open effect, 잘못된 영속 operator/action/priority,
규칙 부재 또는 adapter unavailable이면 run permit을 내리지 않는다. 실행 중 규칙 변경 API는 Conflict로 거부하며,
maintenance stop 뒤 재기동으로만 새 snapshot과 action capability를 검증한다. 인터락 이력 장애는 최초 action을
억제하지 않으며 같은 EffectId로 trigger 기록과 apply ack/readback, condition-normalized, release-pending,
resolved 상태를 CAS version으로 순서대로 재시도한다. 재기동 시 stale `ConditionNormalized`/`ReleasePending`도 먼저
물리 상태를 같은 EffectId로 재확인하고 현재 PLC snapshot이 정상일 때만 해제한다. 물리 Release가 확인되면 해당
episode는 즉시 active set에서 제거해 같은 규칙의 재위반이 새 EffectId로 Apply되도록 하고, 남은 DB terminal CAS는
별도 pending ledger에서 재시도한다. V146은 pre-lifecycle terminal 행을 `Resolved`로 보정하고, 새 해제는 release
acknowledgement/readback과 DB CAS가 모두 성공하기 전에는 `IS_RESOLVED`나 resolved 이벤트를 게시하지 않는다.
범위 일괄 해제 API는 이 증거 경계를 우회하므로 제공하지 않는다.

운전 허가와 FDC 감시 runtime 생존은 별도 상태다. 활성 effect, `ReleasePending`, terminal DB CAS 대기가 하나라도
있으면 자동운전 permit은 닫지만 PLC 구독과 supervisor는 계속 살아 있다. 따라서 수동 reset이 끝난 뒤 입력값이
그대로 정상 범위에 머물러 새 tag-change가 없어도 NexaLogic이 모든 callback 완료 뒤 게시한 immutable poll snapshot을
supervisor가 재평가해 같은 EffectId의 `ReleaseAsync`를 다시 확인한다. 단, 다음 transport poll은 read/callback 전에
`StartedPollCount`를 먼저 증가시키므로 `StartedPollCount == LatestCompletedPollSnapshot.CompletedPollCount`인
generation-fenced cut에서만 물리 해제를 재시도한다. 일반 persistence supervisor는 cached 값으로 Release하지 않고
Prepared/Applied/ConditionNormalized/ReleasePending/Resolved DB 증거만 재시도한다. 해제 확인 전에 값이 다시 위반하면
보류된 release intent를 폐기하고 같은 EffectId의 STOP을 먼저 reconcile한다. Bad 품질, 규칙 snapshot 무효화,
apply/reconcile 미확인, release cancellation·timeout처럼 물리 결과를 신뢰할 수 없는 경우에는 runtime 자체를 fault
처리해 원인 예외를 worker에 전달하고 driver를 닫은 뒤 명시적 재기동·재조정을 요구한다. permit은 활성 물리 effect와
terminal DB pending이 모두 0일 때만 다시 열린다.

completed-poll snapshot은 기존 runtime-health ABI를 바꾸지 않는 선택적 capability다. NexaLogic은
`StartWithSnapshotAsync`로 만든 단일 atomic stream에서만 이를 게시하고 일반·다중 subscription에는 모호한 snapshot을
게시하지 않는다. callback을 다음 poll 뒤로 미루는 jitter/coalescing window와 atomic 완료 snapshot의 조합은
기동 시 거부한다. callback 예외는 진단만 남기고 삼키지 않으며 listener를 fault 처리해 해당 poll의 completed count와
snapshot을 전진시키지 않는다. snapshot 자체도 generation, started/completed count, 완료 시각과 방어 복사된 값 묶음을
함께 보유한다.

임계치 알람은 parameter의 최고 severity 한 값이 아니라 `AlarmConfigId`별 episode로 추적한다. 같은 온도에서
Warning 규칙은 계속 성립하지만 Critical 규칙만 정상화되는 경우 Critical 이력만 해제하고 Warning은 open으로
유지한다. 재시작 시에도 durable open 행을 config별로 복원하며, 이 경계가 No.200처럼 다른 규칙이 함께 걸린
알람의 reset 누락을 막는다. 실제 Cleaner 화면·센서 원인 제거·PLC HIL은 별도 설비 검증 대상이다.

FDC worker는 `PlantController`를 호출하지 않는다. 즉 전체 Machine 시작이나 `OperationMode.Auto` 전환을 소유하지 않고,
생성한 `PlcDeviceInterface`만 직접 `InitializeAsync`한다. 각 endpoint는 driver-native 원자적
`StartWithSnapshotAsync`로 구독과 그 stream의 인과 baseline을 함께 받고, callback은 4,096건 bounded buffer에 둔 채
baseline과 후속 callback을 순서대로 평가한다. 모든 action ack/readback 뒤 FDC 소유 device만 `StartAsync`(Ping)하고,
잔여 buffer drain과 permit/live 전환을 같은 gate에서 완료한다. overflow·Bad/Disconnected 인터락 입력·callback 예외·
apply/reconcile 실패·release cancellation·runtime 무효화·listener 종료·완료 poll freshness 초과는 runtime을 fault
처리하고 worker supervisor가 소유 driver를 역순 Stop/Dispose한다. 확인되지 않은 일반 release/수동 reset 대기는
운전 permit만 닫고 supervisor 재시도를 유지한다. caller의 action readiness/apply/reconcile/release 대기는 bounded timeout으로 제한되지만,
그 timeout은 cancellation을 무시하는 adapter의 늦은 물리 동작까지 중단시키지 못한다. 프로젝트 adapter는 readiness에서
cancellation/deadline fencing을 명시 확인하고, 특히 timeout 뒤 늦은 Release가 발생하지 않도록 controller 또는 durable
command journal에서 강제해야 한다. 별도
snapshot read나 PLC timestamp watermark로 변화 유실을 추정하지 않는다.

현재 이 원자적 cutover 계약을 구현한 FDC 활성 프로토콜은 polling 기반 `ModbusTcp`, `SiemensS7`, `MitsubishiMc`,
`EtherNetIp` 네 종류다. OPC UA provider는 initial monitored-item notification과 stream baseline의 인과 fence를 아직
보장하지 않으므로 FDC endpoint 생성/매핑에서 지원하지 않는다. 모든 활성 `FDC_PARAMETER.ENDPOINT_ID`는 정확히 한
활성 endpoint를 가리켜야 한다. `TAG_MAP_PATH`는 worker enabled 시 필수이며, 상대경로는 `AppContext.BaseDirectory`
기준 절대경로로 정규화하고 연결 전에 파일 존재를 확인한다. 프로젝트 외부 절대경로도 허용하지만 tag map에는
비밀값을 저장하지 않는다. V145는 UnitId, S7 Rack/Slot, Mitsubishi station/routing/frame, 연결·read/write·heartbeat
timeout과 polling reconnect backoff를 명시적 allowlist 열로 저장한다. 임의 options JSON과 endpoint URL의 자격증명·
query·fragment·path는 허용하지 않으며 scheme은 생략하거나 `tcp://`만 사용한다.

기본 호스트 adapter는 의도적으로 unavailable인 fail-closed 구현이다. `Bad` 품질, callback 실패, listener fault와 frozen
poll stream에 따른 permit 철회·driver close는 운영 소프트웨어 수명주기일 뿐 물리 de-energize를 보장하지 않는다.
실제 PLC/STO 또는 safety PLC wiring/readback과 HIL이 통과하기 전에는 물리 안전이나 Production 승인을 주장하지 않는다.

OEE의 신규 출력은 EST 표준 output event를 사용해 LOT 없는 캐리어 세척도 같은 방식으로 집계한다. 기존 LOT
실적 fallback과 MDM 설비·작업조·시간대는 Common `IOeeEvidenceSource`가 계획/생산 snapshot으로 제공한다.
현재 production adapter는 MDM `IOeePlanDirectory`와 POM `IOeeProductionDirectory`의 소유 snapshot을 조합하며,
호스트·EST·Takt 구현에는 타 모듈 물리 테이블명이나 SQL이 없다. 이후 POM output event backfill 또는 MDM
query/projection으로 adapter를 교체해도 OEE Interface와 계산은
바뀌지 않는다. 실제 SQL Server 및 두 번째 설비 검증 전에는 OEE 구현 자체를 NexaFramework로 이관하지 않는다.

FDC worker 실행 정책도 module XML 상수가 아니라 `IConfiguration`이 소유한다. `Worker:Fdc:Enabled`,
`Worker:Fdc:InterlockActionTimeoutSeconds`, `Worker:Fdc:RuntimeHealth:FreshnessTimeoutSeconds`,
`Worker:Fdc:DriverCleanupTimeoutSeconds`,
`Worker:Fdc:Retention:{Enabled,BindingChangesQuiesced,IntervalSeconds,RetentionDays}`,
`Worker:Fdc:VirtualEvent:{Enabled,IntervalSeconds}`를 사용하며 모두 기본 OFF다. 이벤트 토픽은
`Worker:Fdc:Topic`, `Events:Outbox:Topic`, `nexaone.events` 순서로 결정한다.

OEE 재집계는 현재 계획의 `(Plant, Equipment, Shift)` 범위와 기존 `AGG_%`·`AGL_%`·`TKT_%` 산출물을
reconcile한다. 비활성 target, 삭제된 shift와 휴일·빈 계획은 stale 행을 남기지 않는다. 일자 집계는 날짜 전체를
정리하지만 수동 window 집계는 요청한 shift만 정리해 같은 날짜의 다른 shift 결과를 보존한다. 현재 범위의 계산이
모두 성공한 뒤에만 stale primary key를 한 트랜잭션으로 삭제하므로 실패한 재집계가 이전 결과를 먼저 지우지 않는다.

## 작업 관리(WorkScope)와 캐리어 실행

생산 W/O가 없는 세척 설비도 같은 실행 원장을 사용할 수 있도록 POM의 `WorkScope`를
작업 관리의 정본으로 둔다. `Batch`와 `Campaign`은 부모 범위이고 `Carrier`, `Lot`,
`Equipment`, `Other`는 실제 실행 대상 범위다. 따라서 세척 설비는 `Carrier`만 생성해도
되며, `WorkOrderId`와 LOT는 선택적 외부 참조로 남는다. 부모-자식 관계는
`POM_WORK_SCOPE_MEMBER`에 순서와 함께 보존하고, 모든 상태 전이는
`POM_WORK_SCOPE_EXECUTION` append-only 원장에 기록한다.

`작업 관리` 화면은 범위 유형·대상 ID·Carrier ID·설비·레시피·수량·상태·버전을
조회하고 Release/Start/Report/Hold/Resume/Complete/Cancel을 수행한다. API는
`POST /api/v1/pom/work-scopes`와 `POST /api/v1/pom/work-scopes/{id}/{action}`이며,
actor는 요청 본문이 아닌 로그인 JWT에서 캡처한다. `ExpectedVersion`과
`IdempotencyKey`를 모든 변경에 요구해 재시작·중복 요청이 같은 결과로 수렴하도록 한다.
Carrier 범위의 기본 계획 수량은 1이고 `TargetId=CarrierId`를 강제한다.

Cleaner는 로컬 Carrier/Pair Recovery를 정본으로 유지하면서 선택적으로 MES WorkScope ID를
Recovery state에 저장한다. 로컬 Recovery 커밋 뒤에만 `IWorkScopeExecutionSink`로
Running/Completed/Abandoned projection을 전송하며, sink 미구성·전송 오류는 설비 안전과
Recovery를 차단하지 않는다. 동일 `EventId` 재전송은 sink가 멱등 처리해야 한다. 이
경계로 MES 업무 이력과 설비의 실제 Motion/I/O Recovery 커서를 서로 대체하지 않는다.

## 현재 수동 보전 운전

현재는 로그인 사용자가 보전 W/O를 생성하고 Start/Complete/Cancel 명령을 수행한다. 명령의 actor는 요청
본문을 신뢰하지 않고 인증 클레임에서 가져오며, `EMS_MAINTENANCE_ACTION_HISTORY`에 실제 실행자와 상태 전이를
남긴다. PM 반복 정의는 자동 작업지시 생성을 기본 비활성화(`AUTO_CREATE_WO = 0`)한다. BM은 반복 일정이
아니라 고장 사건에서 수동 W/O를 생성하는 흐름으로 유지한다.

점검 결과와 작업시간도 같은 인증 actor, correlation, source event, 멱등성 키를 보존한다. 작업자 매핑이
지정된 경우 로그인 사용자와 매핑된 작업자가 일치해야 하며, 진행 중인 작업시간이 남아 있으면 W/O를
`Complete` 또는 `Cancel` 할 수 없다. 이 규칙은 서비스의 사전 검사뿐 아니라 저장소의 조건부 갱신에도
적용해 동시 요청 사이의 우회를 막는다.

LOT TrackIn/TrackOut/Hold/Release도 호출자가 관측한 `ExpectedVersion`과 재시도에 재사용할 안정된
`IdempotencyKey`를 필수로 전달한다. 서버는 현재 version을 대신 채우거나 임의 키를 만들지 않으며, 같은 키의
정확한 재실행만 기존 결과로 수렴시키고 다른 payload·version 재사용은 충돌로 거부한다.

## 자재 LOT와 소비 정책

자재 LOT의 상태와 수량 변경은 한 서비스가 입고(`Receive`), 이동(`Move`), 보류(`Hold`), 해제(`Release`),
폐기(`Scrap`), 조정(`Adjustment`)을 처리하고, 모든 변경을 같은 버전·멱등성 경계의 재고 TX에 기록한다.
소비와 반전도 이 LOT 원장 경계를 공유하므로 재고와 TRACE 이력이 서로 어긋나지 않는다.

소비 방식마다 별도의 외부 서비스를 만들지 않는다. 호출 계약은 하나로 유지하고 내부 정책을
`Direct`, `Pulse`, `CounterDelta`, `RateIntegrate`로 선택한다. 새 계측 방식은 정책 구현으로 추가하며,
태그 이름·스케일·리셋 판정·설비 신호 의미는 프로젝트 플러그인이 공통 명령으로 변환한다. 이렇게 하면
재고 원자성·반전·멱등성은 공통 서비스에서 한 번만 구현하고 설비별 차이만 교체할 수 있다.

### TRACE binding과 자재 장착 세션

Common `ITraceMaterialBridge`는 IVT의 두 명령만 노출한다. `TraceBindingCommand`는 TRACE
`(Equipment, Parameter)` 원천과 downstream Plant/FeedPoint·계산 정책을 `Create`/`Retire`하고,
`FeedSessionCommand`는 실제 투입점의 자재 LOT를 `Mount`/`Unmount`한다. 두 명령 모두
JWT 로그인 작업자, 호출자가 재사용하는 idempotency key, source system/event, correlation과 사유를
V151 command ledger에 결과 snapshot과 함께 보존한다. 변경은 `ExpectedVersion` CAS이며 같은 키의 같은
payload만 replay하고, payload 변경·source event 재사용·동시 version 변경은 충돌로 거부한다. 이미 commit된
정확한 replay는 읽기 동작이므로 maintenance가 닫힌 뒤에도 같은 결과를 돌려주지만 새 변경은 허용하지 않는다.
두 command ledger는 DB trigger로 update/delete를 금지하고 operation별 결과 snapshot `CHECK`를 적용한다.
idempotency/source identity는 SQL Server `BIN2`와 SQLite `BINARY`의 ordinal·대소문자 구분 의미를 맞춘다.

Binding 변경 계약은 구현돼 있지만 현재 mutation은 전면 비활성이다.
`Ivt:TraceConfiguration:BindingsEnabled=false`에서 API와 Spring bridge 직접 호출은
`IVT.TraceBinding.FeatureDisabled`로 저장소 접근 전에 fail-closed하고, true는 모듈 기동을 거부한다.
`MaintenanceMode=true`만으로는 이 gate를 열 수 없다. binding mutation과 FDC collection/retention/IVT projection이
같은 durable DB revision/advisory lock을 공유해 변경 중 purge·ingestion을 배제하고 crash 후 복구하는
cross-process fence를 구현한 뒤에만 활성화한다. 그 뒤에도 신규 시작점의 V150 completeness boundary,
retire cursor/drain, 과거 effective interval 중첩 검증과 `ExpectedVersion`·감사는 그대로 적용한다.
API 경로는 `POST /api/v1/ivt/trace-material/bindings/events`이며 직접 SQL 변경은 지원하지 않는다.

Feed session은 실제 운전 중 자재 교체 경로이므로 maintenance gate를 요구하지 않지만, 정본 자재 LOT가
`InStock`이고 잔량이 양수이며 material이 일치해야 한다. DB의 filtered unique index와 조건부 insert가 같은
`(Plant, Equipment, FeedPoint)`에 활성 `Mounted` 세션을 하나만 허용한다. 닫힌 과거 세션과도 장착 interval이
겹치는 backdated mount를 거부한다. LOT-side reservation은 `Move/Hold/Scrap/Adjustment`와 동일 LOT 재장착을
원자적으로 차단한다. `Mount`/`Unmount`의 물리 시각은 미래일 수 없고, `Unmount`에는 사유가 필수다.

`Unmount`는 물리 interval과 command ledger만 종결하고 LOT reservation은 의도적으로 유지한다. 이 조합이 현재의
fail-closed `PendingDrain` 표현이다. FDC 원천에는 commit/ingest sequence 또는 upper watermark가 없어서, 현재 inbox가
비었다는 사실만으로는 cutoff 이전 raw TRACE의 지연 유입 부재를 증명할 수 없다. 따라서 reservation을 해제하는
`Finalize`와 온라인 `Cancel`은 아직 제공하지 않는다. 늦게 투영된 cutoff 이전 TRACE는 계속 원래 LOT에 차감되지만,
해당 LOT는 재고 lifecycle에 재사용할 수 없다. durable FDC watermark + binding별 ingestion cursor + cutoff 이하 inbox
terminal을 한 증거로 묶는 Finalize 계약과 HIL 검증 전에는 이 기능을 기본 OFF로 유지하는 Production release
blocker다. host/module 경계의 `Ivt:TraceConfiguration:FeedSessionsEnabled` 기본값은 false이고, 이 blocker가
닫히기 전에는 true로 바꾸지 않는다. 잘못된 장착의 interval은 사유를 남긴 `Unmount`로 종결하고 이미 발생한 소비 오귀속은 별도의 명시적
reversal/correction으로 정정한다. API는
`POST /api/v1/ivt/trace-material/feed-sessions/events`다. 이 계약과 V151 테이블은 실제 MES/설비 공동 검증이
끝날 때까지 IVT에 남기며 NexaFramework로 이관하지 않는다.

## 일정 트리거 의미

- `Calendar`: 시간/일/주/월/년 주기와 `NEXT_DUE_AT`으로 도래한다.
- `Meter`: 누적 계기 baseline, 간격, `NEXT_METER_DUE_VALUE`로 도래한다.
- `Condition`: 설비 플러그인이 평가한 `CONDITION_RULE_ID` 사건으로 도래한다.

자동 W/O 생성은 중복 방지, 담당자/교대조 배정, 휴일 달력, 설비 정지창 정책을 실제 현장에서 검증한 뒤
활성화한다. 수동 운전 단계에서는 일정 조회와 도래 알림이 업무 상태를 임의로 바꾸지 않는다.

## 시퀀스 Recovery 소유권

MES의 W/O·LOT `CURRENT_STEP`은 보고용 업무 상태이며 실제 Motion/I/O 시퀀스의 재개 커서로 사용하지 않는다.
설비 로컬 복구는 다음 경계로 나눈다.

- 공통 커널 후보: checkpoint CAS/revision, mutation 멱등성, append-only journal,
  `PrepareEffect`/`ConfirmEffect`/`Reconcile`, durable store port와 crash-matrix 계약 테스트
- 설비 플러그인: 실제 sequence/cursor, recipe·설비구성·축 topology fingerprint, controller generation을 포함한
  일관된 readback, 축 허용오차·absolute encoder 신뢰성, safety/interlock와 운영자 승인
- Motion/I/O driver adapter: stable `EffectId`를 실제 명령에 전달해 중복 수락을 막거나 authoritative
  command-result를 조회하는 기능
- MES: W/O, Released recipe 실행 snapshot, 공정/자재 이력과 감사·보고

Cleaner Auto Start/Resume의 acquire/keep-alive/Stop 연결 코드는 준비돼 있지만 현재 RunAdmission은 운영에서
전면 비활성이다. `RunAdmission:Enabled` 누락/false이면 HTTP는 503, Spring bridge 직접 호출은
`RUN_ADMISSION_FEATURE_DISABLED`를 반환하며 capability를 발급·연장·release하지 않는다. true는 FDC 모듈 기동을
거부한다. 현재 process-local request/tombstone 원장은 서버 재시작·failover의 동일 요청 재발급을 막지 못하고
전역 용량도 한 client가 소진할 수 있기 때문이다.

DB 등 durable shared request ledger, client/equipment별 quota, 다중 인스턴스 owner/sticky routing과 장애전환 계약을
구현한 뒤에만 이 gate를 재검토한다. 이후에도 credential/설비 allowlist, FDC authority·fence·safety epoch,
인터락, hard/soft TTL과 Cleaner Stop 직렬화가 하나의 계약이어야 하며 driver 효과 멱등성, controller reboot·recipe
변경·축 오차, 실제 PLC/STO wiring/readback, 네트워크 단절·MES 재시작을 포함한 HIL을 통과해야 한다. 두 번째
설비에서도 재사용성이 입증된 커널만 NexaFramework 이관 후보로 삼는다.

## Spring.NET과 직접 참조 기준

NexaMES 호스트 내부의 새 웹/API 구성은 Microsoft DI를 기본으로 사용한다. Spring.NET의 `CreateServer`와
모듈 XML은 기존 NexaFramework 기반 모듈을 독립 ALC로 로드하고 조립하는 composition boundary로만 유지한다.
컨트롤러는 Spring bean을 직접 탐색하거나 업무 서비스를 상속하지 않고 Common bridge 계약을 DI로 받으며,
호스트 프록시가 필요한 Spring bean 연결을 한곳에서 처리한다. XML 조립 루트가 현재 `ApplicationServer`를
한 번 취득해 `ModuleBeanResolver`에 주입하고 모든 형제-context 프록시는 이 typed resolver만 사용한다.
프록시의 요청별 전역 `GetInstance().GetBean()` 탐색은 허용하지 않는다. 따라서 XML은 배선과 교체 가능 구현을
담고, 업무 규칙·SQL·설비별 조건은 담지 않는다.

Motion·I/O·Serial·Vision·SECS/GEM은 드라이버로 직접 주입하거나 프로젝트에서 명시적으로 참조한다.
`NexaFramework.Drivers.Hosting`은 여러 드라이버의 발견·수명주기·상태진단을 표준화해야 할 때 쓰는 선택적
편의 계층이며 현재 NexaMES 공통 업무 서비스의 필수 의존으로 추가하지 않는다.

## DB 조회 성능과 모듈 소유권

조회 성능은 테이블이나 View 개수가 아니라 실제 Repository/named query의 `WHERE`·`JOIN`·`ORDER BY`로
관리한다. V130~V134는 Tool 사용·보전 W/O, W/O별 Spare 사용, Recipe 적용기간, OEE/Takt/Loss 일자
reconciliation, LOT·처분 경로에 복합·filtered index를 추가한다. V141은 재시작 시 FDC open 알람·인터락을
설비/파라미터 단위로 복원하는 filtered index를 제공하고, V142는 누적 inbox를 매 poll마다 스캔하던 TRACE
cursor를 binding별 단일 영속 행으로 분리하며 `IS_WORK_ITEM=1`인 retry 행만 시간순으로 읽는다. POM LOT/Hold/
Defect/W/O와 EMS W/O의 선택 필터 없는 화면 조회는 고유 tie-break 정렬과 최근 500건 상한을 가진다. V143은
endpoint tag map과 parameter→endpoint 명시 매핑, `(ENDPOINT_ID, IS_ACTIVE)` index, SQL Server의 영속 rule
operator/action/priority CHECK를 추가한다. 그 실제
named-query 형태에 맞는 전역·filtered index를 사용한다. V144는 OEE가 읽는 POM TrackOut 증거를
`(PLANT_ID, EQUIPMENT_ID, TRACK_OUT_TIME)` filtered/covering 경로로 분리한다. V142 증분 cursor backfill은
상관 anti-join 대신 binding별 `ROW_NUMBER()` 1회 정렬로 최신 행을 고른다. 이미 게시된 V142는 체크섬 불변성을
유지하고, retry work flag 제약은 새 V147에서 기존 불일치 정규화 뒤 추가한다. SQLite는 `BEGIN IMMEDIATE` 안에서
backfill→불일치 검증→canonical trigger→durable marker를 원자적으로
커밋해 구버전 writer의 중간 진입과 재기동 시 누적 inbox UPDATE/정렬 재실행을 막고, trigger가 `STATUS`와
`IS_WORK_ITEM`의 동치를 강제한다. POM mixing의 PK와 동일했던 중복
index는 제거한다. SQLite 증분
회귀는 이름만 확인하지 않고 key 순서·정렬·partial 조건과 대표 쿼리의 `EXPLAIN QUERY PLAN` 선택까지 검증한다.
TRACE cursor는 SQLite의 가변 소수 정밀도 시각을 7자리로 보정한 expression key와 `COLLECT_ID`로 정렬한다.
SQLite의 `IX_FDC_TRACE_SOURCE`도 같은 expression을 사용해 `LIMIT` 전에 전체 유효 범위를 임시 정렬하지 않으며,
재개 cursor가 있으면 `max(EFFECTIVE_FROM, cursor)`를 index seek 시작점으로 써 반복 page가 과거 행을 다시 훑지
않는다. 일반 parameter 최신/기간 조회는 가변 정밀도 간 동시각 순서가 업무 결과를 바꾸지 않으므로 V017의
raw 시간 index를 그대로 사용해 불필요한 expression sort를 피한다.
현재 Common initializer가 FDC/IVT의 legacy backfill·trigger·marker를 아는 구조는 ADR-0004의 한시 예외이며,
module-owned schema contribution을 도입해 NexaFramework 이관과 Production release 승인 전에 제거한다.

일반 View는 SQL 의미를 캡슐화할 뿐 결과를 저장하지 않으므로 그 자체를 성능 개선으로 간주하지 않는다.
여러 소비자가 공유할 안정된 read contract가 생길 때 소유 모듈 안에 View를 만들고, OEE·Takt·Utility·TRACE처럼
반복 계산 비용이 큰 경로는 summary/projection table을 materialized read model로 유지한다. SQL Server indexed
view·columnstore·partition은 Query Store의 logical read와 쓰기 증폭을 측정한 뒤 별도 운영 ADR로 승인한다.
`Get-MssqlPerformanceBaseline.ps1`은 Query Store 상위 logical-read 쿼리, DB의 자동 통계 생성·갱신 옵션,
index read/write 사용량·실제 key/include/partition column·크기,
통계 갱신 시각·sampling·변경 건수, missing-index DMV 힌트와 View/indexed-view의 실제 index 정의·사용량을 UTC
run-id별 읽기 전용 CSV와 manifest로 수집한다. 대표 산출물은 `query-store-plan-logical-reads.csv`,
`query-store-window.csv`, `view-dependencies.csv`이며 원문 query/View SQL과 plan XML은 기본 수집하지 않는다.
민감 SQL/계획 원문이 필요한 별도 진단은 보안 승인과 최소 권한·보존기간을 먼저 정한다. 물리 fragmentation은
기본 수집에서 제외하며, 점검 창에
`-IncludePhysicalStats`를 지정한 경우에만 `-Top` 및 `-PhysicalStatsMinPageCount`로 제한한 큰 index 후보를
`LIMITED` 모드로 읽는다. Query Store가
`READ_WRITE`가 아니거나 `VIEW DEFINITION`/필수 DMV 보고가 빠지면 기본적으로 전체 실행을 실패시키며 명시적 partial
결과는 승인 근거로 쓰지 않는다. `AUTO_CREATE_STATISTICS`·`AUTO_UPDATE_STATISTICS`가 OFF인 DB도 같은
fail-closed 전제조건으로 처리한다. DMV 힌트는 현재
index와의 중복, 필터 선택도, 변경 빈도, 저장 공간을 검토하는 후보 자료일 뿐 자동 DDL의 근거로 사용하지 않는다.
성능 승인 기준선의 최소 엔진 계약은 Query Store를 제공하는 SQL Server 2016 이상이며, index key/include 집계는
2016에서도 동작하는 ordered `FOR XML PATH` 방식으로 수집한다.

Statistics는 앱 서비스·드라이버 기능이 아니라 DB 운영 계약으로 둔다. SQL Server는
`AUTO_CREATE_STATISTICS`·`AUTO_UPDATE_STATISTICS` ON을 기본으로 하되, Query Store 계획 회귀와
`sys.dm_db_stats_properties` 변경량·sampling을 함께 확인해 편향된 통계만 점검 창에서
`UPDATE STATISTICS ... WITH RESAMPLE`로 갱신한다. 전체 `FULLSCAN`을 마이그레이션이나 호스트 기동에
묶지 않는다. SQLite는 대량 migration·retention·import 후 백업과 쓰기 정지를 확보한 점검
창에서 `PRAGMA optimize;`를 우선하고, 실행 계획 회귀가 남은 특정 테이블에만 수동
`ANALYZE table_name;`을 적용한다. 요청 hot path와 Common schema initializer에서는 둘 다 자동 실행하지 않는다.

V148은 `FDC_COLLECT_DATA` 보존 삭제에 시간 선행
`IX_FDC_COLLECT_RETENTION(COLLECTED_AT, COLLECT_ID)`를 제공한다. repository는 기본 1,000행별 짧은
transaction을 사용하고 호출당 최대 100 batch에서 양보하며 다음 주기가 같은 cutoff를
이어서 처리한다. 이 상한이 lock·transaction log·SQLite single-writer 독점을 제어하므로,
backlog가 연속 상한에 닿을 때 worker가 기록한 최고 행 연령·호출 소요시간과 SQL Server Query
Store/대기 DMV 또는 SQLite busy/lock 계측에서 얻은 writer 대기를 함께 비교한 뒤 주기 또는 점검
창을 조정한다. repository 반환값의 전체 소요시간을 DB writer 대기시간으로 오인하거나, 검증 없이
batch 크기를 키우지 않는다.

V150은 보존 실행 전에 `FDC_TRACE_RETENTION_STATE/GLOBAL.COMPLETENESS_BOUNDARY`를 같은 transaction에서
단조 증가시키고, 실제 DELETE는 그 경계가 기록된 경우에만 수행한다. requested cutoff는 Common
`IFdcTraceRetentionGuard`를 통해 IVT가 계산한 활성 binding 전역 low-watermark보다 앞으로 갈 수 없다. IVT는
binding마다 `max(EFFECTIVE_FROM, LAST_COLLECTED_AT)`를 사용하고 cursor가 아직 없으면 `EFFECTIVE_FROM`부터
보호하며 FDC는 IVT
물리 테이블을 직접 조회하지 않는다. SQLite guard는 활성 binding/cursor 값을 행별 canonical UTC로 검증하고
파싱된 실제 시각의 최소값을 사용하므로 lexical `MIN(TEXT)`에 의존하지 않는다. 한 행이라도 invalid/T/Z/offset
형식이면 보존 실행을 fail-closed한다. purge와 동시 읽기 또는 이후 새 binding의 resume 지점이 completeness
boundary보다 오래되면 `FdcTraceGapException`으로 중단하고 빈 페이지나 다음 남은 표본으로 조용히 건너뛰지
않는다. 최초 전환은 남은 `MIN(COLLECTED_AT) + 100ns`(빈 DB는 DB UTC)를 보수적으로 seed하며,
SQL Server에서 seed~직접 DELETE guard commit 동안 `TABLOCKX, HOLDLOCK`으로 구버전 writer 우회를 막는다.
따라서 V150은 복원본에서 lock 대기·timeout·rollback을 측정하고 구/신 FDC collection·retention writer를 중지한
maintenance window에서만 명시 승인으로 적용한다. SQL Server 신규 TRACE INSERT guard는 RCSI에서도
`READCOMMITTEDLOCK, HOLDLOCK`으로 최신 경계를 공유 잠금해 purge와 직렬화하되 일반 INSERT끼리는 병렬 실행한다.
SQLite도 singleton 삭제·경계 후퇴와 `INSERT OR REPLACE`에 의한 V149 fence/V150 경계 초기화를 canonical BEFORE
INSERT trigger로 차단하고, V148 retention index의 동일 이름
오정의는 기동 reconciliation에서 정확한 `(COLLECTED_AT, COLLECT_ID)` 순서로 교체한다. SQLite 안전 시각은
`yyyy-MM-dd HH:mm:ss[.fffffff]` UTC text만 허용하고 `T`/`Z`/offset, 7자리를 넘는 소수, 존재하지 않는 달력
날짜를 거부한다. 새 TRACE write는 항상 7자리로 저장하며 legacy 가변 소수 정밀도는 동일한 padded key로 비교한다.
보존 경계보다 오래된 late/backdated INSERT, 기존 raw TRACE UPDATE와 `INSERT OR REPLACE`도 거부해 원천을
append-only로 유지한다. 정상 기동은 invalid timestamp partial index를 검사하고, 제약/index가 누락·변조된
경우에는 전체 재검증 후 오염 행이 하나라도 있으면 fail-closed한다.
schema object 이름은 SQLite 식별자 규칙대로 대소문자 무시로 찾되, trigger/index SQL 정의는 문자열 리터럴의
대소문자 의미를 보존하도록 ordinal 비교해 stale 안전 제약을 정상 정의로 교체한다.
현재 guard 조회와 FDC purge는 하나의 cross-module DB transaction이 아니므로 binding 보호 시작점을 낮추는
online 변경과의 원자성은 아직 제공하지 않는다. 따라서 retention을 켜려면 전체 프로세스 실행기간 동안 binding
INSERT/활성화·재활성화, `EFFECTIVE_FROM/TO` 변경과 cursor 수동 후퇴를 운영 절차로 동결하고
`BindingChangesQuiesced=true`를 함께 설정해야 한다. 기본값은 false이며 서약이 없으면 조립 즉시 실패한다.
지속 online 변경은 binding mutation과 purge가 공유하는 durable revision/advisory-lock protocol을 도입한 뒤에만 승인한다.

V150 이전 이력 때문에 활성 binding의 `max(EFFECTIVE_FROM, cursor)`가 현재 completeness boundary보다 과거인
scope가 하나라도 있으면 `Worker:Ivt:TraceMaterialConsumption:Enabled=false`를 유지한다. 현재 ingestion은 모든
활성 scope를 한 batch로 읽으므로 이 gap 하나가 정상 scope까지 중단시키며, boundary 후퇴·pre-boundary raw INSERT·
지원되지 않는 직접 SQL range 변경으로 복구할 수 없다. 이것은 전체 TRACE material worker 활성화의 명시적
release blocker다. 데이터 손실 정책을 임의 구현하지 않고 후속 ADR에서 다음 중 하나를 선택·검증해야 한다.

- strict/manual data repair: 원본·소비 원장 대조와 승인된 수동 정정 뒤 기존 Create/Retire 계약을 유지한다.
- audited Abandon/Rebase: reason, source evidence, 전용 권한, ledger/CAS를 요구하는 maintenance-only 명령을 추가한다.
- durable scope gap health: gap scope를 영속 격리하고 healthy scope만 계속 처리하되 복구·재합류 조건을 감사한다.

V151은 binding/feed session/consumption 대형 기존 테이블에 컬럼을 추가하고 active source/LOT 고유 index를 교체한다.
새 TRACE 소비는 typed `FEED_SESSION_ID`를 기록하고 session/LOT 복합 FK로 귀속을 검증한다. V137 append-only 이력을
깨지 않도록 기존 소비 행은 갱신하지 않으며 legacy provenance는 immutable `CORRELATION_ID`에 남고 typed 컬럼은
null을 유지한다. SQLite는 신규 source key 중복을 구형 V114 index 삭제 전에 검사하고 index 교체를 한 transaction으로
수행하며, `Foreign Keys=False`에서도 command ledger와 consumption provenance가 고아가 되지 않도록 동등 trigger를
강제한다. 기존 Unmounted session은 안전한 LOT reservation winner를 추측할 수 없으므로 감사된 reconciliation 없이
증분 적용하지 않는다. SQL Server 적용은 복원본 리허설·중복/legacy 대상·inbox 규모·log/lock/rollback 근거와
`-ApproveHighImpactMigrations`가 모두 있을 때만 허용한다.
Feed session Unmount 뒤 LOT reservation은 자동 해제되지 않으며, 위의 durable drain Finalize가 도입되기 전까지
수동 SQL로 비우거나 다른 LOT에 재귀속해서는 안 된다.

V152는 WorkScope·member·execution 원장과 LOT 없는 Carrier 세척 상관관계를 추가한다.
EMS Tool 사용 이력에는 WorkScope/Carrier·활동·세척/점검 결과를 선택적으로 기록하고,
EST 출력 및 IVT 소비에는 동일 WorkScope/Carrier 키를 보존한다. V153은 RMS 레시피 실행
스냅샷에도 WorkScope/Carrier를 결박하고, V154는 WorkScope member의 부모별 순번 충돌을
사전 검사하는 unique index를 추가한다. 세 migration은 SQLite initializer의 동등
trigger/check와 MSSQL append-only/member guard를 함께 제공하며, 실제 MSSQL 적용은
복원본 리허설과 `-ApproveHighImpactMigrations` 승인 뒤에만 수행한다.

V149는 `FDC_RUNTIME_OWNERSHIP`의 단일 `GLOBAL` 행으로 FDC 실시간 writer를 선출한다. 획득은 DB가 관찰한
기존 owner+fence를 CAS하고 DB UTC 시각으로 만료를 판정하며, 새 소유권마다 `FENCE_TOKEN`을 정확히 증가시킨다.
각 action readiness/apply/reconcile/release 호출은 일반 action timeout과 캡처한 wall-clock/monotonic lease
잔여시간 중 가장 짧은 값으로 adapter token과 caller 대기를 제한한다. 성공 응답도 같은 owner/fence/config가
유효한지 다시 확인한 뒤에만 수락하므로 lease 직전 시작한 늦은 Release를 confirmed로 처리하지 않는다.
각 acquire는 재사용하지 않는 256-bit random secret을 만들고 DB에는 lowercase SHA-256 hash만 저장한다. 성공
호출자만 secret을 숨긴 opaque grant를 받으며 renew/release는 이 grant의 owner+fence+설정 digest+secret hash가
모두 같은 경우에만 성공한다. 공개 state의 `HasOwnerTuple`은 만료 여부와 무관한 tuple 존재 표시이며 운전 권한이
아니다. `CONFIG_REVISION`은 임의 label이 아니라 canonical 설정 snapshot의 lowercase 64자리 SHA-256 hex digest다.
lease duration은 두 DB 모두 동일하게 integer millisecond로 ceil하며, trigger는 writer가 제공한 시각이 아니라 DB
현재 시각으로 acquire/renew 만료를 판정하고 heartbeat가 DB now 부근인지와 expiry가 최대 1일인지 검증한다.
각 acquire/renew 호출 직전에 `Stopwatch` timestamp를 고정하고 설정 TTL을 더한 보수적 process-local deadline도
같이 발행한다. DB 응답 지연과 DB/host wall-clock 차이는 이 local 권한을 늘리지 않고 남은 시간을 줄이며,
permit 조회·startup/live sample·DB retry·action 경계는 heartbeat continuation과 무관하게 이 deadline을 동기 확인한다.
release는 소유 tuple과 secret hash만 비우고 행과 마지막 fence는 영구 보존하므로 DB 복구·정리 절차에서 이 행을
삭제하거나 token을 0으로 재시드하면 안 된다.
물리 Release 전에는 completed poll의 모든 interlock 입력 품질을 먼저 검사한다. 이후 DB 영속화 대기 뒤와 adapter 호출 직전에
대상 poll generation/count가 여전히 current인지, 그리고 다른 endpoint를 포함한 전체 활성 subscription이 running/fresh인지
재확인한다. 뒤쪽 Bad 입력이나 await 중 freshness 초과가 하나라도 있으면 어떤 EffectId도 Release하지 않고 fail-closed한다.
이 DB lease만으로 물리 명령의 split-brain을 막을 수는 없다. Cleaner action adapter/controller가 모든 명령과
ack journal에서 fence를 저장하고 이전 token을 거부하는 단계가 완료되기 전에는 자동 운전 권한으로 사용하지 않는다.

SQL Server 마이그레이션 이력은 파일명뿐 아니라 LF 정규화 SHA-256을 저장한다. 적용된 SQL의 내용 drift는
배포를 중단하며, 체크섬이 없던 기존 DB는 백업·승인 소스 대조·staging 복원 리허설 뒤 명시적인 1회 adoption만
허용한다. DB에만 있는 미래 version과 중간 누락 뒤의 later-applied version은 어떤 DDL·ops seed보다 먼저
거부해 downlevel binary 및 out-of-order migration 실행을 막는다. `-DryRun`은 advisory lock과 이력 조회만
수행하며 누락된 history table/column을 생성하지 않는다. V142처럼 대량 기존 행을 갱신하는 버전은 보조 index가 있어도 transaction log·lock 비용이 남으므로
운영 규모 데이터의 upgrade rehearsal을 별도 릴리즈 gate로 둔다. V144와 V130~V141의 hot-table index build도
크기·blocking·쓰기 증폭을 같은 기준으로 측정하며, 전환 중 TRACE/POM writer 정지와 edition별 ONLINE/RESUMABLE
가능 여부를 DBA가 승인한다. V142/V144/V146/V147/V148/V150/V151 pending 적용은 이 준비를 완료한 승인 실행에서
`-ApproveHighImpactMigrations`를 주지 않으면 러너가 거부한다.

완료된 TRACE inbox 행은 filtered work set에서 즉시 빠지지만 감사·재처리 근거로 남는다. 장기 보존량이 확인되면
source FDC 원장, 소비 원장과의 재처리 경계를 먼저 고정한 뒤 archive/purge를 적용한다. 목록의 500건 상한은
무제한 scan 방지선이며, 대규모 운영 화면은 다음 단계에서 인증된 Plant/Equipment scope와 keyset pagination을
필수 계약으로 승격한다. 선택 필터의 `(@filter IS NULL OR COLUMN=@filter)`와 MSSQL `NOLOCK`은 실제 Query Store
계획과 운영 일관성 요구를 확인해 scope별 query 또는 snapshot isolation 정책으로 교체할지 결정한다.

QMS와 POM 저장소는 다른 모듈 물리 테이블을 직접 조회하지 않는다. EST 출력 검증에 필요했던 설비·캐리어 조회도
Server SQL에서 MDM 소유 `IEquipmentOutputMasterDirectory`로 이동했다. POM·IVT·MDM·SYS·PRC 소유 directory/bridge와
호스트의 SQL 없는 형제 Spring-context proxy를 사용한다. 현재 예외는 SLS 모듈 부재에 따른 읽기 전용
`LegacySalesOrderMrpProjection`과 로그인–보전 작업자 매핑을 SYS가 제공하는 `MaintenanceIdentityDirectory` 두
건뿐이며, 각각 ADR-0002/0003의 정확한 파일 allowlist와 2026-11-30 검토 기한으로 제한한다.

## 프레임워크 이관 게이트

다음 조건을 모두 만족한 계약만 NexaFramework 후보가 된다.

1. NexaMES와 최소 한 개 설비 프로젝트가 같은 계약을 사용한다.
2. 설비별 차이가 플러그인 포트 뒤에 남고 MES 테이블이 계약에 노출되지 않는다.
3. 재시도·프로세스 재시작·동시 실행·반전/취소 테스트가 통과한다.
4. SQL Server와 SQLite에서 동일한 업무 결과를 낸다.
5. 실제 로그인 작업자, correlation/source event와 원본 TRACE를 역추적할 수 있다.

## 2026-08-29 검증 기록

- Release solution build(`-warnaserror`): 경고 0, 오류 0
- Unit: 1,964/1,964 통과(WorkScope Batch/Campaign/Carrier lifecycle·idempotency·UI command driver와 FDC/TRACE/Recovery 회귀 포함)
- FDC Unit namespace focused: 256/256 통과
- FDC/Spring focused boot: 18/18 통과(worker 기본 OFF + fail-closed adapter 조립 포함)
- Server/SQLite integration: 기존 전체 964/964 통과, V154 관련 focused 4/4 통과
- Portal: 116/116, production build 성공, `npm audit` 취약점 0
- NexaLogic PLC: Unit 12/12, Core 57/57, Integration 14/14, Hardware Simulation 43/43 — 합계 126/126 통과
- modules-ON child-process smoke: 11개 모듈과 호스트 소유 선언형 bridge 47개를 최신 Release 호스트에서 실제 부팅
- migration: V001~V154 strict 이름·숫자 순서·중복·LF 정규화 SHA-256 검증 통과, 신규/증분 SQLite와 MSSQL 정적 계약 통과
- publish: Release publish 성공, 산출물 510개·모듈 11개, 독립 `/health`·JWT 로그인 통과,
  `NexusCom`·`NexusFramework`·`NexusLogic` 파일명/설정 참조 0건
- 정적 경계: QMS/POM 저장소 foreign physical-table SQL 0건(ADR-0002/0003만 허용), Common SQLite bootstrap은
  ADR-0004의 FDC·IVT target whitelist architecture test로 제한, 충돌 marker·diff whitespace 오류 0건

이 실행 환경에는 `NEXAONE_MSSQL_TEST_CONN`, `sqlcmd`, SQL Server 서비스가 없고 Docker daemon도 실행되지
않아 실제 SQL Server 왕복 테스트는 수행하지 못했다. 원격 CI도 비공개 서브모듈용
`NEXA_SUBMODULE_TOKEN` 사전검사에서 중단됐다. SQL Server 검증, 비공개 서브모듈 credential,
Cleaner 실제 하드웨어 Recovery HIL은 프레임워크 이관 및 자동 재개 활성화 전 필수 잔여 gate다.
