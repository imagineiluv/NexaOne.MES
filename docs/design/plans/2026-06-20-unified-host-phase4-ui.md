# 통합 호스트 Phase 4 — Blazor/SPA 흡수 슬라이스 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. 체크박스(`- [ ]`).

**Goal:** 통합 호스트 한 프로세스가 React /spa 정적 + Blazor /meta 화면을 서빙(기존 게이트웨이 데이터 경로 위, 비파괴).

**Architecture:** 점진(C)+컴포넌트 직접 호스팅(A)+RCL. 단일 JwtBearer. 설계: [Phase 4 설계](../specs/2026-06-20-unified-host-phase4-ui-design.md), 격리: [ADR-006](../adr/ADR-006-web-worker-separation.md).

**Tech Stack:** ASP.NET Core 8(Sdk.Web), Blazor Server(InteractiveServer, prerender:false), Vite/React, Razor Class Library. 빌드/테스트 `dotnet ... NexaOne.sln` + `npm`. 커밋 PowerShell BOM-free, `git add -A` 금지, push/merge 금지.

---

## Task 1: SPA 정적 서빙 (호스트)

**Files:** Modify `src/01.Web/NexaOne.Spa/vite.config.ts`, `src/00.Main/NexaOne.Server/Program.cs`, `src/00.Main/NexaOne.Server/NexaOne.Server.csproj`, `.gitignore`. Create `test/NexaOne.ServerTests/SpaStaticServingTests.cs`.

- [ ] **Step 1: vite outDir를 호스트로** — `vite.config.ts`의 `build.outDir`를 `'../NexaOne.Web/wwwroot/spa'` → `'../../00.Main/NexaOne.Server/wwwroot/spa'`로 변경(`base: '/spa/'` 유지). (NexaOne.Web의 기존 wwwroot/spa는 더 이상 산출하지 않음 — 회귀 시 이 한 줄 되돌림.)

- [ ] **Step 2: 호스트 정적 서빙 배선** — `Program.cs`에서 `app.UseSwagger()` 직후(인증 파이프라인 앞 정적 자산 제공) `app.UseStaticFiles();` 추가. `app.MapControllers();` **다음**(명시 라우트 우선 보장)에 `app.MapFallbackToFile("/spa/{*path:nonfile}", "/spa/index.html");` 추가. (Sdk.Web은 wwwroot를 기본 웹루트로 서빙하므로 wwwroot/spa 자산은 UseStaticFiles가 처리.)

- [ ] **Step 3: csproj/gitignore** — NexaOne.Server는 Sdk.Web이라 wwwroot가 기본 포함되지만, 빌드 산출(npm)이 gitignore 대상이므로 `.gitignore`에 `src/00.Main/NexaOne.Server/wwwroot/spa/` 추가. (Sdk.Web wwwroot 자동 서빙·publish 포함이므로 별도 Content Include 불필요 — 단 publish 포함 여부는 빌드로 확인하고, 누락 시 `<Content Include="wwwroot\spa\**" CopyToOutputDirectory="PreserveNewest" />` 추가.)

- [ ] **Step 4: SPA 빌드 + 결정적 fallback 테스트** — `test/NexaOne.ServerTests/SpaStaticServingTests.cs`: 테스트 팩토리(modules OFF + SQLite + JWT, 기존 GatewayMdmE2ETests 패턴)를 쓰되, 테스트 시작 시 팩토리 ContentRoot의 `wwwroot/spa/index.html`을 더미(`<!doctype html><div id=root>SPA-OK</div>`)로 보장(없으면 생성)해 결정적으로 검증:
  - `GET /spa/anything` (nonfile) → 200, 본문에 "SPA-OK"(또는 index 마커) — MapFallbackToFile 동작.
  - `GET /api/v1/query/MDM.PlantList`(인증) 등 기존 라우트가 폴백에 가리지 않고 정상(명시 라우트 우선) — 1건 회귀 확인.
  실제 npm 빌드 산출 검증은 수동(아래 Step 5).
  WebApplicationFactory ContentRoot가 호스트 프로젝트 디렉터리가 아닐 수 있으므로, 더미 index.html을 `factory.Services`의 IWebHostEnvironment.WebRootPath 기준으로 배치하거나 `UseWebRoot`/`UseContentRoot`로 고정한다(구현자가 실제 경로 확인 후 결정 — 핵심은 fallback 라우팅이 200을 주는 결정적 테스트).

- [ ] **Step 5: 빌드 + 수동 SPA 빌드 확인** — `dotnet build NexaOne.sln -c Debug`(0 error). `cd src/01.Web/NexaOne.Spa; npm run build`로 `src/00.Main/NexaOne.Server/wwwroot/spa/index.html` 산출 확인(수동, npm 환경 필요 — 불가 시 그 사실 명시). `dotnet test test/NexaOne.ServerTests` → 신규 + 기존 그린.

- [ ] **Step 6: Commit** — `git add` (vite.config.ts, Program.cs, NexaOne.Server.csproj, .gitignore, SpaStaticServingTests.cs). 메시지: `feat(server): React /spa 정적 서빙 흡수(vite outDir→호스트 + UseStaticFiles + /spa fallback)(Phase 4)`.

---

## Task 2: NexaOne.Web.Components RCL 추출

**개요:** 신규 `src/01.Web/NexaOne.Web.Components`(Microsoft.NET.Sdk.Razor). /meta 의존 폐포를 RCL로 이동: App/Routes/_Imports, MetaScreen + 렌더러(MetaForm/MetaGrid/Layout) + ScreenDefinition 모델 + 인증 서비스(JwtAuthStateProvider/AuthTokenService/AuthContextService/IAuthContext) + IApiClient/ApiClient + IScreenDefinitionProvider/InMemoryScreenDefinitionProvider + 이들이 끌어오는 최소 공용 컴포넌트. NexaOne.Web(exe)·NexaOne.Server가 참조. **구현 전 그라운딩**: NexaOne.Web의 MetaScreen.razor + 렌더러 + 서비스 의존 그래프를 추적해 이동 폐포를 최소 확정(과다 이동 금지). **NexaOne.Web 기존 빌드 + bUnit/Web 테스트 비회귀 필수.**

(상세 단계는 그라운딩 후 본 계획에 보강 — 이동 파일 목록·NexaOne.Web csproj 재배선·RCL csproj. 핵심 제약: RCL은 Default-ALC ProjectReference, ADR-006 모듈 게시 deps-제외 불변, prerender:false 유지.)

## Task 3: 호스트 Blazor 배선 + /meta

**개요:** 호스트가 RCL 참조 + 슬라이스 서비스 DI 등록(Scoped: JwtAuthStateProvider/AuthTokenService/AuthContextService/IApiClient(HttpClient BaseAddress=자기 origin), Singleton: IScreenDefinitionProvider) + `AddRazorComponents().AddInteractiveServerComponents()` + `AddCascadingAuthenticationState()` + 호스트 최소 `HostApp.razor`/`HostRoutes.razor`(/meta/{uiId} + 로그인 + NotFound만 노출, 27화면 미노출) + `app.UseAntiforgery()` + `app.MapRazorComponents<HostApp>().AddInteractiveServerRenderMode()`(§5 순서). DevAutoAuthHandler 미등록. (상세는 Task 2 RCL 경계 확정 후 보강.)

## Task 4: 테스트 + 검증

**개요:** (자동) /meta E2E — modules OFF + SQLite + 게이트웨이로 ScreenDefinition(InMemory) 로드→그리드 조회(/query)→폼 저장(/command) 경로를 bUnit 또는 호스트 통합테스트로 검증(WebApplicationFactory가 Blazor 컴포넌트 렌더 가능 범위 내; circuit 상호작용이 불가하면 렌더러 단위(bUnit)+게이트웨이 통합으로 분할). (수동) 단일 프로세스 풀 기동(modules ON + npm 빌드)에서 /spa 로드 + /meta 화면 + 로그인 E2E. 인증/감사 비회귀. 결과를 설계문서 §7에 기록.

---

## Self-Review
- Task 1은 독립 비파괴 증분(SPA 정적) — 단독 머지 가능. Task 2(RCL)가 위험 핵심 — 그라운딩 후 상세화 + NexaOne.Web 비회귀 게이트. Task 3/4는 RCL 경계 의존이라 Task 2 완료 후 상세 확정.
- 단일 JwtBearer·prerender:false·라우팅 우선순위·ADR-006 deps-제외 불변 — 설계 §4/§5/§8 준수.
- 미해결: WebApplicationFactory의 Blazor circuit/정적 ContentRoot 테스트 한계 → 결정적 부분은 자동, 풀 기동은 수동(Phase 1 패턴).
