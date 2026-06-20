# 통합 호스트 Phase 3b — 인증/토큰 발급(게이트웨이식, 무-브리지) 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 통합 호스트(NexaOne.Server)가 plugin↔DI 브리지 없이 로그인/리프레시 토큰을 직접 발급하도록, Default-ALC 타입(암호/JWT) + **격리된** 명명 쿼리로 인증 경로를 구현한다.

**Architecture:** `IJwtService`/`IRefreshTokenStore`(+인메모리)를 NexaOne.API → NexaOne.Application(Default-ALC)로 이동한다. 호스트의 `GatewayLoginService`/`SysRefreshTokenStore`가 `IRuleDispatcher` + **전용(격리) 쿼리 레지스트리**로 SYS 데이터에 접근하고, `PasswordHasher`/`RolePermissionDefaults`(이미 Default-ALC)로 검증·권한을 재현한다. 운영 SYS UserRepository/UserService의 보안 의미론(잠금 패리티·rehash·감사)을 SQL로 1:1 재현한다.

**Tech Stack:** C#/.NET 8, ASP.NET Core 8(컨트롤러+RateLimiter), Dapper(IRuleDispatcher), 파일 명명 쿼리(mssql/sqlite), JWT(HMAC), PBKDF2, xUnit + WebApplicationFactory + SQLite.

---

## 설계 근거 요약(상위 설계 0b5128b 반영 + 적대검증 HIGH 수정)

상위 설계: [Phase 3b 인증 설계](../specs/2026-06-20-unified-host-phase3b-auth-design.md). 본 계획이 추가로 확정/보정한 항목:

1. **잠금 패리티 SQL**: 운영 `UserRepository.RecordLoginFailureAsync`의 원자 CASE UPDATE를 **그대로** 명명 쿼리로 옮긴다(무조건 증가 금지; 만료 잠금이면 1로 리셋, 임계 도달 시 잠금).
2. **PASSWORD_HASH 폭 확장**: `NVARCHAR(64)`→`NVARCHAR(255)`(PBKDF2 ~83자가 64를 초과해 MSSQL rehash 쓰기 throw — SQLite는 길이 무개념이라 가려진 실버그).
3. **IQueryRegistry 해석**: `IRuleDispatcher`는 raw SQL을 받으므로, 명명 쿼리 id→SQL은 레지스트리로 해석 후 디스패치한다.
4. **단일 조건부 UPDATE 원자성**: 게이트웨이에 다중문 트랜잭션 원시가 없으므로 잠금/성공/회전을 각각 단일 UPDATE로 원자화한다.
5. **레이트리미터**: 호스트에 `"auth"`(IP당 10/min) + 전역(100/min) 정책 도입(`RateLimiting:Enabled` 게이트).
6. **NULL PERMISSIONS**: 역할 소프트삭제 등으로 PERMISSIONS가 NULL이면 빈 집합으로 두되 `RolePermissionDefaults`는 적용(ADMIN `*` 보존).
7. **상수 중앙화(드리프트 차단)**: 잠금 임계(5)·기간(30분)을 `NexaOne.Common.Security.AccountLockoutPolicy`로 중앙화하고 SYS `User`가 이를 위임 참조하게 한다 — 호스트 SQL과 SYS 도메인이 **단일 출처**를 공유.
8. **🔴 신규(본 계획에서 식별한 HIGH): 인증 쿼리 격리.** SYS 인증 쿼리를 **공개 게이트웨이 레지스트리(db/queries)에 넣지 않는다.** 넣으면 임의 인증 사용자가 `POST /api/v1/query/SYS.AuthUserById`로 **타인의 PASSWORD_HASH를 조회**할 수 있다(QueryGatewayController는 read에 권한을 요구하지 않음). 따라서 별도 루트 **`db/queries-auth/{mssql,sqlite}/SYS.xml`**에 두고, 호스트 인증 서비스만 쓰는 **전용 FileQueryRegistry 인스턴스**로 로드한다(공개 `IQueryRegistry` 싱글톤과 분리). 설계 §5의 "db/queries/.../SYS.xml"를 본 항목으로 보정한다.
9. **Redis 변형 비이동(설계 §10.1 보정)**: 설계는 "(+Redis 변형) 이동"이라 적었으나, 호스트는 Redis가 아니라 **DB-backed SysRefreshTokenStore**를 쓰므로 `RedisRefreshTokenStore`는 NexaOne.API에 **남긴다**(이동 시 NexaOne.Application→NexaOne.Driver.Redis 의존이 생겨 기반 공유 어셈블리를 오염시킴). 이동 대상은 `IJwtService`/`JwtService`/`IRefreshTokenStore`/`RefreshTokenStore`(인메모리) 4개로 한정.

---

## File Structure

**이동(NexaOne.API.Services → NexaOne.Application.Auth):**
- `src/02.Backend/NexaOne.Application/Auth/IJwtService.cs` (이동, ns 변경)
- `src/02.Backend/NexaOne.Application/Auth/JwtService.cs` (이동, ns 변경)
- `src/02.Backend/NexaOne.Application/Auth/IRefreshTokenStore.cs` (이동, ns 변경)
- `src/02.Backend/NexaOne.Application/Auth/RefreshTokenStore.cs` (이동, ns 변경 — 인메모리)

**신규(공통):**
- `src/02.Backend/NexaOne.Common/Security/AccountLockoutPolicy.cs`

**신규(DB):**
- `db/migrations/V034__SYS_REFRESH_TOKEN.sql`
- `db/queries-auth/mssql/SYS.xml`
- `db/queries-auth/sqlite/SYS.xml`

**신규(호스트):**
- `src/00.Main/NexaOne.Server/Gateway/AuthDtos.cs` (LoginRequest/LoginResponse/RefreshRequest/TokenRefreshResponse/LoginOutcome — 호스트 로컬, API와 동일 JSON 계약)
- `src/00.Main/NexaOne.Server/Gateway/SysRefreshTokenStore.cs`
- `src/00.Main/NexaOne.Server/Gateway/GatewayLoginService.cs`
- `src/00.Main/NexaOne.Server/Gateway/AuthController.cs`
- `src/00.Main/NexaOne.Server/Gateway/AuthServiceExtensions.cs` (AddNexaOneAuth)

**수정:**
- `src/02.Backend/NexaOne.Application/NexaOne.Application.csproj` (JWT 패키지)
- `src/04.Modules/NexaOne.SYS/Domain/User.cs` (상수 위임)
- `src/02.Backend/NexaOne.API/Extensions/ServiceCollectionExtensions.cs`, `Controllers/AuthController.cs`, `Middleware/PasswordChangeRequiredMiddleware.cs`, `Services/RedisRefreshTokenStore.cs` (using 갱신)
- `src/02.Backend/NexaOne.Infrastructure/Persistence/SqliteSchemaInitializer.cs` (ALTER COLUMN strip 규칙)
- `src/00.Main/NexaOne.Server/Program.cs` (AddNexaOneAuth + RateLimiter)
- `src/00.Main/NexaOne.Server/NexaOne.Server.csproj` (db/queries-auth 복사)

**테스트:**
- `test/NexaOne.ServerTests/GatewayAuthE2ETests.cs` (기능 테스트, RateLimiting OFF)
- `test/NexaOne.ServerTests/GatewayAuthRateLimitTests.cs` (429, RateLimiting ON)

---

## Task 1: ALC 이동 + 잠금 상수 중앙화

**Files:**
- Create: `src/02.Backend/NexaOne.Common/Security/AccountLockoutPolicy.cs`
- Modify: `src/02.Backend/NexaOne.Application/NexaOne.Application.csproj`
- Move: `src/02.Backend/NexaOne.API/Services/{IJwtService,JwtService,IRefreshTokenStore,RefreshTokenStore}.cs` → `src/02.Backend/NexaOne.Application/Auth/`
- Modify: `src/04.Modules/NexaOne.SYS/Domain/User.cs`
- Modify: `src/02.Backend/NexaOne.API/Extensions/ServiceCollectionExtensions.cs`, `Controllers/AuthController.cs`, `Middleware/PasswordChangeRequiredMiddleware.cs`, `Services/RedisRefreshTokenStore.cs`

- [ ] **Step 1: 잠금 정책 상수 신규 작성**

`src/02.Backend/NexaOne.Common/Security/AccountLockoutPolicy.cs`:
```csharp
namespace NexaOne.Common.Security;

/// <summary>§20.10 — 연속 로그인 실패 잠금 정책(단일 출처). SYS 도메인(User)과 통합 호스트 인증 SQL이
/// 동일 값을 공유해 드리프트를 차단한다(Phase 3b 설계 §9 완화).</summary>
public static class AccountLockoutPolicy
{
    /// <summary>연속 실패 잠금 임계값(5회).</summary>
    public const int MaxConsecutiveFailures = 5;

    /// <summary>계정 잠금 시간(30분).</summary>
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(30);
}
```

- [ ] **Step 2: SYS `User`가 상수를 위임 참조하도록 변경(동작 불변)**

`src/04.Modules/NexaOne.SYS/Domain/User.cs` 상단 `using`에 `using NexaOne.Common.Security;`를 추가하고, 두 상수 정의를 위임으로 교체:
```csharp
    /// <summary>§20.10 — 연속 실패 잠금 임계값 (5회). 단일 출처: AccountLockoutPolicy.</summary>
    public const int MaxConsecutiveFailures = AccountLockoutPolicy.MaxConsecutiveFailures;

    /// <summary>§20.10 — 계정 잠금 시간 (30분). 단일 출처: AccountLockoutPolicy.</summary>
    public static readonly TimeSpan LockDuration = AccountLockoutPolicy.LockDuration;
```
(`User`는 이미 `using NexaOne.Common;`가 있다. `const int = const int`, `static readonly TimeSpan = static readonly TimeSpan`은 합법.)

- [ ] **Step 3: NexaOne.Application에 JWT 패키지 추가**

`src/02.Backend/NexaOne.Application/NexaOne.Application.csproj`의 `<ItemGroup>`(PackageReference)에 추가:
```xml
    <PackageReference Include="System.IdentityModel.Tokens.Jwt" Version="8.*" />
```
(이 패키지가 `JwtSecurityToken`/`JwtSecurityTokenHandler` + 전이로 `Microsoft.IdentityModel.Tokens`(`SymmetricSecurityKey`/`SigningCredentials`/`TokenValidationParameters`)를 제공한다. 빌드 시 NexaOne.API의 JwtBearer 8.* 전이 버전과 충돌(다운그레이드 경고)이 나면 해당 해석 버전으로 정렬한다.)

- [ ] **Step 4: 4개 파일 이동 + 네임스페이스 변경**

`src/02.Backend/NexaOne.API/Services/`의 `IJwtService.cs`, `JwtService.cs`, `IRefreshTokenStore.cs`, `RefreshTokenStore.cs`를 `src/02.Backend/NexaOne.Application/Auth/`로 이동(파일 내용 유지)하고, 각 파일의 `namespace NexaOne.API.Services;`를 `namespace NexaOne.Application.Auth;`로 변경한다. `RefreshTokenStore.cs`는 `IConfiguration` 사용 → 파일 상단에 `using Microsoft.Extensions.Configuration;`를 추가(API에서는 글로벌 using으로 가려졌으나 Application엔 없음). `JwtService.cs`의 `NexaOne.Common.Security.Permissions` 전체경로 참조는 그대로 둔다(Application은 Common 참조).

PowerShell 이동 예:
```powershell
$src = "src/02.Backend/NexaOne.API/Services"
$dst = "src/02.Backend/NexaOne.Application/Auth"
New-Item -ItemType Directory -Force $dst | Out-Null
foreach ($f in "IJwtService.cs","JwtService.cs","IRefreshTokenStore.cs","RefreshTokenStore.cs") {
    git mv "$src/$f" "$dst/$f"
}
```
그 후 4개 파일의 `namespace` 줄을 `NexaOne.Application.Auth`로 바꾸고 `RefreshTokenStore.cs`에 `using Microsoft.Extensions.Configuration;`를 추가한다.

- [ ] **Step 5: NexaOne.API 참조 갱신**

다음 4개 파일에서 이동 타입(`IJwtService`/`JwtService`/`IRefreshTokenStore`/`RefreshTokenStore`)을 쓰는 곳에 `using NexaOne.Application.Auth;`를 추가한다(기존 `using NexaOne.API.Services;`는 다른 API 서비스 때문에 유지):
  - `src/02.Backend/NexaOne.API/Controllers/AuthController.cs`
  - `src/02.Backend/NexaOne.API/Extensions/ServiceCollectionExtensions.cs`
  - `src/02.Backend/NexaOne.API/Middleware/PasswordChangeRequiredMiddleware.cs` (`JwtService.PasswordChangeClaim`)
  - `src/02.Backend/NexaOne.API/Services/RedisRefreshTokenStore.cs` (`IRefreshTokenStore`/`IJwtService` 구현·주입 — `namespace NexaOne.API.Services` 유지, 상단에 `using NexaOne.Application.Auth;` 추가)

확인 명령(이동 타입의 잔여 참조를 빠짐없이 찾기):
```powershell
# NexaOne.API에서 이동 타입을 참조하나 using이 없을 수 있는 파일 점검
Select-String -Path src/02.Backend/NexaOne.API -Pattern "IJwtService|IRefreshTokenStore|\bJwtService\b|\bRefreshTokenStore\b" -List
```
나온 파일마다 `using NexaOne.Application.Auth;`가 있는지 확인하고 없으면 추가한다(특히 컨트롤러/미들웨어/Extensions; `UserRegistrationController` 등은 이동 타입 미사용이면 변경 불필요).

- [ ] **Step 6: 빌드 + 전체 회귀**

```powershell
dotnet build NexaMes.sln -c Debug
```
Expected: 0 error, 0 warning.
```powershell
dotnet test test/NexaOne.UnitTests/NexaOne.UnitTests.csproj -c Debug
dotnet test test/NexaOne.IntegrationTests/NexaOne.IntegrationTests.csproj -c Debug
dotnet test test/NexaOne.ServerTests/NexaOne.ServerTests.csproj -c Debug
```
Expected: 기존 그린 그대로(단위 1090, 통합 286/+1 skip, ServerTests 12). NexaOne.API 인증 동작은 타입 위치만 바뀌고 의미 동일이라 회귀 없어야 한다.

- [ ] **Step 7: Commit**
```powershell
git add -A
$m = "refactor(auth): JwtService/RefreshTokenStore를 NexaOne.Application(Default-ALC)로 이동 + 잠금 상수 중앙화(AccountLockoutPolicy)"
$f = [IO.Path]::GetTempFileName(); [IO.File]::WriteAllText($f,$m,(New-Object System.Text.UTF8Encoding($false))); git commit -F $f; Remove-Item $f
```

---

## Task 2: V034 마이그레이션 + SQLite ALTER COLUMN 변환

**Files:**
- Create: `db/migrations/V034__SYS_REFRESH_TOKEN.sql`
- Modify: `src/02.Backend/NexaOne.Infrastructure/Persistence/SqliteSchemaInitializer.cs`

- [ ] **Step 1: V034 마이그레이션 작성**

`db/migrations/V034__SYS_REFRESH_TOKEN.sql`:
```sql
-- 통합 호스트 Phase 3b — 리프레시 토큰 영속(게이트웨이식 인증) + PASSWORD_HASH 폭 확장.
-- SYS_REFRESH_TOKEN: 평문 토큰을 저장하지 않고 SHA-256 해시(TOKEN_HASH)만 저장한다.
CREATE TABLE SYS_REFRESH_TOKEN (
    TOKEN_ID    NVARCHAR(50)    NOT NULL,
    USER_ID     NVARCHAR(50)    NOT NULL,
    TOKEN_HASH  NVARCHAR(100)   NOT NULL,
    EXPIRES_AT  DATETIME2       NOT NULL,
    REVOKED_AT  DATETIME2       NULL,
    CREATED_AT  DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT PK_SYS_REFRESH_TOKEN PRIMARY KEY (TOKEN_ID)
);

CREATE INDEX IX_SYS_REFRESH_TOKEN_LOOKUP ON SYS_REFRESH_TOKEN (USER_ID, TOKEN_HASH);

-- PBKDF2 강화 해시 문자열(약 83자)이 기존 NVARCHAR(64)를 초과해 MSSQL에서 rehash 쓰기가 throw하므로
-- rehash-on-login 활성 전 선행 확장한다(SQLite는 길이 무개념이라 가려진 MSSQL 실버그).
ALTER TABLE SYS_USER ALTER COLUMN PASSWORD_HASH NVARCHAR(255) NOT NULL;
```

- [ ] **Step 2: SQLite 변환기에 ALTER COLUMN 제거 규칙 추가**

`src/02.Backend/NexaOne.Infrastructure/Persistence/SqliteSchemaInitializer.cs`의 `ToSqlite`에서, 다중컬럼 `ALTER ... ADD` 변환 규칙 **직전**에 추가:
```csharp
        // MSSQL ALTER COLUMN(타입/길이 변경)은 SQLite 미지원 + TEXT는 길이 무개념이라 무의미 → 문장 제거(무해).
        s = Regex.Replace(s, @"ALTER\s+TABLE\s+\w+\s+ALTER\s+COLUMN\s+.+?;", "", O | RegexOptions.Singleline);
```
(기존 `ALTER ... ADD` 규칙은 `ADD`만 매칭하므로 본 규칙과 충돌하지 않는다. 기존 마이그레이션엔 `ALTER COLUMN`이 없어(확인됨) 영향 없음.)

- [ ] **Step 3: SQLite 부트스트랩 회귀 확인(V034 적용)**

ServerTests가 매 실행 시 새 SQLite DB로 전체 마이그레이션을 적용하므로, 기존 테스트가 그대로 통과하면 V034가 SQLite에서 정상 변환·적용된 것이다.
```powershell
dotnet test test/NexaOne.ServerTests/NexaOne.ServerTests.csproj -c Debug
dotnet test test/NexaOne.IntegrationTests/NexaOne.IntegrationTests.csproj -c Debug
```
Expected: 그린(SQLite 스키마 생성 단계에서 V034 실패 시 "SQLite 스키마 생성 실패 @ V034..." 예외로 빨갛게 드러남).

- [ ] **Step 4: Commit**
```powershell
git add -A
$m = "feat(db): V034 SYS_REFRESH_TOKEN + PASSWORD_HASH 폭 확장(64→255) + SQLite ALTER COLUMN 변환"
$f = [IO.Path]::GetTempFileName(); [IO.File]::WriteAllText($f,$m,(New-Object System.Text.UTF8Encoding($false))); git commit -F $f; Remove-Item $f
```

---

## Task 3: 격리된 SYS 인증 명명 쿼리(mssql + sqlite)

**Files:**
- Create: `db/queries-auth/mssql/SYS.xml`
- Create: `db/queries-auth/sqlite/SYS.xml`

> 이 쿼리들은 **공개 게이트웨이(db/queries)와 분리**된 `db/queries-auth`에 둔다(계획 §8 보정). 호스트 인증 서비스만 전용 레지스트리로 로드한다. `kind`/`requiredPermission` 속성을 두지 않는다 — 인증 서비스가 read/write를 직접 구분(QueryAsync/ExecuteAsync)하므로 불필요하고, `FileQueryRegistry`의 "write는 requiredPermission 필수" fail-fast도 회피한다.

- [ ] **Step 1: MSSQL 방언 작성**

`db/queries-auth/mssql/SYS.xml`:
```xml
<?xml version="1.0" encoding="utf-8"?>
<!-- SYS 인증 명명 쿼리(통합 호스트 Phase 3b, 격리 레지스트리 전용 — 공개 게이트웨이 미노출).
     운영 SYS UserRepository/UserService 의미론을 SQL로 1:1 재현한다. 수정 시 양쪽 동기화. -->
<queries module="SYS">

  <!-- 로그인 대상 1행(SYS_USER ⋈ 비삭제 SYS_ROLE). 역할 소프트삭제 시 R.* NULL → 권한은 기본 매핑만. -->
  <query id="SYS.AuthUserById">
    <statement><![CDATA[
SELECT U.USER_ID, U.USER_NAME, U.PASSWORD_HASH, U.EMAIL, U.ROLE_ID, U.LANGUAGE,
       U.IS_ACTIVE, U.IS_DELETED, U.LAST_LOGIN_AT, U.PASSWORD_STATE, U.FAIL_COUNT, U.LOCKED_UNTIL,
       R.ROLE_NAME, R.PERMISSIONS
FROM SYS_USER U WITH (NOLOCK)
LEFT JOIN SYS_ROLE R WITH (NOLOCK) ON R.ROLE_ID = U.ROLE_ID AND R.IS_DELETED = 0
WHERE U.USER_ID = @userId
]]></statement>
  </query>

  <!-- 실패 갱신 후 결과 잠금시각 재평가(운영 RecordLoginFailureAsync 사후 SELECT와 동일). -->
  <query id="SYS.GetLockedUntil">
    <statement><![CDATA[
SELECT LOCKED_UNTIL FROM SYS_USER WITH (NOLOCK) WHERE USER_ID = @userId
]]></statement>
  </query>

  <!-- 실패 기록(원자 단일 UPDATE, 잠금 패리티) — UserRepository.RecordLoginFailureAsync와 동일 CASE식. -->
  <query id="SYS.RecordLoginFailure">
    <statement><![CDATA[
UPDATE SYS_USER SET
    FAIL_COUNT = CASE WHEN LOCKED_UNTIL IS NOT NULL AND LOCKED_UNTIL <= @utcNow
                      THEN 1 ELSE FAIL_COUNT + 1 END,
    LOCKED_UNTIL = CASE
        WHEN (CASE WHEN LOCKED_UNTIL IS NOT NULL AND LOCKED_UNTIL <= @utcNow
                   THEN 1 ELSE FAIL_COUNT + 1 END) >= @maxFailures THEN @lockUntil
        WHEN LOCKED_UNTIL IS NOT NULL AND LOCKED_UNTIL <= @utcNow THEN NULL
        ELSE LOCKED_UNTIL END,
    UPDATED_BY = 'SYSTEM', UPDATED_AT = @utcNow
WHERE USER_ID = @userId
]]></statement>
  </query>

  <!-- 성공(단일 UPDATE: LAST_LOGIN_AT·실패카운터·잠금 동시; PASSWORD_STATE 보존; @passwordHash NULL이면 해시 미변경). -->
  <query id="SYS.RecordLoginSuccess">
    <statement><![CDATA[
UPDATE SYS_USER SET
    LAST_LOGIN_AT = @utcNow,
    FAIL_COUNT = 0,
    LOCKED_UNTIL = NULL,
    PASSWORD_HASH = COALESCE(@passwordHash, PASSWORD_HASH),
    UPDATED_BY = 'SYSTEM', UPDATED_AT = @utcNow
WHERE USER_ID = @userId
]]></statement>
  </query>

  <!-- 실패 이력(비존재 사용자도 기록 — FK 없음). FAILURE_ID는 호출부 생성. -->
  <query id="SYS.InsertLoginFailureHist">
    <statement><![CDATA[
INSERT INTO SYS_LOGIN_FAILURE_HIST
    (FAILURE_ID, USER_ID, IP_ADDRESS, USER_AGENT, FAILURE_REASON, OCCURRED_AT,
     CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
VALUES
    (@failureId, @userId, @ipAddress, @userAgent, @failureReason, @utcNow,
     'SYSTEM', @utcNow, 'SYSTEM', @utcNow)
]]></statement>
  </query>

  <!-- 리프레시 토큰 발급(해시 저장). TOKEN_ID는 호출부 생성. -->
  <query id="SYS.InsertRefreshToken">
    <statement><![CDATA[
INSERT INTO SYS_REFRESH_TOKEN
    (TOKEN_ID, USER_ID, TOKEN_HASH, EXPIRES_AT, REVOKED_AT, CREATED_AT)
VALUES
    (@tokenId, @userId, @tokenHash, @expiresAt, NULL, @utcNow)
]]></statement>
  </query>

  <!-- 유효성(미폐기·미만료) 1행 존재 = 유효. -->
  <query id="SYS.ValidateRefreshToken">
    <statement><![CDATA[
SELECT TOKEN_ID FROM SYS_REFRESH_TOKEN WITH (NOLOCK)
WHERE USER_ID = @userId AND TOKEN_HASH = @tokenHash
  AND REVOKED_AT IS NULL AND EXPIRES_AT > @utcNow
]]></statement>
  </query>

  <!-- 활성 토큰만 조건부 폐기 → 영향행수 1=회전 성공, 0=이미 폐기/만료(재생 탐지). -->
  <query id="SYS.RevokeRefreshTokenIfActive">
    <statement><![CDATA[
UPDATE SYS_REFRESH_TOKEN SET REVOKED_AT = @utcNow
WHERE USER_ID = @userId AND TOKEN_HASH = @tokenHash
  AND REVOKED_AT IS NULL AND EXPIRES_AT > @utcNow
]]></statement>
  </query>

  <!-- 사용자 전체 토큰 폐기. -->
  <query id="SYS.RevokeAllRefreshTokens">
    <statement><![CDATA[
UPDATE SYS_REFRESH_TOKEN SET REVOKED_AT = @utcNow
WHERE USER_ID = @userId AND REVOKED_AT IS NULL
]]></statement>
  </query>

</queries>
```

- [ ] **Step 2: SQLite 방언 작성**

`db/queries-auth/sqlite/SYS.xml`: MSSQL과 동일하되 **`WITH (NOLOCK)` 제거**(3곳: AuthUserById의 U/R, GetLockedUntil, ValidateRefreshToken). 그 외 SQL/파라미터/CDATA 동일. (`COALESCE`/`CASE`/`>`/`IS NULL`은 SQLite 동일 동작.)
```xml
<?xml version="1.0" encoding="utf-8"?>
<!-- SYS 인증 명명 쿼리(SQLite 방언) — mssql/SYS.xml와 동일 의미, NOLOCK 힌트만 제거. -->
<queries module="SYS">

  <query id="SYS.AuthUserById">
    <statement><![CDATA[
SELECT U.USER_ID, U.USER_NAME, U.PASSWORD_HASH, U.EMAIL, U.ROLE_ID, U.LANGUAGE,
       U.IS_ACTIVE, U.IS_DELETED, U.LAST_LOGIN_AT, U.PASSWORD_STATE, U.FAIL_COUNT, U.LOCKED_UNTIL,
       R.ROLE_NAME, R.PERMISSIONS
FROM SYS_USER U
LEFT JOIN SYS_ROLE R ON R.ROLE_ID = U.ROLE_ID AND R.IS_DELETED = 0
WHERE U.USER_ID = @userId
]]></statement>
  </query>

  <query id="SYS.GetLockedUntil">
    <statement><![CDATA[
SELECT LOCKED_UNTIL FROM SYS_USER WHERE USER_ID = @userId
]]></statement>
  </query>

  <query id="SYS.RecordLoginFailure">
    <statement><![CDATA[
UPDATE SYS_USER SET
    FAIL_COUNT = CASE WHEN LOCKED_UNTIL IS NOT NULL AND LOCKED_UNTIL <= @utcNow
                      THEN 1 ELSE FAIL_COUNT + 1 END,
    LOCKED_UNTIL = CASE
        WHEN (CASE WHEN LOCKED_UNTIL IS NOT NULL AND LOCKED_UNTIL <= @utcNow
                   THEN 1 ELSE FAIL_COUNT + 1 END) >= @maxFailures THEN @lockUntil
        WHEN LOCKED_UNTIL IS NOT NULL AND LOCKED_UNTIL <= @utcNow THEN NULL
        ELSE LOCKED_UNTIL END,
    UPDATED_BY = 'SYSTEM', UPDATED_AT = @utcNow
WHERE USER_ID = @userId
]]></statement>
  </query>

  <query id="SYS.RecordLoginSuccess">
    <statement><![CDATA[
UPDATE SYS_USER SET
    LAST_LOGIN_AT = @utcNow,
    FAIL_COUNT = 0,
    LOCKED_UNTIL = NULL,
    PASSWORD_HASH = COALESCE(@passwordHash, PASSWORD_HASH),
    UPDATED_BY = 'SYSTEM', UPDATED_AT = @utcNow
WHERE USER_ID = @userId
]]></statement>
  </query>

  <query id="SYS.InsertLoginFailureHist">
    <statement><![CDATA[
INSERT INTO SYS_LOGIN_FAILURE_HIST
    (FAILURE_ID, USER_ID, IP_ADDRESS, USER_AGENT, FAILURE_REASON, OCCURRED_AT,
     CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
VALUES
    (@failureId, @userId, @ipAddress, @userAgent, @failureReason, @utcNow,
     'SYSTEM', @utcNow, 'SYSTEM', @utcNow)
]]></statement>
  </query>

  <query id="SYS.InsertRefreshToken">
    <statement><![CDATA[
INSERT INTO SYS_REFRESH_TOKEN
    (TOKEN_ID, USER_ID, TOKEN_HASH, EXPIRES_AT, REVOKED_AT, CREATED_AT)
VALUES
    (@tokenId, @userId, @tokenHash, @expiresAt, NULL, @utcNow)
]]></statement>
  </query>

  <query id="SYS.ValidateRefreshToken">
    <statement><![CDATA[
SELECT TOKEN_ID FROM SYS_REFRESH_TOKEN
WHERE USER_ID = @userId AND TOKEN_HASH = @tokenHash
  AND REVOKED_AT IS NULL AND EXPIRES_AT > @utcNow
]]></statement>
  </query>

  <query id="SYS.RevokeRefreshTokenIfActive">
    <statement><![CDATA[
UPDATE SYS_REFRESH_TOKEN SET REVOKED_AT = @utcNow
WHERE USER_ID = @userId AND TOKEN_HASH = @tokenHash
  AND REVOKED_AT IS NULL AND EXPIRES_AT > @utcNow
]]></statement>
  </query>

  <query id="SYS.RevokeAllRefreshTokens">
    <statement><![CDATA[
UPDATE SYS_REFRESH_TOKEN SET REVOKED_AT = @utcNow
WHERE USER_ID = @userId AND REVOKED_AT IS NULL
]]></statement>
  </query>

</queries>
```

- [ ] **Step 3: Commit**
```powershell
git add -A
$m = "feat(auth): 격리된 SYS 인증 명명 쿼리(db/queries-auth, mssql+sqlite) — 공개 게이트웨이 미노출"
$f = [IO.Path]::GetTempFileName(); [IO.File]::WriteAllText($f,$m,(New-Object System.Text.UTF8Encoding($false))); git commit -F $f; Remove-Item $f
```

---

## Task 4: 호스트 DTO + SysRefreshTokenStore + GatewayLoginService + AddNexaOneAuth

**Files:**
- Create: `src/00.Main/NexaOne.Server/Gateway/AuthDtos.cs`
- Create: `src/00.Main/NexaOne.Server/Gateway/SysRefreshTokenStore.cs`
- Create: `src/00.Main/NexaOne.Server/Gateway/GatewayLoginService.cs`
- Create: `src/00.Main/NexaOne.Server/Gateway/AuthServiceExtensions.cs`
- Modify: `src/00.Main/NexaOne.Server/NexaOne.Server.csproj`

- [ ] **Step 1: 호스트 DTO + 결과 타입**

`src/00.Main/NexaOne.Server/Gateway/AuthDtos.cs`:
```csharp
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;

namespace NexaOne.Server.Gateway;

// 기존 NexaOne.API와 동일 JSON 계약(필드명/형태 일치). 호스트가 API 웹앱을 참조하지 않도록 로컬 정의한다.
public record LoginRequest(string UserId, string Password, string PlantId = "DEFAULT");

public record LoginResponse(
    string AccessToken,
    string RefreshToken,
    string UserId,
    string UserName,
    string PlantId,
    IReadOnlyList<string> Roles,
    bool RequirePasswordChange = false);

public record RefreshRequest(string UserId, string RefreshToken);

public record TokenRefreshResponse(string AccessToken, string RefreshToken);

/// <summary>로그인/리프레시 서비스 결과 — 컨트롤러가 200/401로 매핑한다(기존 API와 동일 상태코드/오류 코드).</summary>
public sealed class AuthOutcome
{
    private AuthOutcome(IActionResult result) => Result = result;
    public IActionResult Result { get; }

    public static AuthOutcome Ok(object body) => new(new OkObjectResult(body));

    public static AuthOutcome InvalidCredentials() =>
        new(new UnauthorizedObjectResult(new Error("INVALID_CREDENTIALS", "Invalid credentials.")));

    public static AuthOutcome AccountLocked(DateTime lockedUntil, DateTime now)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling((lockedUntil - now).TotalMinutes));
        return new(new UnauthorizedObjectResult(new Error("ACCOUNT_LOCKED",
            $"비밀번호 5회 연속 오류로 계정이 잠겼습니다. 약 {minutes}분 후 다시 시도하거나 관리자에게 문의하세요.")));
    }

    public static AuthOutcome InvalidRefreshToken() =>
        new(new UnauthorizedObjectResult(new Error("Auth.InvalidRefreshToken", "Invalid or expired refresh token.")));
}
```

- [ ] **Step 2: SysRefreshTokenStore(게이트웨이 backed)**

`src/00.Main/NexaOne.Server/Gateway/SysRefreshTokenStore.cs`:
```csharp
using System.Security.Cryptography;
using System.Text;
using NexaOne.Application.Auth;
using NexaOne.Application.Messaging;
using NexaOne.Application.Query;

namespace NexaOne.Server.Gateway;

/// <summary>DB 영속 리프레시 토큰 저장소(게이트웨이식, 무-브리지). 평문 대신 SHA-256 해시를 저장하고,
/// 회전은 '활성 토큰만 조건부 폐기' 영향행수로 재생 공격을 탐지한다(인메모리보다 강화). 격리 인증 레지스트리만 사용한다.</summary>
public sealed class SysRefreshTokenStore : IRefreshTokenStore
{
    private readonly IRuleDispatcher _dispatcher;
    private readonly IQueryRegistry _authQueries;
    private readonly IJwtService _jwt;
    private readonly TimeSpan _ttl;

    public SysRefreshTokenStore(IRuleDispatcher dispatcher, IQueryRegistry authQueries, IJwtService jwt, TimeSpan ttl)
    {
        _dispatcher = dispatcher;
        _authQueries = authQueries;
        _jwt = jwt;
        _ttl = ttl;
    }

    public async Task<string> IssueAsync(string userId)
    {
        var token = _jwt.GenerateRefreshToken();
        var now = DateTime.UtcNow;
        await _dispatcher.ExecuteAsync(Sql("SYS.InsertRefreshToken"), new Dictionary<string, object>
        {
            ["tokenId"] = Guid.NewGuid().ToString("N"),
            ["userId"] = userId,
            ["tokenHash"] = Hash(token),
            ["expiresAt"] = now.Add(_ttl),
            ["utcNow"] = now,
        });
        return token;
    }

    public async Task<bool> ValidateAsync(string userId, string token)
    {
        var rows = await _dispatcher.QueryAsync(Sql("SYS.ValidateRefreshToken"), new Dictionary<string, object>
        {
            ["userId"] = userId,
            ["tokenHash"] = Hash(token),
            ["utcNow"] = DateTime.UtcNow,
        });
        return rows.Count > 0;
    }

    public async Task<string> RotateAsync(string userId, string oldToken)
    {
        // 활성 토큰만 조건부 폐기 — 영향행수 0이면 이미 폐기/만료(재생) → 빈 문자열로 회전 실패를 알린다.
        var affected = await _dispatcher.ExecuteAsync(Sql("SYS.RevokeRefreshTokenIfActive"), new Dictionary<string, object>
        {
            ["userId"] = userId,
            ["tokenHash"] = Hash(oldToken),
            ["utcNow"] = DateTime.UtcNow,
        });
        if (affected == 0) return string.Empty;
        return await IssueAsync(userId);
    }

    public async Task RevokeAsync(string userId, string token)
        => await _dispatcher.ExecuteAsync(Sql("SYS.RevokeRefreshTokenIfActive"), new Dictionary<string, object>
        {
            ["userId"] = userId,
            ["tokenHash"] = Hash(token),
            ["utcNow"] = DateTime.UtcNow,
        });

    public async Task RevokeAllByUserAsync(string userId)
        => await _dispatcher.ExecuteAsync(Sql("SYS.RevokeAllRefreshTokens"), new Dictionary<string, object>
        {
            ["userId"] = userId,
            ["utcNow"] = DateTime.UtcNow,
        });

    private string Sql(string id) => _authQueries.TryGet(id, out var def) && def is not null
        ? def.Sql
        : throw new InvalidOperationException($"인증 명명 쿼리 '{id}'가 격리 레지스트리에 없습니다(db/queries-auth/{_authQueries.Dialect}).");

    // 토큰은 평문 저장 금지 — SHA-256 hex로 저장/조회한다(불투명 난수 토큰이라 stretching 불필요, 인덱스 조회 등가).
    private static string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
```

- [ ] **Step 3: GatewayLoginService(로그인 + 리프레시)**

`src/00.Main/NexaOne.Server/Gateway/GatewayLoginService.cs`:
```csharp
using System.Globalization;
using NexaOne.Application.Auth;
using NexaOne.Application.Messaging;
using NexaOne.Application.Query;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.SYS.Domain;   // LoginFailureHistory.Reasons (상수만 참조 — 플러그인 런타임 타입 사용 아님)

namespace NexaOne.Server.Gateway;

/// <summary>통합 호스트 인증 서비스(게이트웨이식, 무-브리지). 운영 UserService.ValidateAndLoginAsync +
/// AuthController.Login/Refresh의 동작을 Default-ALC 타입 + 격리 명명 쿼리로 재현한다.</summary>
public sealed class GatewayLoginService
{
    private readonly IRuleDispatcher _dispatcher;
    private readonly IQueryRegistry _authQueries;
    private readonly IJwtService _jwt;
    private readonly IRefreshTokenStore _tokenStore;

    public GatewayLoginService(IRuleDispatcher dispatcher, IQueryRegistry authQueries,
        IJwtService jwt, IRefreshTokenStore tokenStore)
    {
        _dispatcher = dispatcher;
        _authQueries = authQueries;
        _jwt = jwt;
        _tokenStore = tokenStore;
    }

    public async Task<AuthOutcome> LoginAsync(
        string userId, string password, string plantId, string ip, string ua, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var row = await QuerySingleAsync("SYS.AuthUserById", new() { ["userId"] = userId }, ct);

        if (row is null)
        {
            await RecordFailureAsync(userId, ip, ua, LoginFailureHistory.Reasons.UserNotFound, now, ct);
            return AuthOutcome.InvalidCredentials();
        }

        var lockedUntil = ToNullableDateTime(Get(row, "LOCKED_UNTIL"));
        if (lockedUntil is { } locked && locked > now)
        {
            await RecordFailureAsync(userId, ip, ua, LoginFailureHistory.Reasons.AccountLocked, now, ct);
            return AuthOutcome.AccountLocked(locked, now);
        }

        if (!ToBool(Get(row, "IS_ACTIVE")) || ToBool(Get(row, "IS_DELETED")))
        {
            await RecordFailureAsync(userId, ip, ua, LoginFailureHistory.Reasons.InactiveUser, now, ct);
            return AuthOutcome.InvalidCredentials();
        }

        var storedHash = ToStr(Get(row, "PASSWORD_HASH"));
        if (!PasswordHasher.Verify(password, storedHash))
        {
            await ExecuteAsync("SYS.RecordLoginFailure", new()
            {
                ["userId"] = userId,
                ["utcNow"] = now,
                ["maxFailures"] = AccountLockoutPolicy.MaxConsecutiveFailures,
                ["lockUntil"] = now.Add(AccountLockoutPolicy.LockDuration),
            }, ct);
            var afterRow = await QuerySingleAsync("SYS.GetLockedUntil", new() { ["userId"] = userId }, ct);
            var afterLock = ToNullableDateTime(afterRow is null ? null : Get(afterRow, "LOCKED_UNTIL"));
            await RecordFailureAsync(userId, ip, ua, LoginFailureHistory.Reasons.WrongPassword, now, ct);
            return afterLock is { } until && until > now
                ? AuthOutcome.AccountLocked(until, now)
                : AuthOutcome.InvalidCredentials();
        }

        // 성공 — rehash-on-login(구 해시면 강화 해시 저장), 단일 UPDATE로 LAST_LOGIN_AT·실패카운터·잠금 처리.
        var rehash = PasswordHasher.NeedsRehash(storedHash) ? PasswordHasher.Hash(password) : null;
        await ExecuteAsync("SYS.RecordLoginSuccess", new()
        {
            ["userId"] = userId,
            ["utcNow"] = now,
            ["passwordHash"] = (object?)rehash ?? DBNull.Value,
        }, ct);

        var userName = ToStr(Get(row, "USER_NAME"));
        var roleId = ToStr(Get(row, "ROLE_ID"));
        var requireChange = !string.Equals(ToStr(Get(row, "PASSWORD_STATE"), "Normal"), "Normal", StringComparison.Ordinal);
        var roles = new[] { roleId };
        var perms = EffectivePermissions(roleId, ToNullableStr(Get(row, "PERMISSIONS")));
        var accessToken = _jwt.GenerateAccessToken(userId, userName, plantId, roles, requireChange, perms);
        var refreshToken = await _tokenStore.IssueAsync(userId);

        return AuthOutcome.Ok(new LoginResponse(
            accessToken, refreshToken, userId, userName, plantId, roles, requireChange));
    }

    public async Task<AuthOutcome> RefreshAsync(string userId, string refreshToken, string? bearerPlantId, CancellationToken ct)
    {
        if (!await _tokenStore.ValidateAsync(userId, refreshToken))
            return AuthOutcome.InvalidRefreshToken();

        // 역할/변경강제/활성·삭제는 DB 상태로 재평가한다(구 토큰 클레임 승계 금지 — pwdChange 우회 방지).
        var row = await QuerySingleAsync("SYS.AuthUserById", new() { ["userId"] = userId }, ct);
        if (row is null || !ToBool(Get(row, "IS_ACTIVE")) || ToBool(Get(row, "IS_DELETED")))
            return AuthOutcome.InvalidRefreshToken();

        var newRefresh = await _tokenStore.RotateAsync(userId, refreshToken);
        if (string.IsNullOrEmpty(newRefresh))
            return AuthOutcome.InvalidRefreshToken();   // 회전 경합/재생 — 패배 측은 무효

        var userName = ToStr(Get(row, "USER_NAME"));
        var roleId = ToStr(Get(row, "ROLE_ID"));
        var requireChange = !string.Equals(ToStr(Get(row, "PASSWORD_STATE"), "Normal"), "Normal", StringComparison.Ordinal);
        var perms = EffectivePermissions(roleId, ToNullableStr(Get(row, "PERMISSIONS")));
        var plantId = string.IsNullOrEmpty(bearerPlantId) ? "DEFAULT" : bearerPlantId;
        var accessToken = _jwt.GenerateAccessToken(userId, userName, plantId, new[] { roleId }, requireChange, perms);

        return AuthOutcome.Ok(new TokenRefreshResponse(accessToken, newRefresh));
    }

    // ── 권한 합성 (운영 UserService.GetEffectivePermissionsAsync와 동일: 기본 매핑 ∪ split('|'), OrdinalIgnoreCase distinct) ──
    private static IReadOnlyList<string> EffectivePermissions(string roleId, string? permissionsCsv)
    {
        var set = new HashSet<string>(RolePermissionDefaults.For(roleId), StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(permissionsCsv))
            foreach (var p in permissionsCsv.Split('|', StringSplitOptions.RemoveEmptyEntries))
                set.Add(p);
        return set.ToList();
    }

    private async Task RecordFailureAsync(string userId, string ip, string ua, string reason, DateTime now, CancellationToken ct)
        => await ExecuteAsync("SYS.InsertLoginFailureHist", new()
        {
            ["failureId"] = Guid.NewGuid().ToString("N"),
            ["userId"] = Truncate(userId, 50),
            ["ipAddress"] = Truncate(ip, 45),
            ["userAgent"] = Truncate(ua, 500),
            ["failureReason"] = Truncate(reason, 50),
            ["utcNow"] = now,
        }, ct);

    private async Task<Dictionary<string, object?>?> QuerySingleAsync(
        string id, Dictionary<string, object> p, CancellationToken ct)
    {
        var rows = await _dispatcher.QueryAsync(Sql(id), p, ct);
        return rows.Count > 0 ? rows[0] : null;
    }

    private async Task ExecuteAsync(string id, Dictionary<string, object> p, CancellationToken ct)
        => await _dispatcher.ExecuteAsync(Sql(id), p, ct);

    private string Sql(string id) => _authQueries.TryGet(id, out var def) && def is not null
        ? def.Sql
        : throw new InvalidOperationException($"인증 명명 쿼리 '{id}'가 격리 레지스트리에 없습니다(db/queries-auth/{_authQueries.Dialect}).");

    private static object? Get(IReadOnlyDictionary<string, object?> row, string col)
        => row.TryGetValue(col, out var v) ? v : null;

    // ── DB 방언 차이 흡수(MSSQL: bool/DateTime, SQLite: long/string) ──
    private static bool ToBool(object? v) => v switch
    {
        null or DBNull => false,
        bool b => b,
        long l => l != 0,
        int i => i != 0,
        string s => s == "1" || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase),
        _ => Convert.ToInt64(v) != 0,
    };

    private static string ToStr(object? v, string fallback = "") => v switch
    {
        null or DBNull => fallback,
        string s => s,
        _ => v.ToString() ?? fallback,
    };

    private static string? ToNullableStr(object? v) => v switch
    {
        null or DBNull => null,
        string s => s,
        _ => v.ToString(),
    };

    // 잠금시각 파싱 — MSSQL DATETIME2→DateTime, SQLite TEXT(ISO8601)→파싱. 보안상 파싱 실패는 null(잠금 미인정)이
    // 되지 않도록, 실패 시 SQL측 검증(실패경로 재잠금)에 의존하되 통합테스트로 SQLite 파싱을 직접 검증한다.
    private static DateTime? ToNullableDateTime(object? v) => v switch
    {
        null or DBNull => null,
        DateTime dt => dt,
        string s => DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var p) ? p : null,
        _ => null,
    };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
```

- [ ] **Step 4: AddNexaOneAuth DI 확장**

`src/00.Main/NexaOne.Server/Gateway/AuthServiceExtensions.cs`:
```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.Application.Auth;
using NexaOne.Application.Messaging;
using NexaOne.Application.Query;

namespace NexaOne.Server.Gateway;

/// <summary>통합 호스트 인증 DI(무-브리지). IJwtService + DB-backed IRefreshTokenStore + GatewayLoginService를 등록한다.
/// 인증 명명 쿼리는 공개 게이트웨이(db/queries)와 분리된 db/queries-auth 전용 레지스트리로 로드해 노출을 막는다.
/// AddNexaOneGateway(IRuleDispatcher 등록) 이후에 호출해야 한다.</summary>
public static class AuthServiceExtensions
{
    public static IServiceCollection AddNexaOneAuth(this IServiceCollection services, IConfiguration configuration)
    {
        var dialect = string.Equals(configuration["Database:Provider"], "Sqlite", StringComparison.OrdinalIgnoreCase)
            ? "sqlite" : "mssql";
        // 격리 인증 레지스트리(공개 IQueryRegistry 싱글톤과 별개). 루트는 Auth:Query:Directory override 또는
        // BaseDirectory 상위탐색으로 db/queries-auth를 찾는다.
        var authRoot = ResolveAuthQueriesRoot(configuration["Auth:Query:Directory"]);
        var authRegistry = FileQueryRegistry.Load(dialect, authRoot);

        services.AddSingleton<IJwtService, JwtService>();

        var ttl = TimeSpan.FromDays(configuration.GetValue("Jwt:RefreshTokenExpiryDays", 7));
        services.AddSingleton<IRefreshTokenStore>(sp => new SysRefreshTokenStore(
            sp.GetRequiredService<IRuleDispatcher>(), authRegistry, sp.GetRequiredService<IJwtService>(), ttl));

        services.AddSingleton(sp => new GatewayLoginService(
            sp.GetRequiredService<IRuleDispatcher>(), authRegistry,
            sp.GetRequiredService<IJwtService>(), sp.GetRequiredService<IRefreshTokenStore>()));

        return services;
    }

    // override가 있으면 그 디렉터리를, 없으면 BaseDirectory에서 상위로 db/queries-auth를 찾는다(db/queries 규약과 동형).
    private static string? ResolveAuthQueriesRoot(string? overrideDirectory)
    {
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
            return Directory.Exists(overrideDirectory) ? overrideDirectory : null;
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            var p = Path.Combine(d.FullName, "db", "queries-auth");
            if (Directory.Exists(p)) return p;
            d = d.Parent;
        }
        return null;
    }
}
```

- [ ] **Step 5: csproj — db/queries-auth 출력 복사**

`src/00.Main/NexaOne.Server/NexaOne.Server.csproj`의 db/queries 복사 `<ItemGroup>` 아래에 추가:
```xml
  <!-- 격리 인증 쿼리(db/queries-auth/{mssql,sqlite}/*.xml)를 출력으로 복사한다(공개 db/queries와 분리 유지).
       호스트 인증 서비스만 전용 레지스트리로 로드한다. 배포 robustness 위해 명시 복사(테스트는 상위탐색). -->
  <ItemGroup>
    <Content Include="..\..\..\db\queries-auth\**\*.xml"
             Link="db\queries-auth\%(RecursiveDir)%(Filename)%(Extension)"
             CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 6: 빌드**
```powershell
dotnet build src/00.Main/NexaOne.Server/NexaOne.Server.csproj -c Debug
```
Expected: 0 error, 0 warning. (컨트롤러/Program 배선은 Task 5에서 추가하므로 이 단계는 컴파일만 확인.)

- [ ] **Step 7: Commit**
```powershell
git add -A
$m = "feat(auth): 호스트 인증 서비스(GatewayLoginService/SysRefreshTokenStore) + AddNexaOneAuth(격리 레지스트리)"
$f = [IO.Path]::GetTempFileName(); [IO.File]::WriteAllText($f,$m,(New-Object System.Text.UTF8Encoding($false))); git commit -F $f; Remove-Item $f
```

---

## Task 5: AuthController + Program.cs 배선(AddNexaOneAuth + RateLimiter)

**Files:**
- Create: `src/00.Main/NexaOne.Server/Gateway/AuthController.cs`
- Modify: `src/00.Main/NexaOne.Server/Program.cs`

- [ ] **Step 1: AuthController(login/refresh)**

`src/00.Main/NexaOne.Server/Gateway/AuthController.cs`:
```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NexaOne.Application.Auth;
using NexaOne.Common;

namespace NexaOne.Server.Gateway;

/// <summary>통합 호스트 인증 엔드포인트(게이트웨이식, 무-브리지). login/refresh만 구현(Phase 3b 범위).
/// 기존 NexaOne.API와 동일 라우트/DTO/상태코드/오류 코드. plugin↔DI 브리지 없이 Default-ALC + 격리 명명 쿼리로 동작.</summary>
[ApiController]
[Route("api/v1/auth")]
[ProducesErrorResponseType(typeof(Error))]
public sealed class AuthController : ControllerBase
{
    private readonly GatewayLoginService _login;
    private readonly IJwtService _jwt;

    public AuthController(GatewayLoginService login, IJwtService jwt)
    {
        _login = login;
        _jwt = jwt;
    }

    [HttpPost("login")]
    [AllowAnonymous]                 // 전역 인증 요구의 익명 예외 진입점
    [EnableRateLimiting("auth")]     // IP당 10/min — 브루트포스 방어
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
        var ua = Request.Headers.UserAgent.ToString();
        var outcome = await _login.LoginAsync(request.UserId, request.Password, request.PlantId, ip, ua, ct);
        return outcome.Result;
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [ProducesResponseType<TokenRefreshResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest request, CancellationToken ct)
    {
        // plantId는 DB에 없으므로 구 Bearer 토큰에서만 승계(판정 미사용). 헤더 없으면 DEFAULT로 저하.
        var principal = _jwt.ValidateAccessToken(
            HttpContext.Request.Headers.Authorization.ToString().Replace("Bearer ", string.Empty));
        var plantId = principal?.FindFirst("plantId")?.Value;
        var outcome = await _login.RefreshAsync(request.UserId, request.RefreshToken, plantId, ct);
        return outcome.Result;
    }
}
```

- [ ] **Step 2: Program.cs — RateLimiter 등록 + AddNexaOneAuth + UseRateLimiter**

`src/00.Main/NexaOne.Server/Program.cs` 수정:

(a) 상단 `using` 추가:
```csharp
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
```

(b) `builder.Services.AddNexaOneGateway(builder.Configuration);` **다음 줄**에 인증 DI 추가:
```csharp
// 인증(무-브리지, 게이트웨이식) — 토큰 직접 발급(login/refresh). 게이트웨이 DI(IRuleDispatcher) 이후 호출.
builder.Services.AddNexaOneAuth(builder.Configuration);
```

(c) `builder.Services.AddAuthorization();` **다음**에 레이트리미터 등록(API와 동일 정책):
```csharp
// §18.2.3 — 레이트리미터: 익명 인증 진입점(login/refresh)은 "auth"(IP당 10/min), 그 외 전역 100/min.
// RateLimiting:Enabled=false면 미적용(통합테스트가 공유 IP·다수 호출로 비결정 실패하는 것을 피함). 기본 활성.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var key = context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 100, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
        });
    });
    options.AddPolicy("auth", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
            }));
});
```

(d) 파이프라인에서 `app.UseAuthentication();` + `app.UseMiddleware<...AuditUserContextMiddleware>();` **다음**, `app.UseAuthorization();` **이전**에 추가:
```csharp
if (builder.Configuration.GetValue("RateLimiting:Enabled", true))
    app.UseRateLimiter();
```

- [ ] **Step 3: 빌드 + ServerTests 회귀(기존 12)**
```powershell
dotnet build src/00.Main/NexaOne.Server/NexaOne.Server.csproj -c Debug
dotnet test test/NexaOne.ServerTests/NexaOne.ServerTests.csproj -c Debug
```
Expected: 0 error/0 warning, 기존 12 그린(인증 추가가 /health·/diag·MDM 게이트웨이를 깨지 않음).

- [ ] **Step 4: Commit**
```powershell
git add -A
$m = "feat(auth): 통합 호스트 AuthController(login/refresh) + Program 배선(AddNexaOneAuth + RateLimiter)"
$f = [IO.Path]::GetTempFileName(); [IO.File]::WriteAllText($f,$m,(New-Object System.Text.UTF8Encoding($false))); git commit -F $f; Remove-Item $f
```

---

## Task 6: 통합 테스트(SQLite E2E)

**Files:**
- Create: `test/NexaOne.ServerTests/GatewayAuthE2ETests.cs`
- Create: `test/NexaOne.ServerTests/GatewayAuthRateLimitTests.cs`

> 결정성을 위해 각 테스트는 고유 userId(Guid 접미)로 자체 사용자를 SQLite에 직접 시드한다(공유 DB 파일 순서 무관). 기능 테스트는 `RateLimiting:Enabled=false`, 429 테스트만 별도 클래스에서 `true`.

- [ ] **Step 1: 기능 테스트 작성(성공·열거방지·잠금패리티·잠금중정답·refresh회전/재생·권한클레임·rehash폭)**

`test/NexaOne.ServerTests/GatewayAuthE2ETests.cs`:
```csharp
using System.Net;
using System.Net.Http.Json;
using System.IdentityModel.Tokens.Jwt;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using NexaOne.Common;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>통합 호스트 인증 E2E(게이트웨이식 무-브리지) — modules OFF + SQLite(NexaMes 스키마, V034 포함).
/// 로그인 성공/열거방지/잠금패리티/잠금중-정답(SQLite 날짜 파싱)/refresh 회전·재생/권한클레임/rehash+폭확장을 검증한다.</summary>
public sealed class GatewayAuthE2ETests : IClassFixture<GatewayAuthE2ETests.AuthFactory>
{
    private const string Secret = "phase3b-auth-e2e-jwt-secret-key-at-least-32-bytes!!";
    private const string Issuer = "nexaone-auth-test";
    private readonly AuthFactory _factory;
    public GatewayAuthE2ETests(AuthFactory factory) => _factory = factory;

    public sealed class AuthFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-auth-e2e-{Guid.NewGuid():N}.db");
        public string ConnString => $"Data Source={DbPath};Foreign Keys=False";
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", ConnString);
            builder.UseSetting("Jwt:SecretKey", Secret);
            builder.UseSetting("Jwt:Issuer", Issuer);
            builder.UseSetting("Jwt:Audience", Issuer);
            builder.UseSetting("RateLimiting:Enabled", "false");   // 기능 테스트는 레이트리밋 비활성(공유 IP 비결정 회피)
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* 임시파일 정리 실패 무시 */ }
        }
    }

    // ── 시드 헬퍼: SYS_USER / SYS_ROLE 직접 삽입(스키마는 호스트 기동 시 부트스트랩됨) ──
    private void EnsureSchemaReady()
    {
        // 호스트를 한 번 띄워 SQLite 스키마(+admin 시드)를 보장한다(개발 SQLite 부트스트랩 경로).
        _ = _factory.CreateClient();
    }

    private void SeedUser(string userId, string passwordHash, string roleId = "ADMIN",
        string passwordState = "Normal", int isActive = 1, int isDeleted = 0)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO SYS_USER
            (USER_ID, USER_NAME, PASSWORD_HASH, EMAIL, ROLE_ID, LANGUAGE, IS_ACTIVE, IS_DELETED,
             PASSWORD_STATE, FAIL_COUNT, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@id, @id, @h, '', @role, 'KoKr', @act, @del, @ps, 0, 'TEST', @now, 'TEST', @now)";
        cmd.Parameters.AddWithValue("@id", userId);
        cmd.Parameters.AddWithValue("@h", passwordHash);
        cmd.Parameters.AddWithValue("@role", roleId);
        cmd.Parameters.AddWithValue("@act", isActive);
        cmd.Parameters.AddWithValue("@del", isDeleted);
        cmd.Parameters.AddWithValue("@ps", passwordState);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.ExecuteNonQuery();
    }

    private void SeedRole(string roleId, string permissions)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO SYS_ROLE (ROLE_ID, ROLE_NAME, DESCRIPTION, PERMISSIONS, IS_DELETED,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@id, @id, '', @perms, 0, 'TEST', @now, 'TEST', @now)";
        cmd.Parameters.AddWithValue("@id", roleId);
        cmd.Parameters.AddWithValue("@perms", permissions);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.ExecuteNonQuery();
    }

    private string? ReadPasswordHash(string userId)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT PASSWORD_HASH FROM SYS_USER WHERE USER_ID = @id";
        cmd.Parameters.AddWithValue("@id", userId);
        return cmd.ExecuteScalar() as string;
    }

    private static string Uid(string p) => $"{p}_{Guid.NewGuid():N}".Substring(0, 16);

    [Fact]
    public async Task Login_succeeds_and_issues_tokens()
    {
        EnsureSchemaReady();
        var uid = Uid("ok");
        SeedUser(uid, NexaOne.Common.PasswordHasher.Hash("p@ssw0rd!"));
        var client = _factory.CreateClient();

        var res = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { userId = uid, password = "p@ssw0rd!", plantId = "P1" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<LoginBody>();
        body.Should().NotBeNull();
        body!.accessToken.Should().NotBeNullOrEmpty();
        body.refreshToken.Should().NotBeNullOrEmpty();
        body.userId.Should().Be(uid);
        body.plantId.Should().Be("P1");
    }

    [Fact]
    public async Task Login_nonexistent_user_returns_invalid_credentials_no_enumeration()
    {
        EnsureSchemaReady();
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { userId = Uid("ghost"), password = "x", plantId = "DEFAULT" });
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var err = await res.Content.ReadFromJsonAsync<Error>();
        err!.Code.Should().Be("INVALID_CREDENTIALS", "존재하지 않는 사용자도 자격오류와 동일 코드(열거 방지)");
    }

    [Fact]
    public async Task Login_wrong_password_returns_invalid_then_locks_after_threshold()
    {
        EnsureSchemaReady();
        var uid = Uid("lock");
        SeedUser(uid, NexaOne.Common.PasswordHasher.Hash("correct!"));
        var client = _factory.CreateClient();

        // 4회 실패 — 아직 잠기지 않음(INVALID_CREDENTIALS)
        for (var i = 0; i < 4; i++)
        {
            var bad = await client.PostAsJsonAsync("/api/v1/auth/login", new { userId = uid, password = "nope", plantId = "x" });
            bad.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await bad.Content.ReadFromJsonAsync<Error>())!.Code.Should().Be("INVALID_CREDENTIALS");
        }
        // 5번째 실패 — 임계 도달로 잠김(ACCOUNT_LOCKED)
        var fifth = await client.PostAsJsonAsync("/api/v1/auth/login", new { userId = uid, password = "nope", plantId = "x" });
        fifth.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await fifth.Content.ReadFromJsonAsync<Error>())!.Code.Should().Be("ACCOUNT_LOCKED", "5회 연속 실패 시 잠금(패리티)");
    }

    [Fact]
    public async Task Login_with_correct_password_while_locked_is_rejected_sqlite_datetime_parse()
    {
        EnsureSchemaReady();
        var uid = Uid("lkok");
        SeedUser(uid, NexaOne.Common.PasswordHasher.Hash("correct!"));
        var client = _factory.CreateClient();
        for (var i = 0; i < 5; i++)
            await client.PostAsJsonAsync("/api/v1/auth/login", new { userId = uid, password = "nope", plantId = "x" });

        // 잠긴 상태에서 '정답'으로 로그인 → step-3 C# 잠금 판정(SQLite LOCKED_UNTIL 문자열 파싱)이 동작해야 한다
        var res = await client.PostAsJsonAsync("/api/v1/auth/login", new { userId = uid, password = "correct!", plantId = "x" });
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await res.Content.ReadFromJsonAsync<Error>())!.Code.Should().Be("ACCOUNT_LOCKED",
            "잠금 중에는 정답이어도 거부돼야 한다(LOCKED_UNTIL 파싱 정상 입증)");
    }

    [Fact]
    public async Task Refresh_rotates_and_old_token_replay_is_rejected()
    {
        EnsureSchemaReady();
        var uid = Uid("rot");
        SeedUser(uid, NexaOne.Common.PasswordHasher.Hash("pw1"));
        var client = _factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/v1/auth/login",
            new { userId = uid, password = "pw1", plantId = "x" })).Content.ReadFromJsonAsync<LoginBody>();

        var r1 = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { userId = uid, refreshToken = login!.refreshToken });
        r1.StatusCode.Should().Be(HttpStatusCode.OK);
        var rotated = await r1.Content.ReadFromJsonAsync<RefreshBody>();
        rotated!.refreshToken.Should().NotBe(login.refreshToken, "회전으로 새 refresh 토큰이 발급돼야 한다");

        // 구 토큰 재사용 → 401(재생 방어)
        var replay = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { userId = uid, refreshToken = login.refreshToken });
        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // 새 토큰은 유효 → 200
        var r2 = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { userId = uid, refreshToken = rotated.refreshToken });
        r2.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Login_issues_permission_claims_and_token_is_accepted_by_gateway()
    {
        EnsureSchemaReady();
        var roleId = Uid("ROLE");
        SeedRole(roleId, "mdm:manage");
        var uid = Uid("perm");
        SeedUser(uid, NexaOne.Common.PasswordHasher.Hash("pw2"), roleId: roleId);
        var client = _factory.CreateClient();

        var body = await (await client.PostAsJsonAsync("/api/v1/auth/login",
            new { userId = uid, password = "pw2", plantId = "x" })).Content.ReadFromJsonAsync<LoginBody>();

        // 토큰에 permission 클레임이 실려야 한다
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(body!.accessToken);
        jwt.Claims.Should().Contain(c =>
            c.Type == NexaOne.Common.Security.Permissions.ClaimType && c.Value == "mdm:manage");

        // 호스트가 발급한 토큰으로 같은 호스트의 게이트웨이 쓰기쿼리(mdm:manage)가 통과해야 한다(E2E)
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", body.accessToken);
        var save = await client.PostAsJsonAsync("/api/v1/command/MDM.CreatePlant", new Dictionary<string, object>
        { ["plantId"] = "AUTH_" + Guid.NewGuid().ToString("N")[..6], ["plantName"] = "auth e2e" });
        save.StatusCode.Should().Be(HttpStatusCode.OK, "호스트 발급 토큰이 동일 호스트 JWT 검증을 통과해야 한다");
    }

    [Fact]
    public async Task Legacy_sha256_login_rehashes_to_pbkdf2_proving_password_hash_widening()
    {
        EnsureSchemaReady();
        var uid = Uid("rehash");
        // 구 무염 SHA-256 hex(64자) — 레거시 형식. 'legacy!'의 SHA-256.
        var legacy = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("legacy!"))).ToLowerInvariant();
        SeedUser(uid, legacy);
        var client = _factory.CreateClient();

        var res = await client.PostAsJsonAsync("/api/v1/auth/login", new { userId = uid, password = "legacy!", plantId = "x" });
        res.StatusCode.Should().Be(HttpStatusCode.OK, "레거시 SHA-256도 로그인 성공해야 한다");

        var stored = ReadPasswordHash(uid);
        stored.Should().StartWith("pbkdf2$", "로그인 성공 시 강화 해시로 재해싱돼야 한다");
        stored!.Length.Should().BeGreaterThan(64, "PBKDF2 해시(~83자)가 저장돼 PASSWORD_HASH 폭 확장(255)을 입증");
    }

    private sealed record LoginBody(string accessToken, string refreshToken, string userId, string userName,
        string plantId, List<string> roles, bool requirePasswordChange);
    private sealed record RefreshBody(string accessToken, string refreshToken);
}
```

- [ ] **Step 2: 429 레이트리밋 테스트(별도 클래스, RateLimiting ON)**

`test/NexaOne.ServerTests/GatewayAuthRateLimitTests.cs`:
```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>"auth" 정책(IP당 10/min)이 통합 호스트에서 동작함을 검증한다 — 기능 테스트와 분리(RateLimiting ON).
/// TestServer는 RemoteIpAddress가 null이라 "anonymous" 파티션을 쓰므로, 이 클래스만 한도를 건드린다.</summary>
public sealed class GatewayAuthRateLimitTests : IClassFixture<GatewayAuthRateLimitTests.RlFactory>
{
    private readonly RlFactory _factory;
    public GatewayAuthRateLimitTests(RlFactory factory) => _factory = factory;

    public sealed class RlFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-auth-rl-{Guid.NewGuid():N}.db");
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", $"Data Source={DbPath};Foreign Keys=False");
            builder.UseSetting("Jwt:SecretKey", "phase3b-ratelimit-jwt-secret-key-at-least-32b!!");
            builder.UseSetting("Jwt:Issuer", "nexaone-rl-test");
            builder.UseSetting("Jwt:Audience", "nexaone-rl-test");
            builder.UseSetting("RateLimiting:Enabled", "true");
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* 무시 */ }
        }
    }

    [Fact]
    public async Task Auth_endpoint_throttles_after_ten_requests_per_minute()
    {
        var client = _factory.CreateClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 12; i++)
        {
            var res = await client.PostAsJsonAsync("/api/v1/auth/login",
                new { userId = "rl-ghost", password = "x", plantId = "x" });
            statuses.Add(res.StatusCode);
        }
        statuses.Should().Contain(HttpStatusCode.TooManyRequests,
            "IP당 10/min을 초과하면 429가 반환돼야 한다(\"auth\" 정책)");
    }
}
```

- [ ] **Step 3: 전체 ServerTests 실행**
```powershell
dotnet test test/NexaOne.ServerTests/NexaOne.ServerTests.csproj -c Debug
```
Expected: 기존 12 + 신규 8(기능 7 + 429 1) 그린.

- [ ] **Step 4: 전체 회귀(단위/통합/서버)**
```powershell
dotnet build NexaMes.sln -c Debug
dotnet test test/NexaOne.UnitTests/NexaOne.UnitTests.csproj -c Debug
dotnet test test/NexaOne.IntegrationTests/NexaOne.IntegrationTests.csproj -c Debug
dotnet test test/NexaOne.ServerTests/NexaOne.ServerTests.csproj -c Debug
```
Expected: 0 error/0 warning, 전부 그린(단위 1090, 통합 286/+1 skip, ServerTests 20).

- [ ] **Step 5: Commit**
```powershell
git add -A
$m = "test(server): 통합 호스트 인증 E2E(성공·열거방지·잠금패리티·잠금중정답·refresh회전/재생·권한·rehash) + 429"
$f = [IO.Path]::GetTempFileName(); [IO.File]::WriteAllText($f,$m,(New-Object System.Text.UTF8Encoding($false))); git commit -F $f; Remove-Item $f
```

---

## Self-Review(작성자 체크)

**스펙 커버리지(설계 0b5128b §10 단계화):**
1. ALC 이동 + API 회귀 → Task 1. ✅
2. V034(SYS_REFRESH_TOKEN + PASSWORD_HASH 확장) + SYS 명명쿼리(잠금 패리티) → Task 2·3. ✅
3. 게이트웨이 backed SysRefreshTokenStore → Task 4. ✅
4. GatewayLoginService + AuthController(login/refresh) + 레이트리미터 + DI → Task 4·5. ✅
5. 통합테스트(성공/열거방지/잠금패리티/refresh회전/재생/권한) → Task 6. ✅ (+잠금중정답·rehash폭·429 추가)

**적대검증 HIGH 수정 반영:** 잠금 패리티 SQL(verbatim) ✅ / PASSWORD_HASH 64→255 ✅ / IQueryRegistry 해석 ✅ / 단일 조건부 UPDATE ✅ / 레이트리미터 ✅ / NULL PERMISSIONS ✅ / 상수 중앙화 ✅ / **인증 쿼리 격리(신규 HIGH)** ✅.

**플레이스홀더 스캔:** 없음(모든 신규 코드 전문 기재; 이동은 정확 경로+ns+grep 확인 명시).

**타입 일관성:** `AuthOutcome.Result`(IActionResult), DTO 필드명(LoginResponse/TokenRefreshResponse)이 API JSON과 동일. `IRefreshTokenStore`(IssueAsync/ValidateAsync/RotateAsync/RevokeAsync/RevokeAllByUserAsync) 시그니처 일치. `IRuleDispatcher.QueryAsync→IReadOnlyList<Dictionary<string,object?>>`, `ExecuteAsync→int` 반영. `IQueryRegistry.TryGet/Dialect` 사용 일치. `RolePermissionDefaults.For`/`PasswordHasher.Verify/Hash/NeedsRehash`/`AccountLockoutPolicy.MaxConsecutiveFailures/LockDuration` 정확. `LoginFailureHistory.Reasons.*` 상수 참조.

**미해결(후속, 설계 §9):** logout/change-password/forgot/reset/register/me 미구현(Phase 3b 범위 외) / dual-issuer 전환기 SYS_REFRESH_TOKEN 공유 / 만료 토큰 정리 배치 / UserNotFound 타이밍(운영과 동일, 회귀 아님).
