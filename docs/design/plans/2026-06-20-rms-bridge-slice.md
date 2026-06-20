# RMS 레시피 승인 얇은 브리지 슬라이스 구현 계획 (ADR-008 복제)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. 체크박스(`- [ ]`) 추적.

**Goal:** EST 슬라이스로 입증한 ADR-008 타입드 얇은 브리지 패턴을 RMS 레시피 승인 서비스(`RecipeService`)에 복제해, 통합 호스트가 6-상태 승인 워크플로(비-부인성·Released 잠금·상태위반 409)를 노출한다.

**Architecture:** 기존 `NexaOne.ServiceContracts`(Default-ALC)에 `IRecipeApprovalBridge` + DTO 추가 → RMS 모듈이 `RecipeBridge` 어댑터로 구현(도메인→DTO, `RecipeApprovalState` enum→string) → 호스트가 `GetBean("Rms","rmsRecipeBridge")`→캐스트→DI 등록(fail-fast) → `RmsBridgeController`(api/v1/rms)가 `Result<T>`를 HTTP로 매핑(기존 `BridgeResultExtensions` 재사용). 결정: [ADR-008](../adr/ADR-008-complex-service-thin-bridge.md), EST 선례: [Phase 3c 설계](../specs/2026-06-20-unified-host-phase3c-bridge-design.md).

**Tech Stack:** EST 슬라이스와 동일. 빌드/테스트 `dotnet ... NexaOne.sln`. 커밋 PowerShell BOM-free 메시지 파일, `git add -A` 금지, push/merge 금지.

**선례 참조(이미 main에 있음):** `src/02.Backend/NexaOne.ServiceContracts/Est/*`(계약 패턴), `src/04.Modules/NexaOne.EST/Application/Est/EquipmentStateBridge.cs`(어댑터 패턴), `src/00.Main/NexaOne.Server/Gateway/EstBridgeController.cs`(컨트롤러 패턴, HasPermission/CurrentUserId 헬퍼), `BridgeResultExtensions.cs`(Result→HTTP, 재사용), `Program.cs`의 EST 브리지 등록 블록(바로 뒤에 RMS 추가), `test/NexaOne.ServerTests/EstBridgeControllerTests.cs` + `test/NexaOne.IntegrationTests/Est/EquipmentStateBridgeIntegrationTests.cs`(테스트 패턴).

---

## Task 1: 계약 — IRecipeApprovalBridge + DTO (NexaOne.ServiceContracts)

**Files:**
- Create: `src/02.Backend/NexaOne.ServiceContracts/Rms/RecipeDtos.cs`
- Create: `src/02.Backend/NexaOne.ServiceContracts/Rms/IRecipeApprovalBridge.cs`

- [ ] **Step 1: DTO** — `src/02.Backend/NexaOne.ServiceContracts/Rms/RecipeDtos.cs`:
```csharp
namespace NexaOne.ServiceContracts.Rms;

// 도메인 엔티티 비노출 경량 DTO. ApprovalState는 enum 비노출 위해 string(enum 이름)으로 표현.
public record RecipeDto(
    string RecipeId, string RecipeName, string Description, string EquipmentClassId,
    int Version, string ApprovalState, string? FirstApproverId, string? SecondApproverId, DateTime? ReleasedAt);

public record RecipeParamDto(
    string ParamId, string RecipeId, string ParamName, string ParamValue, string Unit, int SortOrder);
```

- [ ] **Step 2: 인터페이스** — `src/02.Backend/NexaOne.ServiceContracts/Rms/IRecipeApprovalBridge.cs`:
```csharp
using NexaOne.Common;

namespace NexaOne.ServiceContracts.Rms;

/// <summary>ADR-008 얇은 브리지 — RMS 레시피 승인 상태기계. plugin(RMS)이 구현, 호스트가 GetBean→캐스트로 DI 등록.
/// 상태위반은 Result(Error.Conflict)→409, 검증실패→400, NotFound→404로 매핑된다. 승인/배포자는 토큰 주체(비-부인성).</summary>
public interface IRecipeApprovalBridge
{
    Task<IReadOnlyList<RecipeDto>> GetByEquipmentClassAsync(string equipmentClassId, CancellationToken ct = default);
    Task<IReadOnlyList<RecipeDto>> GetByStateAsync(string state, CancellationToken ct = default);
    Task<Result<RecipeDto>> GetRecipeAsync(string recipeId, CancellationToken ct = default);
    Task<Result<RecipeDto>> CreateRecipeAsync(string recipeId, string name, string desc, string equipmentClassId, CancellationToken ct = default);
    Task<Result> RequestApprovalAsync(string recipeId, CancellationToken ct = default);
    Task<Result> Approve1Async(string recipeId, string approverId, CancellationToken ct = default);
    Task<Result> Approve2Async(string recipeId, string approverId, CancellationToken ct = default);
    Task<Result> ReleaseAsync(string recipeId, string releaserId, CancellationToken ct = default);
    Task<Result> RejectAsync(string recipeId, string reason, CancellationToken ct = default);
    Task<Result<RecipeDto>> CreateNewVersionAsync(string sourceRecipeId, string newRecipeId, CancellationToken ct = default);
    Task<IReadOnlyList<RecipeParamDto>> GetParamsAsync(string recipeId, CancellationToken ct = default);
    Task<Result<RecipeParamDto>> AddParamAsync(string paramId, string recipeId, string paramName, string paramValue, string unit, int sortOrder, CancellationToken ct = default);
    Task<Result> UpdateParamAsync(string paramId, string newValue, CancellationToken ct = default);
    Task<Result> DeleteParamAsync(string paramId, CancellationToken ct = default);
}
```

- [ ] **Step 3: 빌드** — `dotnet build src/02.Backend/NexaOne.ServiceContracts/NexaOne.ServiceContracts.csproj -c Debug` → 0 error/0 warning.

- [ ] **Step 4: Commit**
```powershell
git add src/02.Backend/NexaOne.ServiceContracts/Rms
$m = "feat(contracts): IRecipeApprovalBridge + RecipeDto/RecipeParamDto(ADR-008 RMS 슬라이스)"
$f = [IO.Path]::GetTempFileName(); [IO.File]::WriteAllText($f,$m,(New-Object System.Text.UTF8Encoding($false))); git commit -F $f; Remove-Item $f
```

---

## Task 2: RMS 모듈 어댑터 RecipeBridge + rms.xml + csproj

**Files:**
- Modify: `src/04.Modules/NexaOne.RMS/NexaOne.RMS.csproj`
- Create: `src/04.Modules/NexaOne.RMS/Application/Rms/RecipeBridge.cs`
- Modify: `src/00.Main/NexaOne.Server/Spring/rms.xml`

- [ ] **Step 1: csproj 계약 참조** — `src/04.Modules/NexaOne.RMS/NexaOne.RMS.csproj`의 ProjectReference ItemGroup에 추가:
```xml
    <ProjectReference Include="..\..\02.Backend\NexaOne.ServiceContracts\NexaOne.ServiceContracts.csproj" />
```

- [ ] **Step 2: 어댑터** — `src/04.Modules/NexaOne.RMS/Application/Rms/RecipeBridge.cs`:
```csharp
using NexaOne.Common;
using NexaOne.RMS.Domain;
using NexaOne.ServiceContracts.Rms;

namespace NexaOne.RMS.Application.Rms;

/// <summary>ADR-008 얇은 브리지 어댑터 — RecipeService에 위임하고 도메인 엔티티를 계약 DTO로 매핑한다
/// (RecipeApprovalState enum→string). plugin ALC에서 생성되며 호스트가 IRecipeApprovalBridge로 캐스트해 DI 등록한다.</summary>
public sealed class RecipeBridge : IRecipeApprovalBridge
{
    private readonly RecipeService _service;

    public RecipeBridge(RecipeService service) => _service = service;

    public async Task<IReadOnlyList<RecipeDto>> GetByEquipmentClassAsync(string equipmentClassId, CancellationToken ct = default)
    {
        var r = await _service.GetByEquipmentClassAsync(equipmentClassId, ct);
        return r.IsSuccess ? r.Value.Select(ToDto).ToList() : new List<RecipeDto>();
    }

    public async Task<IReadOnlyList<RecipeDto>> GetByStateAsync(string state, CancellationToken ct = default)
    {
        // 호스트 컨트롤러가 유효 enum일 때만 호출하지만, 방어적으로 파싱 실패 시 빈 목록을 반환한다.
        if (!Enum.TryParse<RecipeApprovalState>(state, out var parsed))
            return new List<RecipeDto>();
        var r = await _service.GetByStateAsync(parsed, ct);
        return r.IsSuccess ? r.Value.Select(ToDto).ToList() : new List<RecipeDto>();
    }

    public async Task<Result<RecipeDto>> GetRecipeAsync(string recipeId, CancellationToken ct = default)
    {
        var r = await _service.GetRecipeAsync(recipeId, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<RecipeDto>(r.Error);
    }

    public async Task<Result<RecipeDto>> CreateRecipeAsync(string recipeId, string name, string desc, string equipmentClassId, CancellationToken ct = default)
    {
        var r = await _service.CreateRecipeAsync(recipeId, name, desc, equipmentClassId, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<RecipeDto>(r.Error);
    }

    public Task<Result> RequestApprovalAsync(string recipeId, CancellationToken ct = default)
        => _service.RequestApprovalAsync(recipeId, ct);

    public Task<Result> Approve1Async(string recipeId, string approverId, CancellationToken ct = default)
        => _service.Approve1Async(recipeId, approverId, ct);

    public Task<Result> Approve2Async(string recipeId, string approverId, CancellationToken ct = default)
        => _service.Approve2Async(recipeId, approverId, ct);

    public Task<Result> ReleaseAsync(string recipeId, string releaserId, CancellationToken ct = default)
        => _service.ReleaseAsync(recipeId, releaserId, ct);

    public Task<Result> RejectAsync(string recipeId, string reason, CancellationToken ct = default)
        => _service.RejectAsync(recipeId, reason, ct);

    public async Task<Result<RecipeDto>> CreateNewVersionAsync(string sourceRecipeId, string newRecipeId, CancellationToken ct = default)
    {
        var r = await _service.CreateNewVersionAsync(sourceRecipeId, newRecipeId, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<RecipeDto>(r.Error);
    }

    public async Task<IReadOnlyList<RecipeParamDto>> GetParamsAsync(string recipeId, CancellationToken ct = default)
        => (await _service.GetParamsAsync(recipeId, ct)).Select(ToDto).ToList();

    public async Task<Result<RecipeParamDto>> AddParamAsync(string paramId, string recipeId, string paramName, string paramValue, string unit, int sortOrder, CancellationToken ct = default)
    {
        var r = await _service.AddParamAsync(paramId, recipeId, paramName, paramValue, unit, sortOrder, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<RecipeParamDto>(r.Error);
    }

    public Task<Result> UpdateParamAsync(string paramId, string newValue, CancellationToken ct = default)
        => _service.UpdateParamAsync(paramId, newValue, ct);

    public Task<Result> DeleteParamAsync(string paramId, CancellationToken ct = default)
        => _service.DeleteParamAsync(paramId, ct);

    private static RecipeDto ToDto(Recipe r)
        => new(r.Id, r.RecipeName, r.Description, r.EquipmentClassId, r.Version,
               r.ApprovalState.ToString(), r.FirstApproverId, r.SecondApproverId, r.ReleasedAt);

    private static RecipeParamDto ToDto(RecipeParam p)
        => new(p.Id, p.RecipeId, p.ParamName, p.ParamValue, p.Unit, p.SortOrder);
}
```

- [ ] **Step 3: rms.xml 빈** — `src/00.Main/NexaOne.Server/Spring/rms.xml`의 `rmsRecipeService` 빈 다음(닫는 `</objects>` 전)에 추가:
```xml
  <!-- ADR-008 얇은 브리지 어댑터 — 호스트가 GetBean("Rms","rmsRecipeBridge")로 IRecipeApprovalBridge 캐스트. -->
  <object id="rmsRecipeBridge" type="NexaOne.RMS.Application.Rms.RecipeBridge, NexaOne.RMS">
    <constructor-arg ref="rmsRecipeService" />
  </object>
```

- [ ] **Step 4: 빌드** — `dotnet build src/04.Modules/NexaOne.RMS/NexaOne.RMS.csproj -c Debug` 후 `dotnet build NexaOne.sln -c Debug` → 0 error/no-new-warning. 그리고 `dotnet test test/NexaOne.IntegrationTests/NexaOne.IntegrationTests.csproj -c Debug` → 회귀 없음(288 통과 +1 skip).

- [ ] **Step 5: Commit**
```powershell
git add src/04.Modules/NexaOne.RMS/NexaOne.RMS.csproj src/04.Modules/NexaOne.RMS/Application/Rms/RecipeBridge.cs src/00.Main/NexaOne.Server/Spring/rms.xml
$m = "feat(rms): RecipeBridge 어댑터(IRecipeApprovalBridge 구현·도메인→DTO·enum→string) + rms.xml 빈(ADR-008)"
$f = [IO.Path]::GetTempFileName(); [IO.File]::WriteAllText($f,$m,(New-Object System.Text.UTF8Encoding($false))); git commit -F $f; Remove-Item $f
```

---

## Task 3: 호스트 배선 — Program 등록 + RmsBridgeController

**Files:**
- Modify: `src/00.Main/NexaOne.Server/Program.cs`
- Create: `src/00.Main/NexaOne.Server/Gateway/RmsBridgeController.cs`

(호스트 csproj는 이미 NexaOne.ServiceContracts 참조. BridgeResultExtensions 재사용. EST 등록 블록 바로 뒤에 RMS 등록만 추가.)

- [ ] **Step 1: Program.cs** — 상단 using에 `using NexaOne.ServiceContracts.Rms;` 추가. EST 브리지 등록(`builder.Services.AddSingleton(equipmentStateBridge);`) **바로 다음 줄**(같은 `if (modulesEnabled)` 블록 내)에 추가:
```csharp
    // ADR-008 얇은 브리지 — RMS 레시피 승인. EST와 동일 메커니즘(GetBean→캐스트→fail-fast 등록).
    var rmsRecipeBridge = server.GetBean("Rms", "rmsRecipeBridge") as IRecipeApprovalBridge
        ?? throw new InvalidOperationException(
            "rmsRecipeBridge 빈을 IRecipeApprovalBridge로 캐스트하지 못했습니다 — "
            + "NexaOne.ServiceContracts ALC 동일성(ADR-008/모듈 게시 deps-제외) 확인.");
    builder.Services.AddSingleton(rmsRecipeBridge);
```

- [ ] **Step 2: RmsBridgeController** — `src/00.Main/NexaOne.Server/Gateway/RmsBridgeController.cs`:
```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Rms;

namespace NexaOne.Server.Gateway;

/// <summary>통합 호스트 RMS 레시피 승인 엔드포인트(ADR-008 얇은 브리지). plugin-ALC RecipeService를
/// IRecipeApprovalBridge로 호출한다. 라우트/상태코드는 NexaOne.API RmsController와 동일. 쓰기는 rms:manage 수동 검사.
/// 승인/배포자는 토큰 주체(비-부인성). (modules ON에서만 IRecipeApprovalBridge가 등록되므로 동작.)</summary>
[ApiController]
[Route("api/v1/rms")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public sealed class RmsBridgeController : ControllerBase
{
    private readonly IRecipeApprovalBridge _bridge;

    public RmsBridgeController(IRecipeApprovalBridge bridge) => _bridge = bridge;

    [HttpGet("recipes")]
    [ProducesResponseType<IReadOnlyList<RecipeDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRecipes([FromQuery] string? equipmentClassId, [FromQuery] string? state, CancellationToken ct)
    {
        // API RmsController와 동일 분기: 유효 state면 상태별 조회, 아니면 설비클래스별 조회.
        if (!string.IsNullOrEmpty(state))
            return Ok(await _bridge.GetByStateAsync(state, ct));
        return Ok(await _bridge.GetByEquipmentClassAsync(equipmentClassId ?? string.Empty, ct));
    }

    [HttpGet("recipes/{recipeId}")]
    [ProducesResponseType<RecipeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRecipe(string recipeId, CancellationToken ct)
        => (await _bridge.GetRecipeAsync(recipeId, ct)).ToActionResult();

    [HttpPost("recipes")]
    [ProducesResponseType<RecipeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateRecipe([FromBody] CreateRecipeRequest req, CancellationToken ct)
    {
        if (!HasPermission(Permissions.RmsManage)) return Forbid();
        return (await _bridge.CreateRecipeAsync(req.RecipeId, req.Name, req.Description, req.EquipmentClassId, ct)).ToActionResult();
    }

    [HttpPut("recipes/{recipeId}/request-approval")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RequestApproval(string recipeId, CancellationToken ct)
    {
        if (!HasPermission(Permissions.RmsManage)) return Forbid();
        return (await _bridge.RequestApprovalAsync(recipeId, ct)).ToActionResult();
    }

    [HttpPut("recipes/{recipeId}/approve1")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Approve1(string recipeId, CancellationToken ct)
    {
        if (!HasPermission(Permissions.RmsManage)) return Forbid();
        return (await _bridge.Approve1Async(recipeId, CurrentUserId, ct)).ToActionResult();
    }

    [HttpPut("recipes/{recipeId}/approve2")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Approve2(string recipeId, CancellationToken ct)
    {
        if (!HasPermission(Permissions.RmsManage)) return Forbid();
        return (await _bridge.Approve2Async(recipeId, CurrentUserId, ct)).ToActionResult();
    }

    [HttpPut("recipes/{recipeId}/release")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Release(string recipeId, CancellationToken ct)
    {
        if (!HasPermission(Permissions.RmsManage)) return Forbid();
        return (await _bridge.ReleaseAsync(recipeId, CurrentUserId, ct)).ToActionResult();
    }

    [HttpPut("recipes/{recipeId}/reject")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Reject(string recipeId, [FromBody] RejectRequest req, CancellationToken ct)
    {
        if (!HasPermission(Permissions.RmsManage)) return Forbid();
        return (await _bridge.RejectAsync(recipeId, req.Reason, ct)).ToActionResult();
    }

    [HttpPost("recipes/{recipeId}/new-version")]
    [ProducesResponseType<RecipeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateNewVersion(string recipeId, [FromBody] NewVersionRequest req, CancellationToken ct)
    {
        if (!HasPermission(Permissions.RmsManage)) return Forbid();
        return (await _bridge.CreateNewVersionAsync(recipeId, req.NewRecipeId, ct)).ToActionResult();
    }

    [HttpGet("recipes/{recipeId}/params")]
    [ProducesResponseType<IReadOnlyList<RecipeParamDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetParams(string recipeId, CancellationToken ct)
        => Ok(await _bridge.GetParamsAsync(recipeId, ct));

    [HttpPost("recipes/{recipeId}/params")]
    [ProducesResponseType<RecipeParamDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> AddParam(string recipeId, [FromBody] AddParamRequest req, CancellationToken ct)
    {
        if (!HasPermission(Permissions.RmsManage)) return Forbid();
        return (await _bridge.AddParamAsync(req.ParamId, recipeId, req.ParamName, req.ParamValue, req.Unit, req.SortOrder, ct)).ToActionResult();
    }

    [HttpPut("recipes/params/{paramId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateParam(string paramId, [FromBody] UpdateParamRequest req, CancellationToken ct)
    {
        if (!HasPermission(Permissions.RmsManage)) return Forbid();
        return (await _bridge.UpdateParamAsync(paramId, req.NewValue, ct)).ToActionResult();
    }

    [HttpDelete("recipes/params/{paramId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DeleteParam(string paramId, CancellationToken ct)
    {
        if (!HasPermission(Permissions.RmsManage)) return Forbid();
        return (await _bridge.DeleteParamAsync(paramId, ct)).ToActionResult();
    }

    private string CurrentUserId =>
        User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
        ?? User.Identity?.Name ?? "SYSTEM";

    private bool HasPermission(string permission) =>
        User.FindAll(Permissions.ClaimType)
            .Any(c => c.Value == Permissions.All || string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));
}

public record CreateRecipeRequest(string RecipeId, string Name, string Description, string EquipmentClassId);
public record RejectRequest(string Reason);
public record NewVersionRequest(string NewRecipeId);
public record AddParamRequest(string ParamId, string ParamName, string ParamValue, string Unit, int SortOrder);
public record UpdateParamRequest(string NewValue);
```
NOTE: 호스트 Gateway 네임스페이스에 `RejectRequest`/`NewVersionRequest` 등 이름이 이미 있는지 확인하라(EstBridgeController는 `ChangeStateRequest`/`UpsertMatrixRequest`만 정의 — 충돌 없음). RMS 요청 레코드 5개는 신규다. `Permissions.RmsManage`("rms:manage") 존재 확인.

- [ ] **Step 3: 빌드 + ServerTests** — `dotnet build NexaOne.sln -c Debug` (0 error/no-new-warning), `dotnet test test/NexaOne.ServerTests/NexaOne.ServerTests.csproj -c Debug` (기존 25 통과 — RMS 컨트롤러는 modules OFF에서 미주입이라 기존 테스트 불변).

- [ ] **Step 4: Commit**
```powershell
git add src/00.Main/NexaOne.Server/Program.cs src/00.Main/NexaOne.Server/Gateway/RmsBridgeController.cs
$m = "feat(server): RMS 얇은 브리지 배선(GetBean→IRecipeApprovalBridge) + RmsBridgeController(ADR-008)"
$f = [IO.Path]::GetTempFileName(); [IO.File]::WriteAllText($f,$m,(New-Object System.Text.UTF8Encoding($false))); git commit -F $f; Remove-Item $f
```

---

## Task 4: 테스트 — 컨트롤러 HTTP(가짜) + 어댑터 로직(SQLite)

**Files:**
- Create: `test/NexaOne.ServerTests/RmsBridgeControllerTests.cs`
- Create: `test/NexaOne.IntegrationTests/Rms/RecipeBridgeIntegrationTests.cs`

- [ ] **Step 1: 컨트롤러 HTTP 테스트** — `EstBridgeControllerTests.cs`를 본으로(같은 BridgeFactory 패턴: modules OFF + SQLite + JWT + `ConfigureTestServices`로 가짜 `IRecipeApprovalBridge` 주입; `using Microsoft.AspNetCore.TestHost;` 포함). 가짜는 recipeId 센티넬로 결과를 결정: `"__conflict__"`→`Result.Failure(Error.Conflict)`, `"__notfound__"`→`Error.NotFound`, `"__validation__"`→`Error.Validation`, else 성공. 검증 Fact:
  - `POST recipes`(rms:manage) 성공 → 200 + RecipeDto.
  - `PUT recipes/__conflict__/approve1`(rms:manage) → 409.
  - `PUT recipes/__notfound__/request-approval`(rms:manage) → 404.
  - `POST recipes`(권한 `fdc:read`) → 403.
  - `GET recipes?state=Draft`(인증만) → 200 + 리스트.
  - `GET recipes/{id}` 성공 → 200 + RecipeDto.
  Fake의 비-제네릭 `Result` 반환 메서드(approve/release/reject/request/update/delete)는 recipeId(또는 paramId) 센티넬로 동일 분기. 성공 void 연산은 `Result.Success()`→ToActionResult→204를 한 건 이상 검증.

- [ ] **Step 2: 어댑터 로직 통합 테스트** — `EquipmentStateBridgeIntegrationTests.cs`의 SQLite 픽스처 패턴을 그대로 본떠(동일 `SqliteSchemaBootstrapper.Apply` + `EesDataSource{SqliteProvider}` + 리포 직접 생성) `new RecipeBridge(new RecipeService(new RecipeRepository(ds, config), new RecipeParamRepository(ds)))`를 실 SQLite로 구동. (생성자 인자: `RecipeRepository(EesDataSource, IConfiguration)`, `RecipeParamRepository(EesDataSource)` — rms.xml 배선과 동일. 정확한 시그니처는 `src/04.Modules/NexaOne.RMS/Infrastructure/*.cs`를 읽어 확인.) 고유 Guid id로 격리. 검증 분기(승인 상태기계 = ADR-008 RMS 핵심):
  1. `CreateRecipeAsync(rid, "n", "d", "EC1")` → IsSuccess, DTO.ApprovalState=="Draft", Version==1.
  2. `RequestApprovalAsync(rid)` → success; 다시 호출 → IsFailure, `Error.Type==Conflict`(Draft 아님).
  3. `Approve1Async(rid, "u1")` → success; `Approve2Async(rid, "u1")`(동일 승인자) → IsFailure `Error.Type==Conflict`(2차≠1차 불변식); `Approve2Async(rid, "u2")` → success.
  4. `ReleaseAsync(rid, "u3")` → success; 이후 `AddParamAsync(pid, rid, ...)` → IsFailure `Error.Type==Conflict`(Released 잠금).
  5. `CreateNewVersionAsync(rid, newRid)` → IsSuccess, DTO.Version==2, ApprovalState=="Draft"(Released만 버전업 가능 — rid는 Released).
  6. `GetRecipeAsync("nope")` → IsFailure `Error.Type==NotFound`.
  7. DTO 매핑: `GetByStateAsync("Released")`가 rid 포함 RecipeDto 반환; `GetParamsAsync` DTO 리스트.
  실 리포 구성자 시그니처가 다르면 `src/04.Modules/NexaOne.RMS/Infrastructure/RecipeRepository.cs`·`RecipeParamRepository.cs`를 읽어 맞춘다(추측 금지).

- [ ] **Step 3: 전체 테스트** — `dotnet build NexaOne.sln -c Debug`; `dotnet test test/NexaOne.ServerTests/...`; `dotnet test test/NexaOne.IntegrationTests/...`. 기대: ServerTests 31(25+6), IntegrationTests 289+(+1 skip). 전부 그린. 실 제품 버그 발견 시 테스트 약화 금지 — BLOCKED 보고.

- [ ] **Step 4: Commit**
```powershell
git add test/NexaOne.ServerTests/RmsBridgeControllerTests.cs test/NexaOne.IntegrationTests/Rms/RecipeBridgeIntegrationTests.cs
$m = "test(server,rms): RMS 얇은 브리지 — 컨트롤러 HTTP 매핑(가짜) + 어댑터 승인 상태기계 SQLite(ADR-008)"
$f = [IO.Path]::GetTempFileName(); [IO.File]::WriteAllText($f,$m,(New-Object System.Text.UTF8Encoding($false))); git commit -F $f; Remove-Item $f
```

---

## Self-Review

**스펙 커버리지:** 계약(14-멤버 인터페이스 + 2 DTO)→T1; 모듈 어댑터+rms.xml→T2; Program 등록+RmsBridgeController(13 엔드포인트, rms:manage·토큰 승인자)→T3; 컨트롤러/어댑터 테스트→T4. ✅
**ADR-008 반영:** 타입드 인터페이스(리플렉션 배제) · deps-제외 ALC 동일성 · 도메인→DTO(enum→string) · 부팅 fail-fast 등록 · 전용 컨트롤러 + rms:manage 수동검사 + 승인자/배포자 토큰(비-부인성) · Result→HTTP(Conflict 409·NotFound 404·기타 400) — EST 슬라이스와 동일. ✅
**타입 일관성:** `IRecipeApprovalBridge` 시그니처가 T1 정의·T2 구현·T3 컨트롤러·T4 가짜에서 동일. `Recipe.{Id,RecipeName,Description,EquipmentClassId,Version,ApprovalState,FirstApproverId,SecondApproverId,ReleasedAt}`, `RecipeParam.{Id,RecipeId,ParamName,ParamValue,Unit,SortOrder}`, `RecipeService` 14메서드 시그니처, `RecipeApprovalState`(Draft/WaitApproval/Approved1/Approved/Released/Rejected), `Permissions.RmsManage="rms:manage"` — 모두 실제 코드 확인값. `Result`(비제네릭) 반환 메서드는 `.ToActionResult()`로 204/오류 매핑.
**미해결:** 어댑터 통합테스트의 RecipeRepository/RecipeParamRepository 생성자 시그니처는 구현자가 Infrastructure 파일을 읽어 확정(rms.xml ctor-arg가 eesDataSource[+appConfiguration]임을 가이드로). RMS도 EST와 동일하게 modules-ON 런타임 ALC 검증은 수동 실행 영역(자동은 컨트롤러·어댑터 레이어로 커버).
