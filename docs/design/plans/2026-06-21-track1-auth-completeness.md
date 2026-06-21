# 통합 호스트 인증 완결 (트랙 ①) 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** 통합 호스트 인증을 login/refresh에서 **logout·change-password·register·me**까지 완결한다(게이트웨이식 무-브리지). 레거시 NexaOne.API AuthController와 동일 라우트/DTO/상태코드를 재현하되, 이메일 인프라가 필요한 forgot/reset-password는 범위 제외(명시).

**Architecture:** 기존 패턴 그대로 — `AuthController`(HTTP·정책 검증·권한 게이트) + `GatewayLoginService`(격리 인증 명명 쿼리로 DB 작업) + `SysRefreshTokenStore`(토큰 폐기 — 이미 RevokeAsync/RevokeAllByUserAsync 보유) + `AuthOutcome`(IActionResult 매핑). 신규 쓰기 쿼리(SYS.UpdatePassword·SYS.InsertUser)는 격리 인증 레지스트리(db/queries-auth)에 추가하며 내부 ExecuteAsync로만 실행(공개 게이트웨이 미노출, 기존 인증 쓰기 쿼리와 동일 규약 — kind 미표기).

**Tech Stack:** C#/.NET 8 ASP.NET Core, 기존 PasswordHasher(PBKDF2)·PasswordPolicy(§19.2.2), xUnit E2E(SQLite, modules-OFF).

---

## 검증된 사실 (직접 확인, 2026-06-21)

- **레거시 참조**(미러 대상): [NexaOne.API/Controllers/AuthController.cs](../../../src/02.Backend/NexaOne.API/Controllers/AuthController.cs) — login/logout/refresh/change-password/forgot-password/reset-password/me. logout(L71-83): userId는 **토큰에서**(본문 신뢰 금지=IDOR 방지), `RevokeAsync(userId, refreshToken)`, 204. change-password(L117-160): NewPassword==ConfirmPassword(아니면 400 `Auth.PasswordMismatch`) → 사용자 로드 → `PasswordPolicy.Validate(new, userId, userName, email)`(위반 400 `PASSWORD_POLICY_VIOLATION`) → 현재 비번 Verify+새 해시 저장(실패 400) → `RevokeAllByUserAsync` → pwdChange 없는 새 토큰 재발급(200 TokenRefreshResponse). me(L186-198): 클레임 반환.
- **호스트 현행**: [AuthController.cs](../../../src/00.Main/NexaOne.Server/Gateway/AuthController.cs) login/refresh만. [GatewayLoginService.cs](../../../src/00.Main/NexaOne.Server/Gateway/GatewayLoginService.cs) LoginAsync/RefreshAsync + 헬퍼(QuerySingleAsync/ExecuteAsync/Get/ToBool/ToStr/EffectivePermissions). [SysRefreshTokenStore.cs](../../../src/00.Main/NexaOne.Server/Gateway/SysRefreshTokenStore.cs) IssueAsync/ValidateAsync/RotateAsync/**RevokeAsync**/**RevokeAllByUserAsync**(이미 존재, 쿼리 SYS.RevokeRefreshTokenIfActive/SYS.RevokeAllRefreshTokens). [AuthOutcome.cs](../../../src/00.Main/NexaOne.Server/Gateway/AuthOutcome.cs) Ok/InvalidCredentials/AccountLocked/InvalidRefreshToken(200/401만). [AuthDtos.cs](../../../src/00.Main/NexaOne.Server/Gateway/AuthDtos.cs) LoginRequest/LoginResponse/RefreshRequest/TokenRefreshResponse.
- **격리 인증 레지스트리**: [AuthServiceExtensions.cs](../../../src/00.Main/NexaOne.Server/Gateway/AuthServiceExtensions.cs)가 `FileQueryRegistry.Load(dialect, db/queries-auth)`로 별도 로드해 `GatewayLoginService`·`SysRefreshTokenStore`에 주입(공개 IQueryRegistry와 분리, PASSWORD_HASH 누출 차단). 인증 쓰기 쿼리(SYS.RecordLoginSuccess 등)는 **kind 미표기**(IsWrite=false)이고 내부 ExecuteAsync로 직접 실행되어 requiredPermission fail-fast 비대상 — 신규 쿼리도 동일 규약.
- **SYS_USER 스키마**(INSERT/UPDATE 대상): [V001](../../../db/migrations/V001__SYS_USER.sql) USER_ID(PK)/USER_NAME/PASSWORD_HASH/EMAIL/ROLE_ID/LANGUAGE(기본 'KoKr')/IS_ACTIVE(기본1)/IS_DELETED(기본0)/DELETED_AT(null)/LAST_LOGIN_AT(null)/CREATED_BY/CREATED_AT/UPDATED_BY/UPDATED_AT. [V011](../../../db/migrations/V011__SYS_LOGIN_FAILURE_HIST.sql) ADD PASSWORD_STATE(기본 'Normal', 값 Normal/Create/Forgot/Expired)/FAIL_COUNT(기본0)/LOCKED_UNTIL(null). PASSWORD_HASH는 Phase 3b V034에서 NVARCHAR(255)로 확장(PBKDF2 수용).
- **PasswordHasher**([PasswordHasher.cs](../../../src/02.Backend/NexaOne.Common/PasswordHasher.cs)): `Hash(pw)`(PBKDF2 저장형식), `Verify(pw, stored)`(PBKDF2+레거시 SHA256), `NeedsRehash`. **PasswordPolicy**([PasswordPolicy.cs](../../../src/02.Backend/NexaOne.Common/PasswordPolicy.cs)): `Validate(pw, userId?, userName?, email?)` → 위반 메시지 또는 null, `ErrorCode="PASSWORD_POLICY_VIOLATION"`.
- **권한 게이트 패턴**: [QueryCatalogController.cs:36-38](../../../src/00.Main/NexaOne.Server/Gateway/QueryCatalogController.cs#L36-L38) `User.FindAll(Permissions.ClaimType).Any(c => c.Value==Permissions.All || ==perm)`. `Permissions.SysManage`("sys:manage") 존재.
- **기존 E2E 패턴**: [GatewayAuthE2ETests.cs](../../../test/NexaOne.ServerTests/GatewayAuthE2ETests.cs)(modules-OFF, SQLite, login/refresh 라운드트립·JWT 발급)을 동형 확장.

**범위 제외**: forgot-password/reset-password(이메일 발송 인프라 부재 — PasswordResetService 미이식). 별도 후속.

## File Structure
- 수정: `src/00.Main/NexaOne.Server/Gateway/AuthDtos.cs` — LogoutRequest·ChangePasswordRequest·RegisterRequest·CurrentUserResponse 추가.
- 수정: `src/00.Main/NexaOne.Server/Gateway/AuthOutcome.cs` — NoContent·BadRequest(Error)·Conflict(Error) 팩토리 추가.
- 수정: `src/00.Main/NexaOne.Server/Gateway/GatewayLoginService.cs` — ChangePasswordAsync·RegisterAsync·GetUserRowAsync(공개 헬퍼) 추가.
- 수정: `src/00.Main/NexaOne.Server/Gateway/AuthController.cs` — logout·change-password·register·me 엔드포인트 추가.
- 수정: `db/queries-auth/mssql/SYS.xml`·`db/queries-auth/sqlite/SYS.xml` — SYS.UpdatePassword·SYS.InsertUser 추가.
- 생성: `test/NexaOne.ServerTests/GatewayAuthCompletenessTests.cs` — logout/change-password/register/me E2E.

---

## Task 1: 신규 인증 쿼리 + 서비스 메서드 + DTO + AuthOutcome

**Files:** db/queries-auth/{mssql,sqlite}/SYS.xml, AuthDtos.cs, AuthOutcome.cs, GatewayLoginService.cs

- [ ] **Step 1: 인증 쿼리 추가 (양 방언, kind 미표기 — 내부 실행용)**

`db/queries-auth/mssql/SYS.xml`의 `</queries>` 직전에 추가:
```xml
  <!-- 비밀번호 변경(change-password) — 해시 교체 + 상태 Normal 복귀. UPDATED_BY는 본인. -->
  <query id="SYS.UpdatePassword">
    <statement><![CDATA[
UPDATE SYS_USER SET
    PASSWORD_HASH = @passwordHash,
    PASSWORD_STATE = 'Normal',
    UPDATED_BY = @userId, UPDATED_AT = @utcNow
WHERE USER_ID = @userId AND IS_DELETED = 0
]]></statement>
  </query>

  <!-- 사용자 등록(register, admin). PASSWORD_STATE='Create'=최초 로그인 시 변경 강제(관리자 발급 임시 비번 관례). -->
  <query id="SYS.InsertUser">
    <statement><![CDATA[
INSERT INTO SYS_USER
    (USER_ID, USER_NAME, PASSWORD_HASH, EMAIL, ROLE_ID, LANGUAGE,
     IS_ACTIVE, IS_DELETED, PASSWORD_STATE, FAIL_COUNT,
     CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
VALUES
    (@userId, @userName, @passwordHash, @email, @roleId, @language,
     1, 0, 'Create', 0,
     @currentUser, @utcNow, @currentUser, @utcNow)
]]></statement>
  </query>
```
`db/queries-auth/sqlite/SYS.xml`에도 **동일 내용**을 `</queries>` 직전에 추가(SQLite는 NOLOCK 없음 — 위 SQL은 힌트가 없어 양 방언 동일). 두 파일 동기화 필수.

- [ ] **Step 2: AuthDtos.cs — 신규 DTO 추가**

[AuthDtos.cs](../../../src/00.Main/NexaOne.Server/Gateway/AuthDtos.cs) 끝(LoginFailureReasons 앞)에 추가:
```csharp
public record LogoutRequest(string RefreshToken);

public record ChangePasswordRequest(string CurrentPassword, string NewPassword, string ConfirmPassword);

// 관리자 사용자 등록. PlantId/Language는 선택(기본). RoleId는 필수(권한 합성 기반).
public record RegisterRequest(
    string UserId, string UserName, string Password, string Email, string RoleId, string Language = "KoKr");

public record CurrentUserResponse(string? UserId, string? UserName, string? PlantId, IReadOnlyList<string> Roles);
```

- [ ] **Step 3: AuthOutcome.cs — 상태코드 팩토리 추가**

[AuthOutcome.cs](../../../src/00.Main/NexaOne.Server/Gateway/AuthOutcome.cs)에 추가(기존 using NexaOne.Common 활용):
```csharp
    public static AuthOutcome NoContent() => new(new NoContentResult());

    public static AuthOutcome BadRequest(Error error) => new(new BadRequestObjectResult(error));

    public static AuthOutcome Conflict(Error error) => new(new ConflictObjectResult(error));
```

- [ ] **Step 4: GatewayLoginService.cs — ChangePasswordAsync·RegisterAsync·공개 사용자 조회 추가**

기존 private 헬퍼(QuerySingleAsync/ExecuteAsync/Get/ToBool/ToStr/PasswordHasher 사용 — 이미 `using NexaOne.Common`)를 재사용한다. `IRefreshTokenStore`를 주입받으므로 토큰 폐기·발급 가능. 클래스에 추가:

```csharp
    /// <summary>현재 사용자 1행 조회(컨트롤러가 me/권한 표시에 사용). 없으면 null.</summary>
    public Task<Dictionary<string, object?>?> GetUserRowAsync(string userId, CancellationToken ct)
        => QuerySingleAsync("SYS.AuthUserById", new() { ["userId"] = userId }, ct);

    /// <summary>비밀번호 변경 — 현재 비번 검증 후 강화 해시 저장, 전 토큰 폐기, 새 토큰 재발급(pwdChange 없음).
    /// 정책 검증은 컨트롤러(평문 보유)가 선행한다. 본인 userId는 토큰에서 전달받는다.</summary>
    public async Task<AuthOutcome> ChangePasswordAsync(string userId, string currentPassword, string newPassword,
        string plantId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var row = await QuerySingleAsync("SYS.AuthUserById", new() { ["userId"] = userId }, ct);
        if (row is null || ToBool(Get(row, "IS_DELETED")))
            return AuthOutcome.BadRequest(new Error("USER_NOT_FOUND", "User not found.", ErrorType.Validation));

        if (!PasswordHasher.Verify(currentPassword, ToStr(Get(row, "PASSWORD_HASH"))))
            return AuthOutcome.BadRequest(new Error("INVALID_CURRENT_PASSWORD", "현재 비밀번호가 올바르지 않습니다.", ErrorType.Validation));

        await ExecuteAsync("SYS.UpdatePassword", new()
        {
            ["userId"] = userId,
            ["passwordHash"] = PasswordHasher.Hash(newPassword),
            ["utcNow"] = now,
        }, ct);

        await _tokenStore.RevokeAllByUserAsync(userId);   // §19.2.4-7 다른 기기 세션 만료

        var userName = ToStr(Get(row, "USER_NAME"));
        var roleId = ToStr(Get(row, "ROLE_ID"));
        var perms = EffectivePermissions(roleId, ToNullableStr(Get(row, "PERMISSIONS")));
        // requireChange=false(변경 완료) 새 토큰 — pwdChange 클레임 제거.
        var accessToken = _jwt.GenerateAccessToken(userId, userName, plantId, new[] { roleId }, false, perms);
        var refreshToken = await _tokenStore.IssueAsync(userId);
        return AuthOutcome.Ok(new TokenRefreshResponse(accessToken, refreshToken));
    }

    /// <summary>관리자 사용자 등록 — 중복 검사 후 INSERT(PASSWORD_STATE='Create'=최초 변경 강제). 권한 게이트는 컨트롤러.
    /// 정책 검증은 컨트롤러가 선행. createdBy는 등록 수행 관리자 userId.</summary>
    public async Task<AuthOutcome> RegisterAsync(RegisterRequest req, string createdBy, CancellationToken ct)
    {
        var existing = await QuerySingleAsync("SYS.AuthUserById", new() { ["userId"] = req.UserId }, ct);
        if (existing is not null)
            return AuthOutcome.Conflict(new Error("USER_ALREADY_EXISTS", $"User '{req.UserId}' already exists.", ErrorType.Conflict));

        await ExecuteAsync("SYS.InsertUser", new()
        {
            ["userId"] = req.UserId,
            ["userName"] = req.UserName,
            ["passwordHash"] = PasswordHasher.Hash(req.Password),
            ["email"] = req.Email,
            ["roleId"] = req.RoleId,
            ["language"] = string.IsNullOrWhiteSpace(req.Language) ? "KoKr" : req.Language,
            ["currentUser"] = createdBy,
            ["utcNow"] = DateTime.UtcNow,
        }, ct);
        return AuthOutcome.Ok(new { userId = req.UserId });
    }
```
주의: `ErrorType.Conflict`가 존재하는지 확인하고(없으면 `ErrorType.Validation` 사용), `Error` 생성자 시그니처(code, description, type)를 기존 사용처(AuthOutcome.cs)와 맞춰라. `QuerySingleAsync`/`ExecuteAsync`/`Get`/`ToBool`/`ToStr`/`ToNullableStr`/`EffectivePermissions`/`_jwt`/`_tokenStore`는 기존 멤버다.

- [ ] **Step 5: 빌드 확인**

Run: `dotnet build src/00.Main/NexaOne.Server/NexaOne.Server.csproj -c Debug --nologo`
Expected: 0 errors.

- [ ] **Step 6: 커밋**

커밋 메시지: `feat(auth): change-password·register 쿼리/서비스 + AuthOutcome 상태코드 확장`
(PowerShell BOM-free, `git add` 명시 경로만: db/queries-auth/mssql/SYS.xml, db/queries-auth/sqlite/SYS.xml, AuthDtos.cs, AuthOutcome.cs, GatewayLoginService.cs.)

---

## Task 2: AuthController 엔드포인트 (logout·change-password·register·me)

**Files:** `src/00.Main/NexaOne.Server/Gateway/AuthController.cs`

- [ ] **Step 1: 엔드포인트 추가**

[AuthController.cs](../../../src/00.Main/NexaOne.Server/Gateway/AuthController.cs)에 추가. 생성자에 `IRefreshTokenStore`를 주입(logout용)하고, 권한 검사 헬퍼를 추가. using 추가: `Microsoft.AspNetCore.Authorization`, `NexaOne.Common.Security`, `System.Security.Claims`, `NexaOne.Application.Auth`(IRefreshTokenStore).

생성자 변경:
```csharp
    private readonly GatewayLoginService _login;
    private readonly IJwtService _jwt;
    private readonly IRefreshTokenStore _tokens;

    public AuthController(GatewayLoginService login, IJwtService jwt, IRefreshTokenStore tokens)
    {
        _login = login;
        _jwt = jwt;
        _tokens = tokens;
    }
```

엔드포인트 추가(클래스 내부, refresh 다음):
```csharp
    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Logout([FromBody] LogoutRequest request)
    {
        // 폐기 대상 userId는 본문이 아니라 토큰에서 — 임의 사용자 토큰 폐기(IDOR/DoS) 방지.
        var userId = CurrentUserId;
        await _tokens.RevokeAsync(userId, request.RefreshToken);
        return NoContent();
    }

    [HttpPost("change-password")]
    [Authorize]
    [ProducesResponseType<TokenRefreshResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken ct)
    {
        if (request.NewPassword != request.ConfirmPassword)
            return BadRequest(new Error("Auth.PasswordMismatch", "Passwords do not match.", ErrorType.Validation));

        var userId = CurrentUserId;
        var row = await _login.GetUserRowAsync(userId, ct);
        if (row is null)
            return BadRequest(new Error("USER_NOT_FOUND", "User not found.", ErrorType.Validation));

        // §19.2.2 서버 최종 정책 검증(userId/이름/이메일 포함 금지). 평문은 컨트롤러에만 존재.
        var userName = row.TryGetValue("USER_NAME", out var un) ? un?.ToString() : null;
        var email = row.TryGetValue("EMAIL", out var em) ? em?.ToString() : null;
        var violation = PasswordPolicy.Validate(request.NewPassword, userId, userName, email);
        if (violation is not null)
            return BadRequest(new Error(PasswordPolicy.ErrorCode, violation, ErrorType.Validation));

        var plantId = User.FindFirst("plantId")?.Value ?? "DEFAULT";
        var outcome = await _login.ChangePasswordAsync(userId, request.CurrentPassword, request.NewPassword, plantId, ct);
        return outcome.Result;
    }

    [HttpPost("register")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request, CancellationToken ct)
    {
        if (!HasPermission(Permissions.SysManage)) return Forbid();

        var violation = PasswordPolicy.Validate(request.Password, request.UserId, request.UserName, request.Email);
        if (violation is not null)
            return BadRequest(new Error(PasswordPolicy.ErrorCode, violation, ErrorType.Validation));
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.RoleId))
            return BadRequest(new Error("INVALID_REGISTRATION", "UserId와 RoleId는 필수입니다.", ErrorType.Validation));

        var outcome = await _login.RegisterAsync(request, CurrentUserId, ct);
        return outcome.Result;
    }

    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType<CurrentUserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult Me()
    {
        var roles = User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
        return Ok(new CurrentUserResponse(CurrentUserId, User.Identity?.Name, User.FindFirst("plantId")?.Value, roles));
    }

    private string CurrentUserId =>
        User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value ?? string.Empty;

    private bool HasPermission(string permission) =>
        User.FindAll(Permissions.ClaimType).Any(c =>
            c.Value == Permissions.All || string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));
```
주의: `IRefreshTokenStore`는 이미 DI 등록됨(AuthServiceExtensions). 생성자 주입만 추가하면 된다. `Permissions.SysManage`/`Permissions.All`/`Permissions.ClaimType` 실재 확인(QueryCatalogController 사용처와 동일). `ErrorType` enum 값(Validation/Conflict) 실재 확인.

- [ ] **Step 2: 빌드**

Run: `dotnet build src/00.Main/NexaOne.Server/NexaOne.Server.csproj -c Debug --nologo` → 0 errors.

- [ ] **Step 3: 커밋**

커밋 메시지: `feat(auth): 호스트 AuthController logout·change-password·register·me 엔드포인트`

---

## Task 3: E2E 테스트 (logout·change-password·register·me)

**Files:** `test/NexaOne.ServerTests/GatewayAuthCompletenessTests.cs`

[GatewayAuthE2ETests.cs](../../../test/NexaOne.ServerTests/GatewayAuthE2ETests.cs)의 팩토리/JWT 패턴(modules-OFF, SQLite, Jwt 설정)을 그대로 따른다. 핵심 시나리오:

- [ ] **Step 1: 테스트 작성 (대표 시나리오 — GatewayAuthE2ETests 팩토리 재사용)**

`GatewayAuthCompletenessTests.cs`에 다음을 검증한다(기존 E2E 팩토리 구조를 복제하되 클래스명/DB만 분리):
1. **logout**: admin/admin 로그인 → refreshToken 획득 → `POST /auth/logout {refreshToken}`(Bearer) → 204 → 같은 refreshToken으로 `POST /auth/refresh` → 401(폐기됨).
2. **change-password 성공**: 로그인 → `POST /auth/change-password {current:"admin", new:"NewP@ssw0rd!", confirm:"NewP@ssw0rd!"}`(Bearer) → 200 + 새 access/refresh. 이후 새 비번으로 재로그인 200, 구 비번 로그인 401. (테스트 종료 후 상태 격리를 위해 전용 SQLite DB 사용.)
3. **change-password 검증 실패**: 현재 비번 오류 → 400 `INVALID_CURRENT_PASSWORD`; 정책 위반(new="weak") → 400 `PASSWORD_POLICY_VIOLATION`; new≠confirm → 400 `Auth.PasswordMismatch`.
4. **register**: admin(sys:manage 보유 토큰)으로 `POST /auth/register {userId:"u1", userName:"U1", password:"Reg!Pass99", email:"u1@x.com", roleId:"OPERATOR"}` → 200 → 신규 사용자 u1은 PASSWORD_STATE='Create'라 로그인 시 requirePasswordChange=true. 중복 등록 → 409. **권한 없는 토큰**(sys:manage 미보유)으로 register → 403.
5. **me**: 로그인 토큰으로 `GET /auth/me` → 200, userId=admin.

주의: admin 시드는 SQLite 부트스트랩(개발 SQLite 부트스트랩, Program.cs:189-194)이 db/migrations + V001 admin/admin 시드를 생성하므로, 팩토리가 `Database:Provider=Sqlite` + `ASPNETCORE_ENVIRONMENT=Development` + 고유 ConnectionStrings:NexaOne로 기동하면 admin 로그인이 가능하다(GatewayAuthE2ETests가 이미 이 방식). 각 테스트 클래스는 고유 DB 파일로 격리(상태 변이 테스트 간섭 방지). sys:manage 권한 토큰은 admin 로그인으로 얻거나, GatewayAuthE2ETests의 JWT 민팅 헬퍼로 `permission=sys:manage` 클레임을 직접 부여(서버 시크릿 일치 필요 — 팩토리 Jwt:SecretKey 사용).

- [ ] **Step 2: 테스트 실행**

Run: `dotnet test test/NexaOne.ServerTests/NexaOne.ServerTests.csproj -c Debug --nologo`
Expected: 기존(46) + 신규(시나리오 수) 전부 통과, 0 실패.
주의(상태 격리): change-password/register는 SYS_USER를 변이하므로 각 테스트가 **고유 SQLite DB**를 쓰거나, 변이 후 원복되도록 설계. admin 비번을 바꾸는 테스트는 전용 DB로 분리해 다른 테스트의 admin/admin 로그인을 깨지 않게 한다.

- [ ] **Step 3: 커밋**

커밋 메시지: `test(auth): logout·change-password·register·me E2E(폐기·정책·권한·중복)`

---

## Task 4 (컨트롤러 직접 수행): 회귀 + 최종 리뷰 + ff-merge

- [ ] Run: `dotnet test test/NexaOne.ServerTests/NexaOne.ServerTests.csproj -c Debug --nologo` → 전부 통과.
- [ ] 최종 통합 리뷰(서브에이전트) → `superpowers:finishing-a-development-branch`로 main ff-merge(sln 가드, git에 `2>&1` 금지, push 안 함).

---

## Self-Review
- **커버리지**: logout(Task2)·change-password(Task1 서비스+Task2 컨트롤러+Task3 테스트)·register(동)·me(Task2). forgot/reset 제외 명시. ✓
- **보안**: logout/change-password userId는 토큰 출처(IDOR 방지), register는 sys:manage 게이트, 정책은 서버 최종 검증, change 성공 시 전 토큰 폐기. PASSWORD_HASH는 격리 레지스트리로만 접근(공개 게이트웨이 미노출). ✓
- **타입 일관성**: AuthOutcome 신규 팩토리(NoContent/BadRequest/Conflict) ↔ 컨트롤러 사용. DTO 필드명 ↔ 서비스/쿼리 파라미터(@userId/@passwordHash/@email/@roleId/@language/@currentUser/@utcNow) 일치. `ErrorType`/`Permissions` 상수 실재는 구현자가 빌드로 확인. ✓
- **상태 격리(테스트)**: SYS_USER 변이 테스트는 고유 SQLite DB로 격리 — 명시. ✓
- **알려진 한계**: forgot/reset-password 미이식(이메일 인프라). register는 PASSWORD_STATE='Create'(최초 변경 강제) — 등록 사용자는 첫 로그인 후 change-password 필요.
