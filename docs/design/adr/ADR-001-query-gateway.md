# ADR-001 — Query Engine 게이트웨이 (모든 데이터 접근이 단일 지점을 통과)

- **Status**: Accepted (채택)
- **Date**: 2026-06-13 (구현현황 갱신 2026-06-15)
- **구현현황**: 구현 완료 — `IQueryGateway`/`DapperQueryGateway`로 전 리포지토리가 게이트 경유, 하드코딩 NOLOCK 0건, `RuleController /query` 백도어는 관리자 전용으로 봉쇄(GapAnalysis §7 Phase 1 참조).
- **관련**: [Frontend-Coexistence-GapAnalysis.md](../Frontend-Coexistence-GapAnalysis.md) §2.4, Phase 1B
- **결정자**: 사용자 승인("모두 진행")

## 컨텍스트

비전은 "모든 데이터 접근이 Query Engine을 통과"한다. 현재:
- NexusCom에 성숙한 추상화(`IQueryExecutor`/`IDriverManager`/`IDatabaseProvider`)가 있으나 NexaMes에서 **호출 0건**.
- 44개 리포지토리가 `QueryRepository`(기반 클래스)를 상속하고, 그 안에서 `IDatabaseProvider.CreateConnection` + **Dapper**(`QueryAsync<T>`/`ExecuteScalarAsync`)로 직접 실행 — 게이트웨이를 한 단계 아래에서 우회.
- SQL이 `const string` 인라인으로 흩어짐. `WITH(NOLOCK)` 109건 하드코딩.
- `RuleController /api/v1/query`가 임의 SQL을 직접 실행하는 백도어.

### 설계 분기 (핵심 쟁점)
`IQueryExecutor`는 `QueryResult`(Columns + Rows 딕셔너리)를 반환해 **Dapper의 타입 매핑을 잃는다.** 모든 리포지토리를 딕셔너리 매핑으로 갈아엎는 것은 비현실적·고위험.

## 결정

**`IQueryGateway`라는 NexaMes 측 단일 데이터 접근 게이트웨이를 도입하고, `QueryRepository`/`ServiceObjectProcessor`가 그 뒤로 위임한다.** 게이트웨이는 내부적으로 **Dapper를 계속 사용**(타입 매핑 보존)하되, 다음을 단일 지점에서 책임진다:

1. **연결 획득** — `IDatabaseProvider.CreateConnection` 호출을 게이트웨이로 집중(리포지토리가 직접 열지 않음).
2. **명명 쿼리 해석** — `IQueryCatalog`(키→SQL). 인라인 `const string`도 계속 허용(점진 이관), 등록된 키는 카탈로그에서 조회.
3. **방언 적용** — `INexaOneEESDbCapability`(NOLOCK/페이징)를 게이트웨이에서 적용 가능하게 노출.
4. **횡단 관심사** — 쿼리 단위 로깅/타이밍(이미 있는 Serilog/OTel와 연동), 향후 정책·타임아웃 훅 지점.

즉 게이트웨이는 "실행 메커니즘 교체(Dapper→IQueryExecutor)"가 아니라 **"단일 진입점·명명 쿼리·관측의 chokepoint"**로 정의한다. 이는 비전의 "모든 접근이 통과"를 **리포지토리 시그니처 변경 없이** 달성한다.

## 접근(구현 범위)

- 신규: `IQueryGateway`(QueryAsync<T>/QueryFirstOrDefaultAsync<T>/ExecuteScalarAsync<T>/ExecuteAsync + ExecuteNamedAsync) + `DapperQueryGateway` 구현, `IQueryCatalog`/`InMemoryQueryCatalog`.
- `QueryRepository`/`ServiceObjectProcessor`의 **내부만** 게이트웨이 위임으로 교체 → 44개 리포지토리·전 화면 무변경.
- 게이트웨이 + 카탈로그 + 위임 + 단위 테스트. *(구현현황: 완료. 인라인 SQL의 카탈로그 이관과 NOLOCK 방언 치환도 반영 — 현재 `src` 내 하드코딩 NOLOCK 0건, 방언은 `INexaOneEESDbCapability`로 추상화.)*
- `RuleController /query` 백도어는 게이트웨이 경유 또는 폐쇄. *(구현현황: 완료 — `[Authorize(Policy="perm:sys:manage")]`로 관리자 전용 봉쇄, 데이터 접근은 등록 명명쿼리 `/query/{queryId}`·`/command/{queryId}` 게이트 경유.)*

## 결과

- **장점**: 단일 chokepoint 확보(로깅/정책/명명쿼리), Dapper 타입 매핑 유지, 무중단·가역, 점진 이관.
- **비용/위험**: `QueryRepository` 단일 진입점 내부 교체 → 회귀는 단위 테스트로 가드. 게이트웨이가 여전히 Dapper라 "공급자 중립 실행"은 부분적(방언 추상화로 보완).
- **비채택**: IQueryExecutor 전면 대체(타입 매핑 상실·고위험), 빅뱅 SQL 카탈로그 이관(범위 과대).
