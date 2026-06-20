# 통합 호스트 Phase 3c — 복잡 서비스 얇은 브리지(EST 슬라이스) 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox(`- [ ]`).

**Goal:** 통합 호스트가 plugin-ALC EST 서비스를 공유 계약 인터페이스로 노출하는 타입드 얇은 브리지를 구현해 `Result<T>`(Conflict/InvalidTransition/Validation/Success)를 HTTP로 충실히 매핑한다.

**Architecture:** 신규 `NexaOne.ServiceContracts`(Default-ALC) 계약 어셈블리 → EST 모듈이 `EquipmentStateBridge` 어댑터로 구현(도메인→DTO 매핑) → 호스트가 `GetBean`→캐스트로 DI 등록(fail-fast) → `EstBridgeController`가 주입받아 HTTP 매핑. 결정: [ADR-008](../adr/ADR-008-complex-service-thin-bridge.md), 설계: [Phase 3c 설계](../specs/2026-06-20-unified-host-phase3c-bridge-design.md).

**Tech Stack:** C#/.NET 8, Spring.NET ApplicationServer(plugin ALC), ASP.NET Core 컨트롤러, `NexaOne.Common.Result<T>`, xunit + WebApplicationFactory + SQLite. 빌드/테스트: `dotnet ... NexaOne.sln`. 커밋: PowerShell BOM-free 메시지 파일(`[IO.File]::WriteAllText($f,$m,(New-Object System.Text.UTF8Encoding($false)))` → `git commit -F $f`), `git add -A` 금지(submodules/NexusLogic dirty), push/merge 금지.

---

## Task 1: NexaOne.ServiceContracts 계약 어셈블리 + IEquipmentStateBridge + DTO

**Files:**
- Create: `src/02.Backend/NexaOne.ServiceContracts/NexaOne.ServiceContracts.csproj`
- Create: `src/02.Backend/NexaOne.ServiceContracts/Est/IEquipmentStateBridge.cs`
- Create: `src/02.Backend/NexaOne.ServiceContracts/Est/EquipmentStateDtos.cs`
- Modify: `NexaOne.sln`

- [ ] **Step 1: csproj**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <!-- Result<T>/Error 재사용. 계약은 Default-ALC 전용; plugin은 참조만(모듈 게시가 deps-제외라 Default ALC 복사본 공유). -->
    <ProjectReference Include="..\NexaOne.Common\NexaOne.Common.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: DTO**

`src/02.Backend/NexaOne.ServiceContracts/Est/EquipmentStateDtos.cs`:
```csharp
namespace NexaOne.ServiceContracts.Est;

// 도메인 엔티티를 직렬화 계약으로 노출하지 않기 위한 경량 DTO(ALC/버전 결합 차단). 엔티티 컬럼과 1:1.
public record EquipmentStateDto(
    string EquipmentId, string PlantId, string CurrentStateId, DateTime StateChangedAt, int StateVersion);

public record EquipmentStateMatrixDto(
    string Id, string PlantId, string FromStateId, string ToStateId,
    bool AllowFlag, string SetStateId, bool RequireReason, string ValidState);

public record EquipmentStateHistoryDto(
    string HistoryId, string EquipmentId, string FromState, string ToState, string SetState,
    DateTime ChangedAt, string ChangedBy, string Reason, string SourceType, long? DurationSeconds);
```

- [ ] **Step 3: 인터페이스**

`src/02.Backend/NexaOne.ServiceContracts/Est/IEquipmentStateBridge.cs`:
```csharp
using NexaOne.Common;

namespace NexaOne.ServiceContracts.Est;

/// <summary>복잡 서비스 얇은 브리지(ADR-008) — EST 설비상태. plugin(EST)이 구현하고 호스트가 GetBean→캐스트로
/// Default-ALC DI에 등록한다. Result&lt;T&gt;로 도메인 분기(Conflict/InvalidTransition/Validation/Success)를
/// 손실 없이 전달해 컨트롤러가 409/400/200으로 매핑한다.</summary>
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

- [ ] **Step 4: 솔루션 추가 + 빌드**
```powershell
dotnet sln NexaOne.sln add src/02.Backend/NexaOne.ServiceContracts/NexaOne.ServiceContracts.csproj
dotnet build src/02.Backend/NexaOne.ServiceContracts/NexaOne.ServiceContracts.csproj -c Debug
```
Expected: 0 error/0 warning.

- [ ] **Step 5: Commit**
```powershell
git add src/02.Backend/NexaOne.ServiceContracts NexaOne.sln
$m = "feat(contracts): NexaOne.ServiceContracts 계약 어셈블리 + IEquipmentStateBridge/DTO(ADR-008 얇은 브리지)"
$f = [IO.Path]::GetTempFileName(); [IO.File]::WriteAllText($f,$m,(New-Object System.Text.UTF8Encoding($false))); git commit -F $f; Remove-Item $f
```

---

## Task 2: EST 모듈 어댑터 EquipmentStateBridge + est.xml + csproj 참조

**Files:**
- Modify: `src/04.Modules/NexaOne.EST/NexaOne.EST.csproj`
- Create: `src/04.Modules/NexaOne.EST/Application/Est/EquipmentStateBridge.cs`
- Modify: `src/00.Main/NexaOne.Server/Spring/est.xml`

- [ ] **Step 1: EST csproj에 계약 참조 추가**

`src/04.Modules/NexaOne.EST/NexaOne.EST.csproj`의 `<ItemGroup>`(ProjectReference)에 추가:
```xml
    <ProjectReference Include="..\..\02.Backend\NexaOne.ServiceContracts\NexaOne.ServiceContracts.csproj" />
```

- [ ] **Step 2: 어댑터**

`src/04.Modules/NexaOne.EST/Application/Est/EquipmentStateBridge.cs`:
```csharp
using NexaOne.Common;
using NexaOne.EST.Domain;
using NexaOne.ServiceContracts.Est;

namespace NexaOne.EST.Application.Est;

/// <summary>ADR-008 얇은 브리지 어댑터 — EquipmentStateService에 위임하고 도메인 엔티티를 계약 DTO로 매핑한다.
/// plugin ALC에서 생성되며 호스트(Default ALC)가 IEquipmentStateBridge로 캐스트해 DI에 등록한다.</summary>
public sealed class EquipmentStateBridge : IEquipmentStateBridge
{
    private readonly EquipmentStateService _service;

    public EquipmentStateBridge(EquipmentStateService service) => _service = service;

    public async Task<IReadOnlyList<EquipmentStateMatrixDto>> GetMatrixAsync(string plantId, CancellationToken ct = default)
        => (await _service.GetMatrixAsync(plantId, ct)).Select(ToDto).ToList();

    public async Task<IReadOnlyList<EquipmentStateMatrixDto>> GetAllowedTransitionsAsync(
        string plantId, string fromState, CancellationToken ct = default)
        => (await _service.GetAllowedTransitionsAsync(plantId, fromState, ct)).Select(ToDto).ToList();

    public async Task<IReadOnlyList<EquipmentStateDto>> GetEquipmentStatesAsync(string plantId, CancellationToken ct = default)
        => (await _service.GetEquipmentStatesAsync(plantId, ct)).Select(ToDto).ToList();

    public async Task<Result<EquipmentStateDto>> ChangeStateAsync(string equipmentId, string plantId, string toState,
        string requestedBy, string? reason, string sourceType, int? expectedVersion, CancellationToken ct = default)
    {
        var r = await _service.ChangeStateAsync(
            equipmentId, plantId, toState, requestedBy, reason ?? string.Empty, sourceType, expectedVersion, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<EquipmentStateDto>(r.Error);
    }

    public async Task<IReadOnlyList<EquipmentStateHistoryDto>> GetHistoryAsync(
        string equipmentId, int limit = 50, CancellationToken ct = default)
        => (await _service.GetHistoryAsync(equipmentId, limit, ct)).Select(ToDto).ToList();

    public async Task<Result<EquipmentStateMatrixDto>> UpsertMatrixAsync(string plantId, string fromStateId, string toStateId,
        bool allowFlag, string? setStateId, bool requireReason, CancellationToken ct = default)
    {
        var r = await _service.UpsertMatrixAsync(plantId, fromStateId, toStateId, allowFlag, setStateId, requireReason, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<EquipmentStateMatrixDto>(r.Error);
    }

    private static EquipmentStateDto ToDto(EquipmentCurrentState e)
        => new(e.Id, e.PlantId, e.CurrentStateId, e.StateChangedAt, e.StateVersion);

    private static EquipmentStateMatrixDto ToDto(EquipmentStateMatrix m)
        => new(m.Id, m.PlantId, m.FromStateId, m.ToStateId, m.AllowFlag, m.SetStateId, m.RequireReason, m.ValidState);

    private static EquipmentStateHistoryDto ToDto(EquipmentStateHistory h)
        => new(h.Id, h.EquipmentId, h.FromState, h.ToState, h.SetState, h.ChangedAt, h.ChangedBy, h.Reason, h.SourceType, h.DurationSeconds);
}
```

- [ ] **Step 3: est.xml 빈 배선**

`src/00.Main/NexaOne.Server/Spring/est.xml`의 `equipmentStateService` 빈 다음에 추가(닫는 `</objects>` 전):
```xml
  <!-- ADR-008 얇은 브리지 어댑터 — 호스트가 GetBean("Est","equipmentStateBridge")로 IEquipmentStateBridge 캐스트. -->
  <object id="equipmentStateBridge" type="NexaOne.EST.Application.Est.EquipmentStateBridge, NexaOne.EST">
    <constructor-arg ref="equipmentStateService" />
  </object>
```

- [ ] **Step 4: 빌드(모듈만)**
```powershell
dotnet build src/04.Modules/NexaOne.EST/NexaOne.EST.csproj -c Debug
```
Expected: 0 error/0 warning.

- [ ] **Step 5: Commit**
```powershell
git add src/04.Modules/NexaOne.EST/NexaOne.EST.csproj src/04.Modules/NexaOne.EST/Application/Est/EquipmentStateBridge.cs src/00.Main/NexaOne.Server/Spring/est.xml
$m = "feat(est): EquipmentStateBridge 어댑터(IEquipmentStateBridge 구현·도메인→DTO) + est.xml 빈 배선(ADR-008)"
$f = [IO.Path]::GetTempFileName(); [IO.File]::WriteAllText($f,$m,(New-Object System.Text.UTF8Encoding($false))); git commit -F $f; Remove-Item $f
```

---

## Task 3: 호스트 배선 — 계약 참조 + Program 등록 + Result 매핑 + EstBridgeController

**Files:**
- Modify: `src/00.Main/NexaOne.Server/NexaOne.Server.csproj`
- Modify: `src/00.Main/NexaOne.Server/Program.cs`
- Create: `src/00.Main/NexaOne.Server/Gateway/BridgeResultExtensions.cs`
- Create: `src/00.Main/NexaOne.Server/Gateway/EstBridgeController.cs`

- [ ] **Step 1: Server csproj에 계약 참조**

`src/00.Main/NexaOne.Server/NexaOne.Server.csproj`의 공유 ProjectReference ItemGroup(NexaOne.Application 등이 있는 곳)에 추가:
```xml
    <ProjectReference Include="..\..\02.Backend\NexaOne.ServiceContracts\NexaOne.ServiceContracts.csproj" />
```

- [ ] **Step 2: Program.cs — 브리지 빈 DI 등록(fail-fast)**

상단 using에 추가: `using NexaOne.ServiceContracts.Est;`

`if (modulesEnabled)` 블록 안에서, `AddService` foreach 루프와 워커 등록(`builder.Services.AddSingleton(server);` 이후 distinctWorkers 등록) **다음**, 같은 블록 끝부분에 추가:
```csharp
    // 복잡 서비스 얇은 브리지(ADR-008) — EST 설비상태 빈을 공유 계약 인터페이스로 캐스트해 DI 등록.
    // 캐스트 실패 = 계약 어셈블리 ALC 동일성 위반(deps-제외 누락 등) → 기동 시 즉시 폭발(무음 런타임 실패 방지).
    var equipmentStateBridge = server.GetBean("Est", "equipmentStateBridge") as IEquipmentStateBridge
        ?? throw new InvalidOperationException(
            "equipmentStateBridge 빈을 IEquipmentStateBridge로 캐스트하지 못했습니다 — "
            + "NexaOne.ServiceContracts가 plugin ALC로 복제 로드되지 않았는지(ADR-008/모듈 게시 deps-제외) 확인하세요.");
    builder.Services.AddSingleton(equipmentStateBridge);
```
(주의: `server.GetBean`은 모듈 컨텍스트가 생성된 뒤여야 하므로 반드시 `AddService` 루프 이후에 둔다. 모듈 비활성 시엔 등록하지 않으므로 EST 브리지 엔드포인트는 modules ON에서만 동작한다.)

- [ ] **Step 3: 호스트 Result→IActionResult 매핑**

`src/00.Main/NexaOne.Server/Gateway/BridgeResultExtensions.cs`:
```csharp
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;

namespace NexaOne.Server.Gateway;

/// <summary>Result/Result&lt;T&gt; → IActionResult — NexaOne.API ControllerResultExtensions와 동일 매핑
/// (Conflict→409, NotFound→404, Validation/Failure→400; 성공→Ok(value)/NoContent). 호스트가 API를 참조하지 않으므로 로컬 정의.</summary>
public static class BridgeResultExtensions
{
    private static ObjectResult Problem(Error error) => error.Type switch
    {
        ErrorType.NotFound => new NotFoundObjectResult(error),
        ErrorType.Conflict => new ConflictObjectResult(error),
        _ => new BadRequestObjectResult(error),   // Validation·Failure(및 미분류)는 400
    };

    public static IActionResult ToActionResult<T>(this Result<T> result, Func<T, IActionResult>? onSuccess = null)
        => result.IsSuccess
            ? onSuccess?.Invoke(result.Value) ?? new OkObjectResult(result.Value)
            : Problem(result.Error);

    public static IActionResult ToActionResult(this Result result, bool useNoContent = true)
        => result.IsSuccess
            ? (useNoContent ? new NoContentResult() : new OkResult())
            : Problem(result.Error);
}
```

- [ ] **Step 4: EstBridgeController**

`src/00.Main/NexaOne.Server/Gateway/EstBridgeController.cs`:
```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Est;

namespace NexaOne.Server.Gateway;

/// <summary>통합 호스트 EST 설비상태 엔드포인트(ADR-008 얇은 브리지). plugin-ALC EquipmentStateService를
/// IEquipmentStateBridge로 호출한다. 라우트/상태코드는 NexaOne.API EstController와 동일. 쓰기는 est:manage 수동 검사.
/// (modules ON에서만 IEquipmentStateBridge가 등록되므로 동작한다.)</summary>
[ApiController]
[Route("api/v1/est")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public sealed class EstBridgeController : ControllerBase
{
    private readonly IEquipmentStateBridge _bridge;

    public EstBridgeController(IEquipmentStateBridge bridge) => _bridge = bridge;

    [HttpGet("state-matrix")]
    [ProducesResponseType<IReadOnlyList<EquipmentStateMatrixDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStateMatrix([FromQuery] string plantId, CancellationToken ct)
        => Ok(await _bridge.GetMatrixAsync(plantId, ct));

    [HttpGet("state-matrix/allowed")]
    [ProducesResponseType<IReadOnlyList<EquipmentStateMatrixDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllowedTransitions(
        [FromQuery] string plantId, [FromQuery] string fromState, CancellationToken ct)
        => Ok(await _bridge.GetAllowedTransitionsAsync(plantId, fromState, ct));

    [HttpPost("state-matrix")]
    [ProducesResponseType<EquipmentStateMatrixDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpsertMatrix([FromBody] UpsertMatrixRequest req, CancellationToken ct)
    {
        if (!HasPermission(Permissions.EstManage)) return Forbid();
        var result = await _bridge.UpsertMatrixAsync(
            req.PlantId, req.FromStateId, req.ToStateId, req.AllowFlag, req.SetStateId, req.RequireReason, ct);
        return result.ToActionResult();
    }

    [HttpGet("equipment-state")]
    [ProducesResponseType<IReadOnlyList<EquipmentStateDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetEquipmentStates([FromQuery] string plantId, CancellationToken ct)
        => Ok(await _bridge.GetEquipmentStatesAsync(plantId, ct));

    [HttpPost("equipment-state/change")]
    [ProducesResponseType<EquipmentStateDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ChangeState([FromBody] ChangeStateRequest req, CancellationToken ct)
    {
        if (!HasPermission(Permissions.EstManage)) return Forbid();
        // requestedBy는 토큰 주체에서 취한다(비-부인성). 감사 사용자는 AuditUserContextMiddleware가 CurrentUserContext에 이미 설정.
        var result = await _bridge.ChangeStateAsync(
            req.EquipmentId, req.PlantId, req.ToState, CurrentUserId, req.Reason, "UI", req.ExpectedVersion, ct);
        return result.ToActionResult();
    }

    [HttpGet("equipment-state/{equipmentId}/history")]
    [ProducesResponseType<IReadOnlyList<EquipmentStateHistoryDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStateHistory(string equipmentId, CancellationToken ct)
        => Ok(await _bridge.GetHistoryAsync(equipmentId, 50, ct));

    private string CurrentUserId =>
        User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
        ?? User.Identity?.Name ?? "SYSTEM";

    private bool HasPermission(string permission) =>
        User.FindAll(Permissions.ClaimType)
            .Any(c => c.Value == Permissions.All || string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));
}

public record ChangeStateRequest(string EquipmentId, string PlantId, string ToState, string? Reason, int? ExpectedVersion);
public record UpsertMatrixRequest(string PlantId, string FromStateId, string ToStateId, bool AllowFlag, string? SetStateId, bool RequireReason);
```

- [ ] **Step 5: 빌드 + ServerTests 회귀**
```powershell
dotnet build NexaOne.sln -c Debug
dotnet test test/NexaOne.ServerTests/NexaOne.ServerTests.csproj -c Debug
```
Expected: 0 error/no-new-warning; ServerTests 22 (기존 20, 신규 컨트롤러는 Task 4에서 추가 — 여기선 기존 그대로 통과). modules OFF 테스트가 깨지지 않아야 한다(브리지는 modules ON에서만 등록되므로 기존 modules-OFF 테스트에 영향 없음).

- [ ] **Step 6: Commit**
```powershell
git add src/00.Main/NexaOne.Server/NexaOne.Server.csproj src/00.Main/NexaOne.Server/Program.cs src/00.Main/NexaOne.Server/Gateway/BridgeResultExtensions.cs src/00.Main/NexaOne.Server/Gateway/EstBridgeController.cs
$m = "feat(server): EST 얇은 브리지 배선(GetBean→IEquipmentStateBridge DI 등록) + EstBridgeController + Result 매핑(ADR-008)"
$f = [IO.Path]::GetTempFileName(); [IO.File]::WriteAllText($f,$m,(New-Object System.Text.UTF8Encoding($false))); git commit -F $f; Remove-Item $f
```

---

## Task 4: 테스트 — 컨트롤러 HTTP(가짜 브리지) + 어댑터 로직(SQLite) + 수동 검증 문서

**Files:**
- Create: `test/NexaOne.ServerTests/EstBridgeControllerTests.cs`
- Create: `test/NexaOne.IntegrationTests/Est/EquipmentStateBridgeIntegrationTests.cs`
- Modify: `docs/design/specs/2026-06-20-unified-host-phase3c-bridge-design.md` (수동 검증 결과 §6 기록)

- [ ] **Step 1: 컨트롤러 HTTP 테스트(modules OFF + 가짜 IEquipmentStateBridge 주입)**

`test/NexaOne.ServerTests/EstBridgeControllerTests.cs`:
```csharp
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using NexaOne.Common;
using NexaOne.ServiceContracts.Est;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>EST 얇은 브리지 컨트롤러 HTTP 매핑 검증 — modules OFF + 가짜 IEquipmentStateBridge 주입으로
/// Result→HTTP(200/409/400)·쓰기 권한 403·읽기 200을 Spring/ALC 없이 결정적으로 검증한다.</summary>
public sealed class EstBridgeControllerTests : IClassFixture<EstBridgeControllerTests.BridgeFactory>
{
    private const string Secret = "phase3c-bridge-e2e-jwt-secret-key-at-least-32b!!";
    private const string Issuer = "nexaone-bridge-test";
    private readonly BridgeFactory _factory;
    public EstBridgeControllerTests(BridgeFactory factory) => _factory = factory;

    public sealed class BridgeFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-bridge-{Guid.NewGuid():N}.db");
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", $"Data Source={DbPath};Foreign Keys=False");
            builder.UseSetting("Jwt:SecretKey", Secret);
            builder.UseSetting("Jwt:Issuer", Issuer);
            builder.UseSetting("Jwt:Audience", Issuer);
            builder.UseSetting("RateLimiting:Enabled", "false");
            // modules OFF라 Program이 브리지를 등록하지 않으므로 가짜를 주입한다(컨트롤러 단독 검증).
            builder.ConfigureTestServices(s => s.AddSingleton<IEquipmentStateBridge>(new FakeBridge()));
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* 무시 */ }
        }
    }

    // toState로 결과를 결정하는 가짜 브리지: "__conflict__"→409, "__invalid__"→400(Failure), "__reason__"→400(Validation), else 성공.
    private sealed class FakeBridge : IEquipmentStateBridge
    {
        public Task<IReadOnlyList<EquipmentStateMatrixDto>> GetMatrixAsync(string plantId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EquipmentStateMatrixDto>>(
                new[] { new EquipmentStateMatrixDto($"{plantId}:IDLE:RUN", plantId, "IDLE", "RUN", true, "RUN", false, "Valid") });
        public Task<IReadOnlyList<EquipmentStateMatrixDto>> GetAllowedTransitionsAsync(string plantId, string fromState, CancellationToken ct = default)
            => GetMatrixAsync(plantId, ct);
        public Task<IReadOnlyList<EquipmentStateDto>> GetEquipmentStatesAsync(string plantId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EquipmentStateDto>>(
                new[] { new EquipmentStateDto("EQ1", plantId, "IDLE", DateTime.UtcNow, 1) });
        public Task<Result<EquipmentStateDto>> ChangeStateAsync(string equipmentId, string plantId, string toState,
            string requestedBy, string? reason, string sourceType, int? expectedVersion, CancellationToken ct = default)
            => Task.FromResult(toState switch
            {
                "__conflict__" => Result.Failure<EquipmentStateDto>(Error.Conflict("concurrent")),
                "__invalid__"  => Result.Failure<EquipmentStateDto>(Error.Failure("EPT.InvalidTransition", "not allowed")),
                "__reason__"   => Result.Failure<EquipmentStateDto>(Error.Validation("reason", "reason required")),
                _ => Result.Success(new EquipmentStateDto(equipmentId, plantId, toState, DateTime.UtcNow, (expectedVersion ?? 1) + 1)),
            });
        public Task<IReadOnlyList<EquipmentStateHistoryDto>> GetHistoryAsync(string equipmentId, int limit = 50, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EquipmentStateHistoryDto>>(
                new[] { new EquipmentStateHistoryDto("H1", equipmentId, "IDLE", "RUN", "RUN", DateTime.UtcNow, "tester", "", "UI", null) });
        public Task<Result<EquipmentStateMatrixDto>> UpsertMatrixAsync(string plantId, string fromStateId, string toStateId,
            bool allowFlag, string? setStateId, bool requireReason, CancellationToken ct = default)
            => Task.FromResult(Result.Success(new EquipmentStateMatrixDto(
                $"{plantId}:{fromStateId}:{toStateId}", plantId, fromStateId, toStateId, allowFlag, setStateId ?? toStateId, requireReason, "Valid")));
    }

    private HttpClient Client(params string[] permissions)
    {
        var client = _factory.CreateClient();
        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "bridge-tester") };
        claims.AddRange(permissions.Select(p => new Claim(NexaOne.Common.Security.Permissions.ClaimType, p)));
        var token = new JwtSecurityToken(Issuer, Issuer, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    [Fact]
    public async Task ChangeState_success_returns_200_with_dto()
    {
        var res = await Client("est:manage").PostAsJsonAsync("/api/v1/est/equipment-state/change",
            new { equipmentId = "EQ1", plantId = "P1", toState = "RUN", reason = (string?)null, expectedVersion = (int?)1 });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<EquipmentStateDto>();
        dto!.CurrentStateId.Should().Be("RUN");
        dto.StateVersion.Should().Be(2);
    }

    [Fact]
    public async Task ChangeState_conflict_maps_to_409()
    {
        var res = await Client("est:manage").PostAsJsonAsync("/api/v1/est/equipment-state/change",
            new { equipmentId = "EQ1", plantId = "P1", toState = "__conflict__", reason = (string?)null, expectedVersion = (int?)1 });
        res.StatusCode.Should().Be(HttpStatusCode.Conflict, "낙관적 동시성 Conflict는 409로 매핑");
    }

    [Fact]
    public async Task ChangeState_invalid_transition_and_missing_reason_map_to_400()
    {
        var invalid = await Client("est:manage").PostAsJsonAsync("/api/v1/est/equipment-state/change",
            new { equipmentId = "EQ1", plantId = "P1", toState = "__invalid__", reason = (string?)null, expectedVersion = (int?)null });
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest, "InvalidTransition(Failure)은 400");
        var reason = await Client("est:manage").PostAsJsonAsync("/api/v1/est/equipment-state/change",
            new { equipmentId = "EQ1", plantId = "P1", toState = "__reason__", reason = (string?)null, expectedVersion = (int?)null });
        reason.StatusCode.Should().Be(HttpStatusCode.BadRequest, "RequireReason(Validation)은 400");
    }

    [Fact]
    public async Task ChangeState_without_est_manage_is_forbidden()
    {
        var res = await Client("fdc:read").PostAsJsonAsync("/api/v1/est/equipment-state/change",
            new { equipmentId = "EQ1", plantId = "P1", toState = "RUN", reason = (string?)null, expectedVersion = (int?)1 });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "est:manage 미보유 쓰기는 403");
    }

    [Fact]
    public async Task GetStateMatrix_returns_200_for_authenticated_reader()
    {
        var res = await Client().GetAsync("/api/v1/est/state-matrix?plantId=P1");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await res.Content.ReadFromJsonAsync<List<EquipmentStateMatrixDto>>();
        rows!.Should().ContainSingle(m => m.FromStateId == "IDLE" && m.ToStateId == "RUN");
    }
}
```

- [ ] **Step 2: 어댑터 로직 통합 테스트(실 SQLite 리포)**

먼저 기존 EST 통합테스트 패턴을 확인하라: `Select-String -Path test/NexaOne.IntegrationTests -Pattern "EquipmentStateService|EquipmentStateMatrixRepository|EquipmentStateRepository" -List` 로 SQLite EesDataSource·리포 생성·스키마 부트스트랩(SqliteSchemaBootstrapper 등) 사용법을 파악한 뒤 동일 픽스처로 작성한다.

`test/NexaOne.IntegrationTests/Est/EquipmentStateBridgeIntegrationTests.cs` — `EquipmentStateBridge`를 실 SQLite 리포로 구동해 다음을 검증(기존 픽스처의 EesDataSource/dialect/config 생성 방식을 그대로 사용):
- 매트릭스 시드: `UpsertMatrixAsync(plant,"IDLE","RUN",allow:true,setState:null,requireReason:false)`, `("IDLE","NEEDR",true,null,requireReason:true)` (IDLE→BAD는 미시드=불허).
- `ChangeStateAsync(eq,plant,"RUN",user,null,"UI",expectedVersion:null)` → `IsSuccess`, `Value.CurrentStateId=="RUN"`, `Value.StateVersion` 증가, DTO 타입이 `EquipmentStateDto`.
- 같은 설비에 `ChangeStateAsync(...,expectedVersion: 1)` 처럼 **틀린 버전** → `IsFailure`, `Error.Type==ErrorType.Conflict`.
- `ChangeStateAsync(eq,plant,"BAD",...)`(미시드 전이) → `IsFailure`, `Error.Code=="EPT.InvalidTransition"`.
- `ChangeStateAsync(eq2,plant,"NEEDR",user,reason:null,...)` → `IsFailure`, `Error.Type==ErrorType.Validation`.
- `GetMatrixAsync`/`GetEquipmentStatesAsync`/`GetHistoryAsync`가 DTO 리스트를 반환(매핑 검증).

핵심 어설션은 위 5개 분기(Success/Conflict/InvalidTransition/Validation + DTO 매핑)다. 실제 SQLite 위에서 `EquipmentStateBridge`(→`EquipmentStateService`→리포→`ChangeStateWithHistoryAsync`)가 Spring/ALC 없이 동작함을 입증한다.

- [ ] **Step 3: 전체 테스트**
```powershell
dotnet build NexaOne.sln -c Debug
dotnet test test/NexaOne.ServerTests/NexaOne.ServerTests.csproj -c Debug
dotnet test test/NexaOne.IntegrationTests/NexaOne.IntegrationTests.csproj -c Debug
dotnet test test/NexaOne.UnitTests/NexaOne.UnitTests.csproj -c Debug
```
Expected: build 0 error; ServerTests 25(20+5), IntegrationTests 287+신규(+1 skip), UnitTests 1090. 전부 그린.

- [ ] **Step 4: 수동 ALC 동일성 검증(modules ON)**

WebApplicationFactory가 plugin ALC를 못 띄우므로 수동 확인한다. `src/00.Main/NexaOne.Server` 출력에서 modules ON + SQLite로 기동:
```powershell
$env:Server__Modules__Enabled = "true"; $env:Database__Provider = "Sqlite"
$env:ConnectionStrings__NexaOne = "Data Source=$([IO.Path]::Combine($env:TEMP,'nexaone-p3c.db'));Foreign Keys=False"
$env:Jwt__SecretKey = "phase3c-manual-verify-secret-at-least-32-bytes!!"; $env:Jwt__Issuer="nx"; $env:Jwt__Audience="nx"; $env:ASPNETCORE_ENVIRONMENT="Development"
dotnet run --project src/00.Main/NexaOne.Server -c Debug
```
기동 로그에 9개 모듈 로드 + 캐스트 예외 없음(브리지 등록 성공)을 확인하고, JWT(est:manage)로 `POST /api/v1/est/equipment-state/change`를 호출해 200/매트릭스 미시드 시 400을 확인한다. **빌드 산출물 DLL 잠금 등으로 로컬 기동이 불가하면 그 사실과 함께 "ALC 동일성은 빌드 성공 + GetBean 캐스트 코드 경로로 정적 보장, 런타임 검증 보류"로 명시**(Phase 1 패턴). 결과를 설계문서 §6에 1~2문장 기록.

- [ ] **Step 5: Commit**
```powershell
git add test/NexaOne.ServerTests/EstBridgeControllerTests.cs test/NexaOne.IntegrationTests/Est/EquipmentStateBridgeIntegrationTests.cs docs/design/specs/2026-06-20-unified-host-phase3c-bridge-design.md
$m = "test(server,est): EST 얇은 브리지 — 컨트롤러 HTTP 매핑(가짜) + 어댑터 로직 SQLite + 수동검증 기록(ADR-008)"
$f = [IO.Path]::GetTempFileName(); [IO.File]::WriteAllText($f,$m,(New-Object System.Text.UTF8Encoding($false))); git commit -F $f; Remove-Item $f
```

---

## Self-Review

**스펙 커버리지(설계 §3~6):** 계약 어셈블리+인터페이스+DTO→T1; 모듈 어댑터+xml→T2; 호스트 참조+Program 등록(fail-fast)+Result 매핑+컨트롤러→T3; 컨트롤러/어댑터/수동 검증→T4. ✅
**ADR-008 결정 반영:** 타입드 인터페이스(리플렉션 배제) ✅ / deps-제외 ALC 동일성 ✅ / 도메인→DTO 매핑 ✅ / 부팅 fail-fast 등록 ✅ / 전용 컨트롤러 + 수동 permission(est:manage) + requestedBy 토큰 ✅ / Result→HTTP 파리티(Conflict 409·기타 400) ✅.
**플레이스홀더:** 핵심 코드 전문 기재. 어댑터 통합테스트만 기존 IntegrationTests 픽스처 의존이라 픽스처 확인 지시 + 5개 분기 어설션 명시(픽스처 API는 리포 생성자 시그니처가 코드에 있으니 구현자가 동일 패턴으로 작성).
**타입 일관성:** `IEquipmentStateBridge` 시그니처가 T1 정의와 T2 구현·T3 컨트롤러·T4 가짜에서 동일. `EquipmentCurrentState.Id/PlantId/CurrentStateId/StateChangedAt/StateVersion`, `EquipmentStateMatrix.Id/...`, `EquipmentStateHistory.Id/...DurationSeconds`, `Result.Success/Failure<T>/IsSuccess/Value/Error`, `Error.Conflict/Failure/Validation`·`ErrorType.Conflict/NotFound/Validation/Failure`, `Permissions.EstManage/ClaimType/All` — 모두 실제 코드 확인값.
**미해결:** MDM 설비 존재 사전검증(파리티 갭, 설계 §8 기록); RMS/Lot 후속 슬라이스(청사진 §7); 수동 ALC 검증의 환경 의존.
