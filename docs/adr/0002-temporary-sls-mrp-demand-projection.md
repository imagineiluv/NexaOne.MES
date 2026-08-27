# SLS 소유 모듈이 생길 때까지 MRP 수요 projection을 한 파일로 격리한다

- 상태: Accepted (temporary exception)
- 결정일: 2026-08-28
- 소유 후보: `NexaOne.SLS`
- 검토 기한: 2026-11-30

## 배경

MRP는 확정·생산 중인 수주의 미납 수량을 입력으로 사용한다. 현재 SLS query와 화면 자산은 존재하지만
업무 모듈과 공유 bridge가 없어 POM이 소유 모듈 계약으로 수요를 받을 수 없다. MDM, IVT, PRC 입력은 이번
변경에서 각 소유 모듈 계약으로 분리한다.

## 결정

읽기 전용 `LegacySalesOrderMrpProjection`만 `SLS_SALES_ORDER`를 참조할 수 있다. POM 계산/원장 구현인
`MrpPlanningRepository`에는 SLS 식별자를 허용하지 않는다. architecture test는 이 정확한 파일-테이블 한 쌍만
allowlist하고, 예외 파일이나 테이블이 늘어나면 실패한다.

이 projection은 SLS 상태를 변경하지 않으며 `Confirmed`/`Producing` 미납 수량만 반환한다. 새 쓰기, join 또는
두 번째 SLS 테이블 참조는 이 결정의 범위가 아니다.

## 위험과 제거 조건

SLS 스키마 변경이 POM 배포에 영향을 줄 수 있다. `NexaOne.SLS`가 생성되어 동일 의미의 versioned
`IMrpDemandSource` bridge와 SQLite/MSSQL 계약 테스트를 제공하면 다음 변경에서 projection과 allowlist를 함께
삭제한다. 검토 기한까지 소유 모듈 작업이 시작되지 않으면 SLS 담당자를 지정하고 새 기한을 ADR에 기록한다.
