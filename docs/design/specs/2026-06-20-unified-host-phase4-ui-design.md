# 통합 호스트 Phase 4 — Blazor/SPA UI 흡수 설계 (대표 슬라이스)

> 상태: 승인(범위=SPA 정적 + Blazor /meta 슬라이스 확정) · 작성일 2026-06-20
> 상위: [통합 호스트 설계](2026-06-20-unified-host-design.md) §2(접근 A)·§7(Phase 4) · 격리: [ADR-006](../adr/ADR-006-web-worker-separation.md)

## 1. 목적

통합 호스트(NexaOne.Server) 한 프로세스가 **UI까지 서빙**하도록, React SPA 정적 서빙 + Blazor 대표 화면(/meta) 흡수를 **비파괴 점진**으로 시작한다. 사용자 최종 목표('dotnet run 한 번으로 /designer 실시간 편집', Phase 5)의 기반(Blazor 호스팅 + 메타 런타임)을 깐다.

## 2. 왜 전체가 아니라 슬라이스인가 (검증된 차단요인)

- 통합 호스트는 현재 `/api/v1/{auth,query,command}` + EST/RMS 얇은 브리지만 노출. **MDM/FDC/QMS/CMMS/POM/SHP/SYS 컨트롤러(~150 라우트)와 `/hubs/smartees` SignalR 허브가 없다.**
- Blazor `ApiClient`(typed HttpClient)는 이 api/v1/* 라우트를 호출 → **전체 Blazor를 통째 흡수하면 27개 화면이 404/실시간 깨짐.**
- 따라서 **기존 게이트웨이(/query·/command)만으로 E2E가 성립하는 화면 1개(/meta)**로 한정하고, 나머지는 §10 명명쿼리·허브 흡수 도착 전까지 NexaOne.Web 잔류(하이브리드).

## 3. 접근 — 점진(C) + 컴포넌트 직접 호스팅(A) + RCL

- **RCL 추출**: NexaOne.Web의 **공유 가능한 조각**(App/Routes/_Imports, /meta 페이지 `MetaScreen` + 렌더러 `MetaFormRenderer`/`MetaGridRenderer`/`LayoutRenderer`, `ScreenDefinition` 모델, 인증 서비스 `JwtAuthStateProvider`/`AuthTokenService`/`AuthContextService`/`IAuthContext`, `IApiClient`/`ApiClient`, `IScreenDefinitionProvider`/`InMemoryScreenDefinitionProvider` 및 /meta가 요구하는 최소 의존)를 신규 **`NexaOne.Web.Components`(Microsoft.NET.Sdk.Razor)** 로 옮긴다. NexaOne.Web(exe)·NexaOne.Server(host) 둘 다 이 RCL을 참조 — 컴포넌트 단일 출처.
- **호스트 라우터 범위 제한**: 호스트는 **자체 최소 `App`/`Routes`**(또는 라우트 필터)로 **/meta + 로그인 + NotFound만** 노출한다. 27개 미흡수 화면 페이지는 호스트 라우터에 포함하지 않아 깨진 화면 노출을 막는다(NexaOne.Web exe는 전체 App으로 모든 화면 유지).
- **NexaOne.Web 잔류(하이브리드)**: 전환기 동안 NexaOne.Web(별도 포트)이 전체 앱을, 호스트가 슬라이스(+API)를 담당. 리버스 프록시·BaseAddress 분기 회피.

## 4. 인증 — 단일 JwtBearer (쿠키 미도입)

- Blazor 클라이언트측 `JwtAuthStateProvider`(localStorage 토큰 JS interop)가 서버 스킴과 독립 동작 → 두 번째 스킴 불필요. 호스트의 기존 `AddAuthentication(JwtBearer)` 그대로.
- **`prerender:false` 필수**(SSR 프리렌더 단계엔 클라이언트 토큰 미상 → [Authorize] 로그인 루프 방지). `App.razor`의 `InteractiveServerRenderMode(prerender:false)` 유지.
- **`DevAutoAuthHandler`는 호스트에 미등록**(prerender:false면 불필요, 보안 경계 아님).
- 흡수된 화면의 `ApiClient.BaseAddress`는 **자기 origin**(/meta가 쓰는 query/command/auth가 모두 호스트에 존재) → CORS/AllowCredentials 불필요.

## 5. 라우팅 우선순위 (호스트 Program.cs)

미들웨어: `UseSwagger` → `UseAuthentication` → `AuditUserContextMiddleware` → (`UseRateLimiter` 조건부) → `UseAuthorization` → **`UseStaticFiles`** → **`UseAntiforgery`** → 엔드포인트.
엔드포인트 순서(명시 라우트 우선):
1. `MapControllers` — `/api/v1/*` (auth·query·command·est·rms) — **절대 폴백에 가리지 않게 최우선**.
2. `MapHealthChecks("/health")` 익명 + `/diag` 인증.
3. `MapRazorComponents<HostApp>().AddInteractiveServerRenderMode()` — Blazor 라우터(/meta/{uiId}, 로그인) + `/_blazor` circuit.
4. `MapFallbackToFile("/spa/{*path:nonfile}", "/spa/index.html")` — nonfile 폴백(정적 자산은 `UseStaticFiles` 선처리, React BrowserRouter만 폴백).
충돌 없음: api/v1·/health는 명시, /spa는 nonfile, Blazor가 나머지.

## 6. 빌드/배포

- Vite `outDir`를 `../../00.Main/NexaOne.Server/wwwroot/spa`로 변경(`base='/spa/'` 유지). `NexaOne.Server.csproj`에 `wwwroot/spa/**` Content 복사. `.gitignore`에 호스트 `wwwroot/spa` 등록(매 npm 빌드 산출).
- `dotnet build` 전 `npm run build` 선행 필요(전환기 수동; publish Target은 후속). NexaOne.Web의 `wwwroot/spa` 출력은 중단(SPA는 더 이상 Web으로 출력 안 함) — 회귀 시 vite outDir만 되돌림.

## 7. 비파괴 단계화 (이 슬라이스 내부)

1. **SPA 정적 흡수**: vite outDir→호스트, 호스트 csproj 복사, Program `UseStaticFiles`+`MapFallbackToFile(/spa)`. 기존 게이트웨이/인증/health 회귀 무영향. (Task 1)
2. **RCL 추출**: `NexaOne.Web.Components` 신설, 공유 조각 이동, NexaOne.Web(exe) 재배선 — **NexaOne.Web 기존 동작·테스트(bUnit) 비회귀 확인**. (Task 2)
3. **호스트 Blazor 배선**: RCL 참조 + 슬라이스 서비스 DI + `AddRazorComponents().AddInteractiveServerComponents()` + 호스트 최소 `HostApp`(/meta+로그인) + `MapRazorComponents` + `UseAntiforgery`. (Task 3)
4. **검증**: SPA 정적 스모크 + /meta E2E(정의 로드→그리드 조회→폼 저장, SQLite 게이트웨이) + 인증/감사 비회귀. WebApplicationFactory가 plugin ALC를 못 띄우므로 /meta E2E는 modules OFF + 게이트웨이만으로 성립(정의는 InMemoryScreenDefinitionProvider). 단일프로세스 풀 기동은 수동. (Task 4)

## 8. 위험

- **라우트 공백**: 전체 컴포넌트 흡수 시 27화면 404 → 호스트 라우터를 /meta+로그인으로 제한(§3). NexaOne.Web 잔류.
- **prerender 루프**: prerender:false 미유지 시 전 화면 로그인 무한루프 → HostApp에 InteractiveServer(prerender:false) 명시.
- **ALC 동일성(ADR-006)**: RCL이 plugin ALC로 중복 로드되면 EST/RMS 브리지 GetBean 캐스트 깨짐 → RCL은 Default ALC ProjectReference, 모듈 게시 deps-제외 불변 유지(호스트 csproj CopyDomainModulePlugins 무변경).
- **NexaOne.Web 회귀**: RCL 추출이 기존 Web 화면/테스트를 깨뜨릴 위험 → Task 2에서 NexaOne.Web 빌드 + 기존 bUnit/Web 테스트 그린 확인.
- **수명주기**: /meta는 게이트웨이(CurrentUserContext AsyncLocal + per-call 연결)만 사용 → 싱글톤-요청 스코프 안전. 타입드 브리지 화면 흡수 시 재검증(차기).
- **파이프라인 누락**: UseAntiforgery(Blazor 폼)·UseStaticFiles 누락 주의. OTel은 차기(슬라이스 비차단).
- **빌드 결합**: npm 빌드 선행 누락 시 빈 SPA → 전환기 수동, publish Target 후속.

## 9. 연기(차기 증분/Phase)

- 27개 타입드 라우트 화면: §10 명명쿼리 도착 후 화면별 이전(NexaOne.Web→호스트).
- SignalR 허브(/hubs/smartees) 흡수 + 실시간 화면(Dashboard/FdcMonitor/EST Alarms).
- NexaOne.Web/NexaOne.API 프로세스 은퇴(전 화면·허브 흡수 완료 후).
- /designer 실시간 WYSIWYG(GrapesJS) — Phase 5(이 슬라이스가 /meta 런타임을 호스트에 두어 디자이너가 같은 ScreenDefinition·게이트웨이를 타겟하게 함).

## 10. 미해결 결정(슬라이스 진행 중 확정)

- RCL 추출 경계(어떤 서비스/컴포넌트까지 RCL로) — Task 2 그라운딩에서 /meta 의존 폐포(closure)로 최소화 확정.
- 호스트 포트 — 전환기 호스트 5180 / NexaOne.Web 5000 유지(설계서 §8.3), 전 흡수 후 단일화.
