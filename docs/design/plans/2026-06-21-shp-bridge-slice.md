# 모듈별 API 확장 대표 슬라이스 — SHP 얇은 브리지 (ADR-008)

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. 체크박스 단계.

**Goal:** "각 모듈이 자기 API를 소유"를 plugin ALC(ADR-006)를 깨지 않고 확장하는 대표 슬라이스로, SHP(출하)의 DeliveryOrder 생명주기를 EST/RMS와 동일한 얇은 브리지(ADR-008)로 노출한다 — 모듈이 계약 어댑터를 구현하고 호스트는 얇은 컨트롤러만 둔다.

**Architecture:** EST/RMS 브리지를 1:1 모방. `NexaOne.ServiceContracts.Shp`(Default-ALC 공유 계약)에 `IShipmentBridge`+`DeliveryOrderDto`. 모듈 `ShipmentBridge : IShipmentBridge`가 `ShpService`에 위임하고 도메인→DTO 매핑(enum→string). 호스트가 `GetBean("Shp","shipmentBridge")`→캐스트→`AddSingleton`(fail-fast). 얇은 `ShpBridgeController`(api/v1/shp)가 `Result`→HTTP(BridgeResultExtensions: Conflict→409, NotFound→404, else→400). 쓰기는 `shp:manage` 수동 검사.

**Tech Stack:** C#/.NET 8, Spring.NET plugin ALC, xUnit(modules-OFF + FakeBridge, EstBridgeControllerTests 패턴).

---

## 검증된 사실 (직접 확인, 2026-06-21)

- **ShpService**([ShpService.cs](../../../src/04.Modules/NexaOne.SHP/Application/Shp/ShpService.cs)): `GetByPlantAsync(plantId, from?, to?)→Result<IReadOnlyList<DeliveryOrder>>`, `CreateOrderAsync(orderId, customerName, plantId, requestedDate)→Result<DeliveryOrder>`, `ConfirmOrderAsync/ShipOrderAsync(shippedDate)/CancelOrderAsync(orderId)→Result`. 단일 애그리거트(DeliveryOrder), 다중 트랜잭션 없음(POM Lot 같은 UoW 차단요인 無).
- **DeliveryOrder**([DeliveryOrder.cs](../../../src/04.Modules/NexaOne.SHP/Domain/DeliveryOrder.cs)): 속성 Id/CustomerName/PlantId/RequestedDate/ShippedDate?/Status(enum Draft/Confirmed/Shipped/Cancelled)/Remark/TotalQty. 상태기계 Confirm(Draft→Confirmed)/Ship(Confirmed→Shipped)/Cancel(≠Shipped·Cancelled→Cancelled), 위반 시 `Error.Conflict`. Restore(읽기경로 무손실).
- **shp.xml**([shp.xml](../../../src/00.Main/NexaOne.Server/Spring/shp.xml)): `shpService` 빈 존재(ref deliveryOrderRepository/deliveryItemRepository/shipmentHistoryRepository). 브리지 빈 추가 위치.
- **Permissions.ShpManage** = "shp:manage" 실재([Permissions.cs](../../../src/02.Backend/NexaOne.Common/Security/Permissions.cs)). `All`("*")·`ClaimType`("permission")도.
- **미러 대상**: [IEquipmentStateBridge.cs](../../../src/02.Backend/NexaOne.ServiceContracts/Est/IEquipmentStateBridge.cs)+[EquipmentStateDtos.cs](../../../src/02.Backend/NexaOne.ServiceContracts/Est/EquipmentStateDtos.cs), [EquipmentStateBridge.cs](../../../src/04.Modules/NexaOne.EST/Application/Est/EquipmentStateBridge.cs)(어댑터·ToDto), [EstBridgeController.cs](../../../src/00.Main/NexaOne.Server/Gateway/EstBridgeController.cs)(HasPermission/CurrentUserId/ToActionResult), [est.xml:28-31](../../../src/00.Main/NexaOne.Server/Spring/est.xml#L28-L31)(브리지 빈), [Program.cs:78-91](../../../src/00.Main/NexaOne.Server/Program.cs#L78-L91)(EST/RMS GetBean→캐스트→AddSingleton), [BridgeResultExtensions.cs](../../../src/00.Main/NexaOne.Server/Gateway/BridgeResultExtensions.cs).
- **SHP csproj**([NexaOne.SHP.csproj](../../../src/04.Modules/NexaOne.SHP/NexaOne.SHP.csproj)): Common/Application/Infrastructure만 참조 — ServiceContracts 참조 추가 필요(EST/RMS는 이미 보유).
- **테스트 패턴**: [EstBridgeControllerTests.cs](../../../test/NexaOne.ServerTests/EstBridgeControllerTests.cs) — modules-OFF, `ConfigureTestServices(AddSingleton<IEquipmentStateBridge>(new FakeBridge()))`로 Spring/ALC 없이 HTTP 매핑·권한 결정적 검증.

## File Structure
- 생성: `src/02.Backend/NexaOne.ServiceContracts/Shp/IShipmentBridge.cs`, `.../Shp/ShipmentDtos.cs`.
- 생성: `src/04.Modules/NexaOne.SHP/Application/Shp/ShipmentBridge.cs`.
- 수정: `src/04.Modules/NexaOne.SHP/NexaOne.SHP.csproj`(ServiceContracts ProjectReference).
- 수정: `src/00.Main/NexaOne.Server/Spring/shp.xml`(shipmentBridge 빈).
- 수정: `src/00.Main/NexaOne.Server/Program.cs`(using + GetBean→캐스트→AddSingleton).
- 생성: `src/00.Main/NexaOne.Server/Gateway/ShpBridgeController.cs`.
- 생성: `test/NexaOne.ServerTests/ShpBridgeControllerTests.cs`.

---

## Task 1: 계약 + 어댑터 + 배선 + 컨트롤러

- [ ] **Step 1: ServiceContracts/Shp/ShipmentDtos.cs**
```csharp
namespace NexaOne.ServiceContracts.Shp;

// 도메인 엔티티를 직렬화 계약으로 노출하지 않는 경량 DTO(ALC/버전 결합 차단). Status는 enum→string.
public record DeliveryOrderDto(
    string OrderId, string CustomerName, string PlantId, DateTime RequestedDate,
    DateTime? ShippedDate, string Status, decimal TotalQty);
```
- [ ] **Step 2: ServiceContracts/Shp/IShipmentBridge.cs**
```csharp
using NexaOne.Common;

namespace NexaOne.ServiceContracts.Shp;

/// <summary>복잡 서비스 얇은 브리지(ADR-008) — SHP 출하주문 생명주기. plugin(SHP)이 구현하고 호스트가 GetBean→캐스트로
/// Default-ALC DI에 등록한다. Result로 상태전이 분기(Conflict/Validation/Success)를 손실 없이 전달한다.</summary>
public interface IShipmentBridge
{
    Task<IReadOnlyList<DeliveryOrderDto>> GetOrdersByPlantAsync(string plantId, DateTime? from, DateTime? to, CancellationToken ct = default);
    Task<Result<DeliveryOrderDto>> CreateOrderAsync(string orderId, string customerName, string plantId, DateTime requestedDate, CancellationToken ct = default);
    Task<Result> ConfirmOrderAsync(string orderId, CancellationToken ct = default);
    Task<Result> ShipOrderAsync(string orderId, DateTime shippedDate, CancellationToken ct = default);
    Task<Result> CancelOrderAsync(string orderId, CancellationToken ct = default);
}
```
- [ ] **Step 3: NexaOne.SHP.csproj — ServiceContracts 참조 추가** (Infrastructure 참조 다음 줄):
```xml
    <ProjectReference Include="..\..\02.Backend\NexaOne.ServiceContracts\NexaOne.ServiceContracts.csproj" />
```
- [ ] **Step 4: NexaOne.SHP/Application/Shp/ShipmentBridge.cs**
```csharp
using NexaOne.Common;
using NexaOne.ServiceContracts.Shp;
using NexaOne.SHP.Domain;

namespace NexaOne.SHP.Application.Shp;

/// <summary>ADR-008 얇은 브리지 어댑터 — ShpService에 위임하고 DeliveryOrder를 계약 DTO로 매핑(Status enum→string).
/// plugin ALC에서 생성되며 호스트(Default ALC)가 IShipmentBridge로 캐스트해 DI에 등록한다.</summary>
public sealed class ShipmentBridge : IShipmentBridge
{
    private readonly ShpService _service;
    public ShipmentBridge(ShpService service) => _service = service;

    public async Task<IReadOnlyList<DeliveryOrderDto>> GetOrdersByPlantAsync(
        string plantId, DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        var r = await _service.GetByPlantAsync(plantId, from, to, ct);
        return r.IsSuccess ? r.Value.Select(ToDto).ToList() : new List<DeliveryOrderDto>();
    }

    public async Task<Result<DeliveryOrderDto>> CreateOrderAsync(
        string orderId, string customerName, string plantId, DateTime requestedDate, CancellationToken ct = default)
    {
        var r = await _service.CreateOrderAsync(orderId, customerName, plantId, requestedDate, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<DeliveryOrderDto>(r.Error);
    }

    public Task<Result> ConfirmOrderAsync(string orderId, CancellationToken ct = default) => _service.ConfirmOrderAsync(orderId, ct);
    public Task<Result> ShipOrderAsync(string orderId, DateTime shippedDate, CancellationToken ct = default) => _service.ShipOrderAsync(orderId, shippedDate, ct);
    public Task<Result> CancelOrderAsync(string orderId, CancellationToken ct = default) => _service.CancelOrderAsync(orderId, ct);

    private static DeliveryOrderDto ToDto(DeliveryOrder o)
        => new(o.Id, o.CustomerName, o.PlantId, o.RequestedDate, o.ShippedDate, o.Status.ToString(), o.TotalQty);
}
```
- [ ] **Step 5: shp.xml — 브리지 빈 추가** (`</objects>` 직전):
```xml
  <!-- ADR-008 얇은 브리지 어댑터 — 호스트가 GetBean("Shp","shipmentBridge")로 IShipmentBridge 캐스트. -->
  <object id="shipmentBridge" type="NexaOne.SHP.Application.Shp.ShipmentBridge, NexaOne.SHP">
    <constructor-arg ref="shpService" />
  </object>
```
- [ ] **Step 6: Program.cs — using + 등록** (RMS 브리지 등록 다음, `if(modulesEnabled)` 블록 내 [Program.cs:91](../../../src/00.Main/NexaOne.Server/Program.cs#L91) 뒤). using에 `using NexaOne.ServiceContracts.Shp;` 추가([Program.cs:15](../../../src/00.Main/NexaOne.Server/Program.cs#L15) 근처):
```csharp
    // ADR-008 얇은 브리지 — SHP 출하주문 생명주기. EST/RMS와 동일 메커니즘(GetBean→캐스트→fail-fast 등록).
    var shipmentBridge = server.GetBean("Shp", "shipmentBridge") as IShipmentBridge
        ?? throw new InvalidOperationException(
            "shipmentBridge 빈을 IShipmentBridge로 캐스트하지 못했습니다 — "
            + "NexaOne.ServiceContracts ALC 동일성(ADR-008/모듈 게시 deps-제외) 확인.");
    builder.Services.AddSingleton(shipmentBridge);
```
- [ ] **Step 7: ShpBridgeController.cs** (`src/00.Main/NexaOne.Server/Gateway/`)
```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Shp;

namespace NexaOne.Server.Gateway;

/// <summary>통합 호스트 SHP 출하 엔드포인트(ADR-008 얇은 브리지). plugin-ALC ShpService를 IShipmentBridge로 호출한다.
/// 쓰기(생성/확정/출하/취소)는 shp:manage 수동 검사. Result→HTTP(BridgeResultExtensions). (modules ON에서만 동작.)</summary>
[ApiController]
[Route("api/v1/shp")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public sealed class ShpBridgeController : ControllerBase
{
    private readonly IShipmentBridge _bridge;
    public ShpBridgeController(IShipmentBridge bridge) => _bridge = bridge;

    [HttpGet("orders")]
    [ProducesResponseType<IReadOnlyList<DeliveryOrderDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetOrders(
        [FromQuery] string plantId, [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
        => Ok(await _bridge.GetOrdersByPlantAsync(plantId, from, to, ct));

    [HttpPost("orders")]
    [ProducesResponseType<DeliveryOrderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateOrder([FromBody] CreateDeliveryOrderRequest req, CancellationToken ct)
    {
        if (!HasPermission(Permissions.ShpManage)) return Forbid();
        return (await _bridge.CreateOrderAsync(req.OrderId, req.CustomerName, req.PlantId, req.RequestedDate, ct)).ToActionResult();
    }

    [HttpPost("orders/{orderId}/confirm")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ConfirmOrder(string orderId, CancellationToken ct)
    {
        if (!HasPermission(Permissions.ShpManage)) return Forbid();
        return (await _bridge.ConfirmOrderAsync(orderId, ct)).ToActionResult();
    }

    [HttpPost("orders/{orderId}/ship")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ShipOrder(string orderId, [FromBody] ShipDeliveryOrderRequest req, CancellationToken ct)
    {
        if (!HasPermission(Permissions.ShpManage)) return Forbid();
        return (await _bridge.ShipOrderAsync(orderId, req.ShippedDate, ct)).ToActionResult();
    }

    [HttpPost("orders/{orderId}/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CancelOrder(string orderId, CancellationToken ct)
    {
        if (!HasPermission(Permissions.ShpManage)) return Forbid();
        return (await _bridge.CancelOrderAsync(orderId, ct)).ToActionResult();
    }

    private bool HasPermission(string permission) =>
        User.FindAll(Permissions.ClaimType)
            .Any(c => c.Value == Permissions.All || string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));
}

public record CreateDeliveryOrderRequest(string OrderId, string CustomerName, string PlantId, DateTime RequestedDate);
public record ShipDeliveryOrderRequest(DateTime ShippedDate);
```
- [ ] **Step 8: 빌드** `dotnet build NexaOne.sln -c Debug --nologo` → 0 errors.
- [ ] **Step 9: 커밋** `feat(shp): ADR-008 얇은 브리지 — SHP 출하주문 생명주기(api/v1/shp), 모듈별 API 소유 확장`.

---

## Task 2: ShpBridgeController E2E (modules-OFF + FakeBridge)

- [ ] **Step 1: test/NexaOne.ServerTests/ShpBridgeControllerTests.cs** — EstBridgeControllerTests 팩토리/JWT 패턴 복제(modules-OFF, SQLite, RateLimiting off, `ConfigureTestServices(s => s.AddSingleton<IShipmentBridge>(new FakeBridge()))`). `FakeBridge : IShipmentBridge`로:
  - GetOrdersByPlant → 1건 DTO 반환.
  - CreateOrder → Result.Success(DTO).
  - ConfirmOrder → Result.Success().
  - ShipOrder → 인자 orderId가 "CONFLICT"면 Result.Failure(Error.Conflict(...)), else Success.
  - CancelOrder → Success.
  검증:
  1. **읽기 200**: GET `/api/v1/shp/orders?plantId=P1`(권한 무관 인증 토큰) → 200, 1건.
  2. **쓰기 권한**: shp:manage 없는 토큰으로 POST `/orders` → 403; shp:manage 토큰 → 200(DTO).
  3. **상태전이 200/409**: shp:manage 토큰으로 POST `/orders/OK/ship`(body {shippedDate}) → 204; `/orders/CONFLICT/ship` → 409(Result.Failure Conflict→409 매핑 검증).
  4. confirm/cancel → 204.
  JWT 민팅은 EstBridgeControllerTests 헬퍼(permission 클레임 부여, 팩토리 Jwt:SecretKey/Issuer/Audience 일치) 동형.
- [ ] **Step 2: 테스트 실행** `dotnet test test/NexaOne.ServerTests/NexaOne.ServerTests.csproj -c Debug --nologo` → 기존(71) + 신규 전부 통과.
- [ ] **Step 3: 커밋** `test(shp): SHP 브리지 컨트롤러 E2E(읽기·권한·상태전이 409, FakeBridge)`.

---

## Task 3 (컨트롤러): 회귀 + 리뷰 + ff-merge
- 전체 sln 빌드 0 errors + ServerTests 녹색 재확인. ADR-008 부합(계약 어셈블리 공유·DTO 매핑·fail-fast 등록·권한 게이트) 리뷰 후 main ff-merge(sln 가드, git `2>&1` 금지, push 안 함).
- 선택: ADR-008 "확장" 목록에 SHP 추가(문서 갱신)·메모리 후속 갱신.

## Self-Review
- 패턴 정합: EST/RMS 브리지를 1:1 모방(계약/어댑터/빈/등록/컨트롤러/매핑). plugin ALC·MVC-단순성 보존(ADR-006/008). ✓
- 모듈 API 소유: 계약 구현·도메인 매핑은 SHP 모듈 소유, 호스트는 얇은 컨트롤러만. ✓
- 타입 동일성: ServiceContracts는 Default-ALC 공유, SHP는 deps-제외 게시 → GetBean 캐스트 fail-fast. ✓
- 검증 한계: plugin ALC 실로드는 수동 기동(ADR-008 관례) — 자동 테스트는 modules-OFF+FakeBridge로 HTTP 매핑·권한·Result 분기. (modules-ON 부팅은 Phase 6 HostModulesBootSmokeTests가 9서비스+브리지 캐스트를 이미 커버 — shipmentBridge 추가로 그 부팅에 포함됨.)
- 범위: DeliveryOrder 생명주기(대표 복잡 슬라이스). 품목/이력/SPC는 후속(게이트웨이 명명쿼리 또는 브리지 확장).
