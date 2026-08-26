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

OEE의 신규 출력은 EST 표준 output event를 사용해 LOT 없는 캐리어 세척도 같은 방식으로 집계한다. 기존 LOT
실적 fallback과 MDM 설비·작업조·시간대는 Common `IOeeEvidenceSource`가 계획/생산 snapshot으로 제공한다.
현재 production adapter는 호스트 조립 루트에서 MDM/POM을 읽지만 EST와 Takt 구현에는 타 모듈 물리 테이블명이
없다. 이후 POM output event backfill 또는 MDM query/projection으로 adapter를 교체해도 OEE Interface와 계산은
바뀌지 않는다. 실제 SQL Server 및 두 번째 설비 검증 전에는 OEE 구현 자체를 NexaFramework로 이관하지 않는다.

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
연결될 때까지 실제 하드웨어는 fail-closed로 유지한다. 두 번째 설비에서도 재사용성이 입증된 커널만
NexaFramework 이관 후보로 삼는다.

## Spring.NET과 직접 참조 기준

NexaMES 호스트 내부의 새 웹/API 구성은 Microsoft DI를 기본으로 사용한다. Spring.NET의 `CreateServer`와
모듈 XML은 기존 NexaFramework 기반 모듈을 독립 ALC로 로드하고 조립하는 composition boundary로만 유지한다.
컨트롤러는 Spring bean을 직접 탐색하거나 업무 서비스를 상속하지 않고 Common bridge 계약을 DI로 받으며,
호스트 프록시가 필요한 Spring bean 연결을 한곳에서 처리한다. 따라서 XML은 배선과 교체 가능 구현을 담고,
업무 규칙·SQL·설비별 조건은 담지 않는다.

Motion·I/O·Serial·Vision·SECS/GEM은 드라이버로 직접 주입하거나 프로젝트에서 명시적으로 참조한다.
`NexaFramework.Drivers.Hosting`은 여러 드라이버의 발견·수명주기·상태진단을 표준화해야 할 때 쓰는 선택적
편의 계층이며 현재 NexaMES 공통 업무 서비스의 필수 의존으로 추가하지 않는다.

## 프레임워크 이관 게이트

다음 조건을 모두 만족한 계약만 NexaFramework 후보가 된다.

1. NexaMES와 최소 한 개 설비 프로젝트가 같은 계약을 사용한다.
2. 설비별 차이가 플러그인 포트 뒤에 남고 MES 테이블이 계약에 노출되지 않는다.
3. 재시도·프로세스 재시작·동시 실행·반전/취소 테스트가 통과한다.
4. SQL Server와 SQLite에서 동일한 업무 결과를 낸다.
5. 실제 로그인 작업자, correlation/source event와 원본 TRACE를 역추적할 수 있다.

## 2026-08-27 검증 기록

- Release solution build: 오류 0, 기존 NexaFramework `DictionaryExtensions<TKey>` nullability 경고 2
- Unit: 1,588/1,588 통과
- Server/SQLite integration: 770/770 통과
- modules-ON child-process smoke: 10개 모듈과 선언형 bridge 27개를 최신 Release 호스트에서 실제 부팅
- Spring/query XML: 전체 파싱 통과
- migration: V001~V120 버전 중복 없음, 신규 DB와 증분 SQLite 경로 통과
- 정적 경계: IVT의 FDC 및 EST의 MDM/POM 물리 테이블 참조 0건, 충돌 marker·diff whitespace 오류 0건

이 실행 환경에는 `NEXAONE_MSSQL_TEST_CONN`, `sqlcmd`, SQL Server 서비스가 없고 Docker daemon도 실행되지
않아 실제 SQL Server 왕복 테스트는 수행하지 못했다. SQL Server 검증과 Cleaner 실제 하드웨어 Recovery HIL은
프레임워크 이관 및 자동 재개 활성화 전 필수 잔여 gate다.
