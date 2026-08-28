# SQLite 증분 부트스트랩의 모듈 업무 규칙 소유를 한시 허용한다

- 상태: Accepted (temporary ownership exception)
- 결정일: 2026-08-28
- 소유자: FDC·IVT / Server composition
- 검토 기한: 2026-11-30

## 배경과 결정

기존 SQLite 배포는 업무 모듈을 Spring.NET으로 조립하기 전에
`NexaOne.Common/Infrastructure/Persistence/SqliteSchemaInitializer.cs` 하나가 빈 DB와 구버전 DB를 모두
증분 복구한다. 이번 변경의 FDC effect lifecycle·GLOBAL runtime ownership fence와 IVT TRACE cursor는
열 추가뿐 아니라 기존 행 backfill/단일 seed, canonical trigger 교체, durable reconciliation marker를 한
`BEGIN IMMEDIATE` 경계에서 처리해야 한다.
현재 부팅 순서에는 모듈 소유 schema contribution을 수집하는 seam이 없어 이를 즉시 이동하면 기존 DB의
원자적 upgrade 경로가 사라진다.

따라서 다음 정확한 예외만 한시 허용한다.

- source: `SqliteSchemaInitializer.cs`
- target: `FDC_INTERLOCK_HISTORY`의 V146 lifecycle evidence,
`FDC_RUNTIME_OWNERSHIP`의 V149 단일 GLOBAL seed·monotonic fence·lease secret hash/DB-time guard,
`FDC_TRACE_RETENTION_STATE`와 `FDC_COLLECT_DATA`의 V150 completeness seed·단조 경계·late INSERT/UPDATE/DELETE/
`INSERT OR REPLACE` guard·invalid timestamp partial index와
  `IVT_TRACE_PROJECTION_INBOX`/`IVT_TRACE_INGESTION_CURSOR`의 V142·V147 reconciliation
- 허용 동작: migration asset 적용, legacy backfill, 불일치 검증, canonical trigger와 reconciliation marker 설치
- 금지 동작: FDC action 판단, TRACE 소비량 계산, 다른 모듈 서비스 호출, 런타임 업무 상태 전이

MSSQL 원본 migration과 SQLite 증분 구현이 어긋날 위험은 fresh/incremental schema contract test와
trigger 우회 회귀 테스트로 제한한다. 이 예외는 새 업무 schema를 Common에 추가하는 선례가 아니다.

## 제거 조건

Server가 모듈을 로드하기 전 다음을 만족하는 module-owned SQLite schema contribution을 수집할 수 있게 되면
이 예외를 제거한다.

1. 각 모듈 `Resources/`의 migration/reconciliation asset이 stable ID와 checksum을 제공한다.
2. 호스트의 generic runner가 contribution을 dependency 순서로 한 connection/transaction에서 실행한다.
3. FDC·IVT가 위 객체의 seed·backfill·trigger·marker를 소유하고 Common은 업무 테이블명을 알지 않는다.
4. 기존 pre-V142/pre-V146/pre-V149/pre-V150 DB upgrade와 crash 재기동 회귀가 그대로 통과한다.

이 분리는 NexaFramework 이관과 Production release 승인 전에 재검토하며, 검토 기한 연장은 새 ADR 없이는
허용하지 않는다.
