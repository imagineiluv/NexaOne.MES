# 통합 호스트 pwdChange 미들웨어 이식 (후속) 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. 체크박스 단계.

**Goal:** 비밀번호 강제 변경 사용자(`pwdChange` 클레임)가 업무 API를 호출하지 못하게 통합 호스트에 `PasswordChangeRequiredMiddleware`를 이식한다. 트랙①의 register(`PASSWORD_STATE='Create'`)·강제변경 토큰이 실제로 게이트웨이를 차단하도록 만든다(현재 호스트엔 미들웨어 부재로 미강제).

**Architecture:** 레거시 [NexaOne.API/Middleware/PasswordChangeRequiredMiddleware.cs](../../../src/02.Backend/NexaOne.API/Middleware/PasswordChangeRequiredMiddleware.cs)를 호스트 로컬로 미러(호스트는 NexaOne.API 미참조 — AuthController/AuthDtos와 동일 미러 패턴). 판정은 토큰 `pwdChange` 클레임만(요청당 DB 조회 없음). 단, 통합 호스트는 UI를 같은 프로세스에서 서빙하므로 **차단 대상을 데이터 표면(`/api/v1/*` 비-auth + `/hubs/*`)으로 한정**하고 정적 SPA·Blazor 셸·/health·/diag는 허용해 강제변경 사용자가 앱을 로드해 비밀번호를 바꿀 수 있게 한다. `JwtService.PasswordChangeClaim`("pwdChange")은 NexaOne.Application.Auth(호스트 참조)에 있음.

**Tech Stack:** ASP.NET Core 미들웨어, xUnit(단위 path-matrix + E2E 수명주기).

---

## 검증된 사실 (직접 확인, 2026-06-21)

- 레거시 미들웨어: `pwdChange == "true"`면 `IsAllowedPath`(=`/api/v1/auth` 또는 `/health`) 외 전부 403 `PASSWORD_CHANGE_REQUIRED`. 판정은 클레임만(DB 무조회), 변경 성공 시 클레임 없는 새 토큰 재발급으로 자연 해제.
- `JwtService.PasswordChangeClaim = "pwdChange"`([JwtService.cs:13](../../../src/02.Backend/NexaOne.Application/Auth/JwtService.cs#L13)); `requirePasswordChange` 시 발급([JwtService.cs:44-45](../../../src/02.Backend/NexaOne.Application/Auth/JwtService.cs#L44-L45)). 호스트 GatewayLoginService가 `requireChange = PASSWORD_STATE != 'Normal'`로 발급(login/refresh), change-password 성공 시 `requireChange=false` 새 토큰.
- 호스트 미들웨어 순서([Program.cs](../../../src/00.Main/NexaOne.Server/Program.cs)): `UseAuthentication()`(L207) → `AuditUserContextMiddleware`(L208) → `UseAuthorization()`(L211) → `MapControllers()`(L216). 신규 미들웨어는 **User 채워진 뒤(UseAuthentication 이후)**, 엔드포인트 실행 전(MapControllers 앞)에 둔다 → `UseAuthorization()` 다음(L211와 L216 사이)에 등록.
- 호스트는 **SignalR 허브 미매핑**(MapHub 없음). `/hubs/*` 차단 절은 방어적·미래대비(레거시 단위테스트가 `/hubs/smartees` 차단을 기대 — 호환).
- 레거시 단위테스트([PasswordChangeRequiredMiddlewareTests.cs](../../../test/NexaOne.UnitTests/Middleware/PasswordChangeRequiredMiddlewareTests.cs)) 기대: `/api/v1/sys/menus`·`/api/v1/mdm/equipments`·`/hubs/smartees` 차단; `/api/v1/auth/change-password`·`/api/v1/auth/logout`·`/health` 허용; 클레임 없으면 통과. **본 호스트 블록리스트 로직은 이 기대를 전부 충족**(추가로 /spa·/meta·/diag 허용은 레거시 테스트 미검증 경로라 비충돌).
- E2E 토큰 경로: register(admin, PASSWORD_STATE='Create')→해당 사용자 login→pwdChange 토큰. 업무 API는 modules-OFF에서도 동작하는 `/api/v1/query/MDM.PlantList`(게이트웨이) 사용.

## File Structure
- 생성: `src/00.Main/NexaOne.Server/Gateway/PasswordChangeRequiredMiddleware.cs`.
- 수정: `src/00.Main/NexaOne.Server/Program.cs`(UseAuthorization 다음에 등록 1줄).
- 생성: `test/NexaOne.ServerTests/PasswordChangeGateTests.cs`(단위 path-matrix + E2E 수명주기).

---

## Task 1: 미들웨어 + 등록

- [ ] **Step 1: 미들웨어 생성** `src/00.Main/NexaOne.Server/Gateway/PasswordChangeRequiredMiddleware.cs`
```csharp
using NexaOne.Application.Auth;

namespace NexaOne.Server.Gateway;

/// <summary>비밀번호 강제 변경(pwdChange 클레임) 사용자의 업무 데이터 호출을 차단한다(§20.10). 통합 호스트는 UI를
/// 같은 프로세스에서 서빙하므로 데이터 표면(/api/v1/* 비-auth + /hubs/*)만 403으로 막고, 정적 SPA·Blazor 셸·
/// /health·/diag는 허용해 강제변경 사용자가 앱을 로드해 비밀번호를 바꿀 수 있게 한다. 판정은 토큰 클레임만(DB 무조회).</summary>
public sealed class PasswordChangeRequiredMiddleware
{
    private readonly RequestDelegate _next;
    public PasswordChangeRequiredMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var requiresChange = context.User?.FindFirst(JwtService.PasswordChangeClaim)?.Value == "true";
        if (requiresChange && IsBlocked(context.Request.Path))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                code = "PASSWORD_CHANGE_REQUIRED",
                message = "비밀번호 변경 후 이용할 수 있습니다."
            });
            return;
        }
        await _next(context);
    }

    // 차단 = 업무 데이터 표면만: /api/v1/* (단 /api/v1/auth는 허용) 또는 /hubs/*. 정적 UI·/health·/diag는 통과.
    private static bool IsBlocked(PathString path)
        => (path.StartsWithSegments("/api/v1") && !path.StartsWithSegments("/api/v1/auth"))
           || path.StartsWithSegments("/hubs");
}
```

- [ ] **Step 2: Program.cs 등록** — `app.UseAuthorization();`(L211) 바로 다음 줄에 추가:
```csharp
// 비밀번호 강제 변경(pwdChange) 사용자의 업무 데이터 호출 차단 — 인증 이후, 엔드포인트 실행 이전.
app.UseMiddleware<NexaOne.Server.Gateway.PasswordChangeRequiredMiddleware>();
```
(UseAuthorization과 UseAntiforgery 사이. AuditUserContextMiddleware 등록 형식과 동일하게 `UseMiddleware<T>`.)

- [ ] **Step 3: 빌드** `dotnet build src/00.Main/NexaOne.Server/NexaOne.Server.csproj -c Debug --nologo` → 0 errors.

- [ ] **Step 4: 커밋** `feat(auth): pwdChange 강제변경 게이트 미들웨어 호스트 이식(업무 API 차단)` — 명시 경로만(PasswordChangeRequiredMiddleware.cs, Program.cs).

---

## Task 2: 테스트 (단위 path-matrix + E2E 수명주기)

- [ ] **Step 1: `test/NexaOne.ServerTests/PasswordChangeGateTests.cs` 생성**

**(a) 단위 path-matrix**(DefaultHttpContext로 미들웨어 직접 — WebApplicationFactory 불필요):
- 차단(403, next 미호출, 본문 "PASSWORD_CHANGE_REQUIRED"): `/api/v1/query/MDM.PlantList`, `/api/v1/sys/queries`, `/api/v1/est/states`, `/hubs/smartees` (pwdChange=true).
- 허용(next 호출, 200): `/api/v1/auth/change-password`, `/api/v1/auth/logout`, `/api/v1/auth/me`, `/health`, `/diag`, `/spa/index.html`, `/meta/DEMO_GRID` (pwdChange=true).
- 클레임 없으면 `/api/v1/query/MDM.PlantList` 통과.
패턴은 [PasswordChangeRequiredMiddlewareTests.cs](../../../test/NexaOne.UnitTests/Middleware/PasswordChangeRequiredMiddlewareTests.cs) 동형(ClaimsPrincipal에 `new Claim(JwtService.PasswordChangeClaim,"true")`, ctx.Response.Body=MemoryStream). 미들웨어 타입은 `NexaOne.Server.Gateway.PasswordChangeRequiredMiddleware`.

**(b) E2E 수명주기**(GatewayAuthCompletenessTests의 팩토리 패턴 복제 — modules-OFF, SQLite 전용 DB, admin 시드):
1. admin 로그인 토큰으로 `POST /api/v1/auth/register {userId:"mc1", userName:"MC1", password:"McUser!99", email:"mc@x.com", roleId:"OPERATOR"}` → 200.
2. mc1 로그인 → `requirePasswordChange == true`(PASSWORD_STATE='Create'). 그 accessToken으로:
   - `POST /api/v1/query/MDM.PlantList {}` → **403**, 본문 code `PASSWORD_CHANGE_REQUIRED`.
   - `GET /api/v1/auth/me` → **200**(auth는 허용).
3. mc1 토큰으로 `POST /api/v1/auth/change-password {currentPassword:"McUser!99", newPassword:"McNew!Pass1", confirmPassword:"McNew!Pass1"}` → 200(새 토큰, pwdChange 없음).
4. **새** accessToken으로 `POST /api/v1/query/MDM.PlantList {}` → **403 아님**(PASSWORD_CHANGE_REQUIRED 미발생; 게이트웨이 통과 — 200 또는 데이터 의존 응답이되 PASSWORD_CHANGE_REQUIRED 코드가 아님). 단언: status != 403 또는 본문에 PASSWORD_CHANGE_REQUIRED 미포함.

- [ ] **Step 2: 테스트 실행** `dotnet test test/NexaOne.ServerTests/NexaOne.ServerTests.csproj -c Debug --nologo` → 기존(55) + 신규 전부 통과.
주의: 다른 dotnet NexaOne.Server 프로세스가 출력 잠그면 BLOCKED 보고.

- [ ] **Step 3: 커밋** `test(auth): pwdChange 게이트 단위(path-matrix) + E2E(강제변경→차단→변경→해제)`.

---

## Task 3 (컨트롤러): 회귀 + 리뷰 + ff-merge
- ServerTests 전부 통과 재확인. 최종 보안 리뷰(차단 표면 정확성·UI 셸 미차단·auth 자기해제) 후 main ff-merge(sln 가드, git `2>&1` 금지, push 안 함).

## Self-Review
- 차단 표면: `/api/v1/*` 비-auth + `/hubs/*`(업무 데이터). 허용: auth·health·diag·정적 SPA·Blazor 셸(강제변경 사용자가 변경 UI 로드 가능). 레거시 단위테스트 기대(데이터 차단/auth·health 허용)와 결과 동일. ✓
- 자기해제: change-password가 클레임 없는 새 토큰 발급 → 새 토큰은 게이트 통과(E2E Step 4). 무한 잠금 없음. ✓
- 순서: UseAuthentication 이후(User 채워짐) → 차단 판정 가능. UseAuthorization 다음·MapControllers 앞(엔드포인트 전 차단). ✓
- 무영향: 클레임 없는 일반 사용자·익명(클레임 없음)은 통과(익명은 애초에 [Authorize]에서 처리). login/refresh 무변경. ✓
- 한계: 판정은 토큰 클레임 기반(DB 무조회) — 변경 전 발급 토큰은 만료까지 차단 유지(의도). /diag 허용은 진단 표면(비-업무) — 수용.
