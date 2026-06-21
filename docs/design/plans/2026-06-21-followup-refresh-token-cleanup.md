# 리프레시 토큰 만료 정리 워커 (후속) 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. 체크박스 단계.

**Goal:** SYS_REFRESH_TOKEN의 무제한 증가(회전 churn·만료 토큰 누적)를 막는 호스트 백그라운드 정리 워커를 추가한다. 정리 로직은 `SysRefreshTokenStore.PurgeExpiredAsync`(테스트 가능)에 두고, 얇은 `RefreshTokenCleanupWorker`(BackgroundService)가 주기 호출한다.

**Architecture:** 기존 워커(ScheduledOutboxDispatchWorker·LoginFailureRetentionWorker)는 Quartz `IRecurringScheduler`(server.xml 빈, modules-ON 전용)에 의존한다. 리프레시 토큰 저장소는 **호스트 레벨(항상 ON, modules 무관)**이므로 정리 워커는 Quartz 비의존 `BackgroundService`(PeriodicTimer)로 만들고 `AddNexaOneAuth`에서 `AddHostedService`로 등록한다. 정리 쿼리는 격리 인증 레지스트리(db/queries-auth)에 두고 store가 내부 ExecuteAsync로 실행(공개 게이트웨이 미노출). 판정 기준시각은 C#에서 산정(MSSQL/SQLite 날짜 방언 분기 회피, LoginFailureRetentionWorker 패턴).

**Tech Stack:** ASP.NET Core BackgroundService, xUnit(store.PurgeExpiredAsync 직접 검증 + 워커 disabled no-op).

---

## 검증된 사실 (직접 확인, 2026-06-21)

- **SYS_REFRESH_TOKEN**([V034](../../../db/migrations/V034__SYS_REFRESH_TOKEN.sql)): TOKEN_ID(PK)/USER_ID/TOKEN_HASH/EXPIRES_AT(NOT NULL)/REVOKED_AT(NULL)/CREATED_AT. 회전 시 구 토큰은 REVOKED_AT 설정되나 EXPIRES_AT는 원래값(미래) 유지([SysRefreshTokenStore.cs:52-63](../../../src/00.Main/NexaOne.Server/Gateway/SysRefreshTokenStore.cs#L52-L63)).
- **SysRefreshTokenStore**([SysRefreshTokenStore.cs](../../../src/00.Main/NexaOne.Server/Gateway/SysRefreshTokenStore.cs)): IRuleDispatcher + IQueryRegistry(authQueries) + IJwtService + ttl. IssueAsync는 EXPIRES_AT = now + ttl. 내부 ExecuteAsync(_authQueries.Sql(id), params)로 인증 쿼리 실행.
- **DI 등록**([AuthServiceExtensions.cs:30-31](../../../src/00.Main/NexaOne.Server/Gateway/AuthServiceExtensions.cs#L30-L31)): 현재 `AddSingleton<IRefreshTokenStore>(sp => new SysRefreshTokenStore(...))`. authRegistry는 AddNexaOneAuth의 로컬 변수(DI 미등록). AddNexaOneAuth는 modules 무관 항상 호출.
- **워커 패턴**: 기존 둘 다 Quartz IRecurringScheduler 사용(modules-ON). 호스트 토큰 정리는 Quartz 없이 `BackgroundService` + PeriodicTimer로. enabled 게이트 기본 OFF(기존 워커 관례 — 테스트/CI 무영향). 예외는 잡아 다음 주기 보존(LoginFailureRetentionWorker.PurgeAsync 패턴).
- **정리 술어**: `DELETE FROM SYS_REFRESH_TOKEN WHERE EXPIRES_AT < @cutoff`(cutoff = now - retention). 만료 토큰·회전 churn(구 토큰의 EXPIRES_AT 경과분)을 일괄 정리. 미만료·미폐기 활성 토큰은 보존(EXPIRES_AT 미래). 폐기됐으나 미만료인 토큰은 inert(Validate가 REVOKED_AT IS NULL 요구)하며 EXPIRES_AT 경과 후 정리 — 허용.
- **테스트 자산**: [GatewayAuthE2ETests.cs](../../../test/NexaOne.ServerTests/GatewayAuthE2ETests.cs)가 팩토리 DbPath + SqliteConnection 직접 SQL(SeedUser/SeedRole) 패턴 보유 — 토큰 EXPIRES_AT를 과거로 UPDATE해 만료 상황을 만들 수 있다. 팩토리 `.Services`로 SysRefreshTokenStore 해석 가능(DI 등록).

## File Structure
- 수정: `db/queries-auth/mssql/SYS.xml`·`db/queries-auth/sqlite/SYS.xml`(SYS.DeleteExpiredRefreshTokens 추가, 양 방언 동일).
- 수정: `src/00.Main/NexaOne.Server/Gateway/SysRefreshTokenStore.cs`(`PurgeExpiredAsync(TimeSpan retention)` 추가 — 구체 클래스 메서드, IRefreshTokenStore 인터페이스 무변경=레거시 미영향).
- 생성: `src/00.Main/NexaOne.Server/Gateway/RefreshTokenCleanupWorker.cs`(BackgroundService).
- 수정: `src/00.Main/NexaOne.Server/Gateway/AuthServiceExtensions.cs`(구체 등록 + 인터페이스 alias + AddHostedService).
- 생성: `test/NexaOne.ServerTests/GatewayRefreshTokenCleanupTests.cs`.

---

## Task 1: 정리 쿼리 + store.PurgeExpiredAsync

- [ ] **Step 1: 인증 쿼리 추가**(양 방언 `</queries>` 직전, kind 미표기=내부 실행). `db/queries-auth/mssql/SYS.xml`·`db/queries-auth/sqlite/SYS.xml` 동일:
```xml
  <!-- 만료 토큰 정리(retention 경과) — EXPIRES_AT < cutoff(=now-retention). 회전 churn·만료 누적 제거. -->
  <query id="SYS.DeleteExpiredRefreshTokens">
    <statement><![CDATA[
DELETE FROM SYS_REFRESH_TOKEN WHERE EXPIRES_AT < @cutoff
]]></statement>
  </query>
```

- [ ] **Step 2: SysRefreshTokenStore.PurgeExpiredAsync 추가**(구체 메서드 — 인터페이스 변경 금지):
```csharp
    /// <summary>retention 경과(EXPIRES_AT &lt; now-retention) 토큰을 삭제한다. 영향행수 반환. 정리 워커가 주기 호출.
    /// 기준시각은 C#에서 산정(날짜 방언 분기 회피). 미만료 활성 토큰은 보존.</summary>
    public async Task<int> PurgeExpiredAsync(TimeSpan retention)
        => await _dispatcher.ExecuteAsync(_authQueries.Sql("SYS.DeleteExpiredRefreshTokens"), new Dictionary<string, object>
        {
            ["cutoff"] = DateTime.UtcNow - retention,
        });
```

- [ ] **Step 3: 빌드** `dotnet build src/00.Main/NexaOne.Server/NexaOne.Server.csproj -c Debug --nologo` → 0 errors.
- [ ] **Step 4: 커밋** `feat(auth): SYS_REFRESH_TOKEN 만료 정리 쿼리 + SysRefreshTokenStore.PurgeExpiredAsync`.

---

## Task 2: RefreshTokenCleanupWorker + 등록

- [ ] **Step 1: 워커 생성** `src/00.Main/NexaOne.Server/Gateway/RefreshTokenCleanupWorker.cs`:
```csharp
using Microsoft.Extensions.Hosting;

namespace NexaOne.Server.Gateway;

/// <summary>리프레시 토큰 만료 정리 워커(호스트 레벨, Quartz 비의존 BackgroundService). enabled 시 시작 직후 1회 +
/// interval마다 SysRefreshTokenStore.PurgeExpiredAsync로 retention 경과 토큰을 삭제한다. 기본 OFF(테스트/CI 무영향).
/// 예외는 잡아 삼켜 다음 주기를 막지 않는다(LoginFailureRetentionWorker 패턴).</summary>
public sealed class RefreshTokenCleanupWorker : BackgroundService
{
    private readonly SysRefreshTokenStore _store;
    private readonly bool _enabled;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _retention;

    public RefreshTokenCleanupWorker(SysRefreshTokenStore store, bool enabled, TimeSpan interval, TimeSpan retention)
    {
        _store = store;
        _enabled = enabled;
        _interval = interval;
        _retention = retention;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            Console.WriteLine("[RefreshTokenCleanupWorker] disabled (enabled=false). Skipping startup.");
            return;
        }
        Console.WriteLine($"[RefreshTokenCleanupWorker] started (interval={_interval.TotalSeconds}s, retentionDays={_retention.TotalDays}).");
        using var timer = new PeriodicTimer(_interval);
        do { await PurgeOnceAsync(stoppingToken); }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PurgeOnceAsync(CancellationToken ct)
    {
        try
        {
            var deleted = await _store.PurgeExpiredAsync(_retention);
            Console.WriteLine($"[RefreshTokenCleanupWorker] purged {deleted} expired refresh token(s).");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Console.WriteLine($"[RefreshTokenCleanupWorker] purge failed: {ex.Message}"); }
    }
}
```
주의: `WaitForNextTickAsync`가 취소 시 OperationCanceledException을 던지므로 `do/while`이 자연 종료된다(StopAsync 시). 별도 catch 불요(BackgroundService가 처리).

- [ ] **Step 2: AuthServiceExtensions 등록 변경** — [AuthServiceExtensions.cs:30-31](../../../src/00.Main/NexaOne.Server/Gateway/AuthServiceExtensions.cs#L30-L31)의 IRefreshTokenStore 등록을 구체+alias로 바꾸고 워커 등록 추가:
```csharp
        var ttl = TimeSpan.FromDays(configuration.GetValue("Jwt:RefreshTokenExpiryDays", 7));
        // 구체 등록(정리 워커가 PurgeExpiredAsync 호출) + 인터페이스 alias(기존 소비자 무변경).
        services.AddSingleton(sp => new SysRefreshTokenStore(
            sp.GetRequiredService<IRuleDispatcher>(), authRegistry, sp.GetRequiredService<IJwtService>(), ttl));
        services.AddSingleton<IRefreshTokenStore>(sp => sp.GetRequiredService<SysRefreshTokenStore>());

        // 만료 토큰 정리 워커(호스트 레벨). 기본 OFF — Auth:RefreshTokenCleanup:Enabled=true로 켠다.
        var cleanupEnabled = configuration.GetValue("Auth:RefreshTokenCleanup:Enabled", false);
        var cleanupInterval = TimeSpan.FromSeconds(configuration.GetValue("Auth:RefreshTokenCleanup:IntervalSeconds", 86400));
        var cleanupRetention = TimeSpan.FromDays(configuration.GetValue("Auth:RefreshTokenCleanup:RetentionDays", 7));
        services.AddHostedService(sp => new RefreshTokenCleanupWorker(
            sp.GetRequiredService<SysRefreshTokenStore>(), cleanupEnabled, cleanupInterval, cleanupRetention));
```
(기존 GatewayLoginService 등록의 `sp.GetRequiredService<IRefreshTokenStore>()`는 alias로 그대로 해석 — 무변경.)

- [ ] **Step 3: 빌드** → 0 errors.
- [ ] **Step 4: 커밋** `feat(auth): 리프레시 토큰 정리 BackgroundService(호스트 레벨, 기본 OFF) 등록`.

---

## Task 3: 테스트 — PurgeExpiredAsync 동작 + 워커 disabled no-op

- [ ] **Step 1: `test/NexaOne.ServerTests/GatewayRefreshTokenCleanupTests.cs` 생성**

GatewayAuthE2ETests 팩토리(modules-OFF, SQLite, DbPath 노출) 패턴 사용. 팩토리 `.Services`로 `SysRefreshTokenStore` 해석. 검증:
1. **만료 정리**: 한 사용자에 IssueAsync로 토큰 발급(EXPIRES_AT=now+ttl) → SqliteConnection으로 그 사용자 토큰의 EXPIRES_AT를 과거(now-30d)로 UPDATE → `PurgeExpiredAsync(TimeSpan.Zero)` 호출 → 반환 ≥1 → 같은 토큰 `ValidateAsync` false(행 삭제됨, COUNT 0).
2. **활성 보존**: IssueAsync로 새 토큰(EXPIRES_AT 미래) → `PurgeExpiredAsync(TimeSpan.FromDays(7))` → 그 토큰 `ValidateAsync` true(보존).
3. **워커 disabled no-op**: `new RefreshTokenCleanupWorker(store, enabled:false, interval:1s, retention:0)` 의 StartAsync→즉시 완료(ExecuteAsync가 enabled=false로 return). 예외 없이 StopAsync 가능.

토큰 EXPIRES_AT를 과거로 만드는 직접 SQL은 GatewayAuthE2ETests의 connection 패턴 참고(`Microsoft.Data.Sqlite`). 토큰 식별이 어려우면 USER_ID로 일괄 UPDATE(테스트 전용 사용자라 안전). ValidateAsync(userId, token)로 존재 판정.

- [ ] **Step 2: 테스트 실행** `dotnet test test/NexaOne.ServerTests/NexaOne.ServerTests.csproj -c Debug --nologo` → 기존(68) + 신규 전부 통과.
- [ ] **Step 3: 커밋** `test(auth): 리프레시 토큰 만료 정리(PurgeExpiredAsync 삭제·보존) + 워커 disabled no-op`.

---

## Task 4 (컨트롤러): 회귀 + 리뷰 + ff-merge
ServerTests 전부 통과 재확인. 리뷰(정리 술어 정확성·활성 토큰 보존·인터페이스 무변경·기본 OFF) 후 main ff-merge(sln 가드, git `2>&1` 금지, push 안 함).

## Self-Review
- 증가 방지: 회전 churn·만료 토큰이 retention 경과 후 삭제(EXPIRES_AT < cutoff). 활성(미만료) 토큰 보존. ✓
- 인터페이스 무변경: PurgeExpiredAsync는 구체 SysRefreshTokenStore에만 추가 — IRefreshTokenStore·레거시 구현 무영향. 구체+alias 등록으로 기존 소비자(GatewayLoginService) 무변경. ✓
- modules 무관: Quartz 비의존 BackgroundService — modules-OFF/ON 모두 동작. 기본 OFF로 테스트/CI 무영향(워커 등록되나 즉시 no-op). ✓
- 테스트 격리: 전용 SQLite DB. PurgeExpiredAsync를 store에서 직접 검증(워커 타이밍 의존 회피). ✓
- 한계: 폐기됐으나 미만료인 토큰은 EXPIRES_AT 경과 전까지 잔존(inert, Validate가 REVOKED_AT 요구) — 허용된 수용.
