# 통합 호스트 Phase 3c — 복잡 서비스 얇은 브리지 설계 (대표 슬라이스: EST)

> 상태: 승인(범위=EST 대표 슬라이스 확정) · 작성일 2026-06-20
> 상위: [통합 호스트 설계](2026-06-20-unified-host-design.md) §5·§7 · 결정: [ADR-008](../adr/ADR-008-complex-service-thin-bridge.md) · 격리: [ADR-006](../adr/ADR-006-web-worker-separation.md)

## 1. 목적

게이트웨이(명명쿼리, Dictionary)로 재현 불가능한 **복잡 도메인 서비스**를 통합 호스트가 노출하는 **타입드 인터페이스 브리지** 메커니즘을 **EST 설비상태 1개 슬라이스로 입증**한다. RMS·Lot은 동일 패턴 복제 대상으로 §7에 청사진만 기록한다.

## 2. 왜 EST가 대표 슬라이스인가

`EquipmentStateService.ChangeStateAsync`([src](../../../src/04.Modules/NexaOne.EST/Application/Est/EquipmentStateService.cs))는 게이트웨이-불가의 핵심 3요소를 모두 갖되 단일 애그리거트라 다중 트랜잭션 위험이 없다:
- **낙관적 동시성**: `expectedVersion != current.StateVersion`이면 `Error.Conflict` → HTTP 409. 단일 명명쿼리로는 재시도 의미 전달 불가.
- **매트릭스 기반 전이 검증**: `_matrixRepo.FindAsync(plant, from, to)`의 `AllowFlag`/`RequireReason` → `Error.Failure("EPT.InvalidTransition")` 또는 `Error.Validation("reason")` → HTTP 400.
- **상태+이력 원자 쓰기**: `ChangeStateWithHistoryAsync(current, history)`로 상태 변경과 이력 기록을 한 트랜잭션에 묶음(부분 커밋 방지).

## 3. 계약 어셈블리 `NexaOne.ServiceContracts` (신규, Default-ALC 전용)

`Result<T>`(NexaOne.Common)만 참조. 도메인 엔티티 비노출 — 경량 record DTO로 매핑.

```csharp
// IEquipmentStateBridge — plugin(EST)이 구현, 호스트가 GetBean→캐스트
public interface IEquipmentStateBridge
{
    Task<IReadOnlyList<EquipmentStateMatrixDto>> GetMatrixAsync(string plantId, CancellationToken ct = default);
    Task<IReadOnlyList<EquipmentStateMatrixDto>> GetAllowedTransitionsAsync(string plantId, string fromState, CancellationToken ct = default);
    Task<IReadOnlyList<EquipmentStateDto>> GetEquipmentStatesAsync(string plantId, CancellationToken ct = default);
    Task<Result<EquipmentStateDto>> ChangeStateAsync(string equipmentId, string plantId, string toState,
        string requestedBy, string? reason, string sourceType, int? expectedVersion, CancellationToken ct = default);
    Task<IReadOnlyList<EquipmentStateHistoryDto>> GetHistoryAsync(string equipmentId, int limit = 50, CancellationToken ct = default);
    Task<Result<EquipmentStateMatrixDto>> UpsertMatrixAsync(string plantId, string fromStateId, string toStateId,
        bool allowFlag, string? setStateId, bool requireReason, CancellationToken ct = default);
}
```

DTO(엔티티 컬럼과 1:1, 직렬화 안정 필드만):
- `EquipmentStateDto(EquipmentId, PlantId, CurrentStateId, StateChangedAt, StateVersion)`
- `EquipmentStateMatrixDto(Id, PlantId, FromStateId, ToStateId, AllowFlag, SetStateId, RequireReason, ValidState)`
- `EquipmentStateHistoryDto(HistoryId, EquipmentId, FromState, ToState, SetState, ChangedAt, ChangedBy, Reason, SourceType, DurationSeconds)`

## 4. 모듈 어댑터 (NexaOne.EST)

`EquipmentStateBridge : IEquipmentStateBridge`가 `EquipmentStateService`에 위임하고 도메인→DTO 매핑. est.xml에 `equipmentStateBridge` 빈(ctor ref=`equipmentStateService`) 추가. NexaOne.EST.csproj에 `NexaOne.ServiceContracts` ProjectReference 추가(deps-제외 게시로 Default-ALC 복사본 공유).

## 5. 호스트 배선 (NexaOne.Server)

- csproj: `NexaOne.ServiceContracts` ProjectReference(Default ALC).
- Program.cs: 모듈 컨텍스트 생성(`AddService` 루프) 후, `modulesEnabled`면 `server.GetBean("Est","equipmentStateBridge") as IEquipmentStateBridge ?? throw`로 캐스트해 `AddSingleton<IEquipmentStateBridge>` 등록(fail-fast: 캐스트 실패=ALC 동일성 위반을 기동 시 폭발).
- `EstBridgeController`(`api/v1/est`, `[Authorize]`): `IEquipmentStateBridge` 주입. 라우트/상태코드는 NexaOne.API `EstController`와 동일. 쓰기(change/state-matrix)는 수동 permission 검사(`est:manage` 또는 `*`)로 403 집행(호스트 기존 `QueryGatewayController.HasPermission` 패턴). `requestedBy`는 토큰 NameIdentifier에서 취함(비-부인성).
- `BridgeResultExtensions.ToActionResult`(호스트 로컬): `ControllerResultExtensions`와 동일 매핑(Conflict→409, NotFound→404, Validation/Failure→400, 성공→Ok(value)).

## 6. 검증 전략 (정직한 경계)

- **자동(컨트롤러)**: modules OFF + `ConfigureTestServices`로 가짜 `IEquipmentStateBridge` 주입 → `api/v1/est/*` 호출이 각 `Result`를 200/409/400으로 매핑, 쓰기 권한 미보유 403, 읽기 200을 검증.
- **자동(어댑터 로직)**: `NexaOne.IntegrationTests`에서 실 SQLite 리포로 `EquipmentStateBridge` 직접 구동 — ChangeState Conflict/InvalidTransition/RequireReason/Success + DTO 매핑 검증(Spring/ALC 불필요).
- **수동(ALC 동일성)**: `Server:Modules:Enabled=true` + SQLite로 `dotnet run` 후 `api/v1/est/equipment-state/change` 호출 → `GetBean`→캐스트(타입 동일성)·CurrentUserContext 감사·Result→HTTP를 E2E 확인. WebApplicationFactory가 plugin ALC(`./Modules/*.dll`)를 테스트 작업디렉터리에서 로드 불가하므로 수동 검증(Phase 1 패턴). 자동화 가능하면 시도하되 불가 시 명시.

## 7. 차기 슬라이스 청사진 (이번 미구현)

- **RMS 레시피 승인**(`RecipeService`): `IRecipeApprovalBridge`로 Request/Approve1/Approve2/Release/Reject/CreateNewVersion 노출. 승인자=토큰(비-부인성), Released 불변(파라미터 잠금), 상태위반 409. EST와 동일 패턴.
- **Lot 추적**(`LotTrackingService`): 다중 애그리거트·5+ 순차 트랜잭션·교차모듈 게이트웨이. **선결**: UnitOfWork 원자성(명시적 트랜잭션 또는 ExecuteManyAsync 확장) — 그 전엔 부분 커밋 위험으로 보류.
- **제외**: FDC 실시간 수집(하드웨어/라이브 구독 → 워커 소유 유지, ADR-006), 워크플로 엔진(ADR-006 Phase 4 보류), 순수 CRUD(게이트웨이 흡수).

## 8. 위험

- 타입 동일성 위반(계약 어셈블리가 plugin ALC로 복제 로드) → 부팅 fail-fast로 검출. 빌드 시 모듈 게시가 deps-제외인지 확인.
- ADR-006 격리 침식: 브리지는 계약 어셈블리에 두고 모듈은 웹/SignalR 직접 호출 금지(도메인 이벤트만) 원칙 유지.
- 권한 우회: 쓰기 permission 검사·`requestedBy` 토큰 강제. 본문 입력 신뢰 금지.
- MDM 설비 존재 사전검증 부재(파리티 갭): API `EstController`는 `equipmentService.GetEquipmentAsync`로 미등록 설비를 400 처리하나, 호스트 슬라이스는 EST 상태 의미에 집중하고 이 교차모듈 검증은 연기(SQLite 테스트는 FK off라 무관; MSSQL 운영은 후속 보강). 명시 기록.
