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
priority 순으로 각 rule/action을 모두 실행하고, 정상 범위로 돌아온 rule의 episode만 개별 해제한다.

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

임계치 알람은 parameter의 최고 severity 한 값이 아니라 `AlarmConfigId`별 episode로 추적한다. 같은 온도에서
Warning 규칙은 계속 성립하지만 Critical 규칙만 정상화되는 경우 Critical 이력만 해제하고 Warning은 open으로
유지한다. 재시작 시에도 durable open 행을 config별로 복원하며, 이 경계가 No.200처럼 다른 규칙이 함께 걸린
알람의 reset 누락을 막는다. 실제 Cleaner 화면·센서 원인 제거·PLC HIL은 별도 설비 검증 대상이다.

FDC worker는 `PlantController`를 호출하지 않는다. 즉 전체 Machine 시작이나 `OperationMode.Auto` 전환을 소유하지 않고,
생성한 `PlcDeviceInterface`만 직접 `InitializeAsync`한다. 각 endpoint는 driver-native 원자적
`StartWithSnapshotAsync`로 구독과 그 stream의 인과 baseline을 함께 받고, callback은 4,096건 bounded buffer에 둔 채
baseline과 후속 callback을 순서대로 평가한다. 모든 action ack/readback 뒤 FDC 소유 device만 `StartAsync`(Ping)하고,
잔여 buffer drain과 permit/live 전환을 같은 gate에서 완료한다. overflow·Bad/Disconnected 인터락 입력·callback 예외·
action 실패·runtime 무효화·listener 종료·완료 poll freshness 초과는 permit을 철회하고 worker supervisor가 소유 driver를
역순 Stop/Dispose한다. caller의 action readiness/apply/reconcile/release 대기는 bounded timeout으로 제한되지만,
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
`Worker:Fdc:Retention:{Enabled,IntervalSeconds,RetentionDays}`,
`Worker:Fdc:VirtualEvent:{Enabled,IntervalSeconds}`를 사용하며 모두 기본 OFF다. 이벤트 토픽은
`Worker:Fdc:Topic`, `Events:Outbox:Topic`, `nexaone.events` 순서로 결정한다.

OEE 재집계는 현재 계획의 `(Plant, Equipment, Shift)` 범위와 기존 `AGG_%`·`AGL_%`·`TKT_%` 산출물을
reconcile한다. 비활성 target, 삭제된 shift와 휴일·빈 계획은 stale 행을 남기지 않는다. 일자 집계는 날짜 전체를
정리하지만 수동 window 집계는 요청한 shift만 정리해 같은 날짜의 다른 shift 결과를 보존한다. 현재 범위의 계산이
모두 성공한 뒤에만 stale primary key를 한 트랜잭션으로 삭제하므로 실패한 재집계가 이전 결과를 먼저 지우지 않는다.

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

현재 Cleaner의 복구 커널과 Simulator gate만으로는 실제 설비 자동 재개를 허용하지 않는다. 앱 시작 경로와
실제 오케스트레이터, driver 효과 멱등성, controller reboot·recipe 변경·축 오차를 포함한 HIL 검증이 모두
연결될 때까지 실제 하드웨어는 fail-closed로 유지한다. 특히 Cleaner의 Auto Start/Resume 경로에는 아직 FDC permit을
소비하는 cross-process admission lease가 없다. 최초 거부, 세대번호 fencing, heartbeat/TTL, 연결 단절·재시작 즉시
철회와 Stop 직렬화를 갖춘 계약을 실제 시작 전후에 연결해야 하며 단순 bool 또는 1회 HTTP 조회로 대체하지 않는다.
다중 MES/FDC 인스턴스의 effect 소유권도 외부 durable lease/fencing과 장애전환 시험 전에는 단일 writer 운영으로
제한한다. 두 번째 설비에서도 재사용성이 입증된 커널만
NexaFramework 이관 후보로 삼는다.

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
현재 Common initializer가 FDC/IVT의 legacy backfill·trigger·marker를 아는 구조는 ADR-0004의 한시 예외이며,
module-owned schema contribution을 도입해 NexaFramework 이관과 Production release 승인 전에 제거한다.

일반 View는 SQL 의미를 캡슐화할 뿐 결과를 저장하지 않으므로 그 자체를 성능 개선으로 간주하지 않는다.
여러 소비자가 공유할 안정된 read contract가 생길 때 소유 모듈 안에 View를 만들고, OEE·Takt·Utility·TRACE처럼
반복 계산 비용이 큰 경로는 summary/projection table을 materialized read model로 유지한다. SQL Server indexed
view·columnstore·partition은 Query Store의 logical read와 쓰기 증폭을 측정한 뒤 별도 운영 ADR로 승인한다.
`Get-MssqlPerformanceBaseline.ps1`은 Query Store 상위 logical-read 쿼리, index read/write 사용량·key/include·크기,
통계 갱신 시각·sampling·변경 건수, missing-index DMV 힌트와 View/indexed-view의 실제 index 정의·사용량을 UTC
run-id별 읽기 전용 CSV와 manifest로 수집한다. 물리 fragmentation은 기본 수집에서 제외하며, 점검 창에
`-IncludePhysicalStats`를 지정한 경우에만 `-Top` 및 `-PhysicalStatsMinPageCount`로 제한한 큰 index 후보를
`LIMITED` 모드로 읽는다. Query Store가
`READ_WRITE`가 아니거나 `VIEW DEFINITION`/필수 DMV 보고가 빠지면 기본적으로 전체 실행을 실패시키며 명시적 partial
결과는 승인 근거로 쓰지 않는다. DMV 힌트는 현재
index와의 중복, 필터 선택도, 변경 빈도, 저장 공간을 검토하는 후보 자료일 뿐 자동 DDL의 근거로 사용하지 않는다.

SQL Server 마이그레이션 이력은 파일명뿐 아니라 LF 정규화 SHA-256을 저장한다. 적용된 SQL의 내용 drift는
배포를 중단하며, 체크섬이 없던 기존 DB는 백업·승인 소스 대조·staging 복원 리허설 뒤 명시적인 1회 adoption만
허용한다. V142처럼 대량 기존 행을 갱신하는 버전은 보조 index가 있어도 transaction log·lock 비용이 남으므로
운영 규모 데이터의 upgrade rehearsal을 별도 릴리즈 gate로 둔다. V144와 V130~V141의 hot-table index build도
크기·blocking·쓰기 증폭을 같은 기준으로 측정하며, 전환 중 TRACE/POM writer 정지와 edition별 ONLINE/RESUMABLE
가능 여부를 DBA가 승인한다. V142/V144/V146/V147 pending 적용은 이 준비를 완료한 승인 실행에서
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

## 2026-08-28 검증 기록

- Release solution build(`-warnaserror`): 경고 0, 오류 0
- Unit: 1,841/1,841 통과(FDC alarm/config episode 및 runtime key 경계 회귀 포함)
- FDC/Spring focused boot: 18/18 통과(worker 기본 OFF + fail-closed adapter 조립 포함)
- Server/SQLite integration: 881/881 통과
- Portal: 116/116, production build 성공, `npm audit` 취약점 0
- NexaLogic PLC: Unit 12/12, Core 48/48, Integration 14/14, Hardware Simulation 43/43 — 합계 117/117 통과
- modules-ON child-process smoke: 11개 모듈과 호스트 소유 선언형 bridge 43개를 최신 Release 호스트에서 실제 부팅
- migration: V001~V147 strict 이름·숫자 순서·중복·LF 정규화 SHA-256 검증 통과, 신규/증분 SQLite와 MSSQL 정적 계약 통과
- publish: Release publish 성공, 산출물 507개·모듈 11개, 독립 `/health`·JWT 로그인 통과,
  `NexusCom`·`NexusFramework`·`NexusLogic` 파일명/설정 참조 0건
- 정적 경계: QMS/POM 저장소 foreign physical-table SQL 0건(ADR-0002/0003만 허용), Common SQLite bootstrap은
  ADR-0004의 FDC·IVT target whitelist architecture test로 제한, 충돌 marker·diff whitespace 오류 0건

이 실행 환경에는 `NEXAONE_MSSQL_TEST_CONN`, `sqlcmd`, SQL Server 서비스가 없고 Docker daemon도 실행되지
않아 실제 SQL Server 왕복 테스트는 수행하지 못했다. SQL Server 검증과 Cleaner 실제 하드웨어 Recovery HIL은
프레임워크 이관 및 자동 재개 활성화 전 필수 잔여 gate다.
