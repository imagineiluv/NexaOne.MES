# 단일 프로세스 통합 호스트 설계 (접근 A — 순수 플러그인, 단계적)

> 상태: 승인 대기(브레인스토밍 산출 스펙) · 작성일 2026-06-20
> 관련: ADR-005(서비스 빈 컨테이너), ADR-006(모듈 플러그인 격리), [Frontend-Coexistence.md](../Frontend-Coexistence.md), [GrapesJS 디자이너 스펙](2026-06-19-grapesjs-screen-designer-design.md)

## 1. 목적

`dotnet run` 한 번으로 **단일 프로세스**가 NexaOne.Web(Blazor UI) + NexaOne.API(HTTP API) + NexaOne.Server(백그라운드 워커) 기능을 동시에 수행하고, 그 프로세스에서 `/designer` URI로 실시간(WYSIWYG) 화면 디자인 수정이 되게 한다. 모듈 플러그인 격리(ADR-006)는 유지한다.

## 2. 확정된 결정 (브레인스토밍)

| 갈림길 | 결정 | 근거 |
|---|---|---|
| 통합 방식 | **접근 A — NexaOne.Server를 웹 호스트로 승격** | Server가 이미 Spring 플러그인 부트스트랩 + 워커 발견을 수행 |
| 조립 모델 | **Spring.NET 플러그인 유지(ADR-006)** | 모듈 격리·핫플러그 보존 (사용자 결정) |
| ALC 방향 | **순수 플러그인(단계적)** | 모듈은 plugin ALC에만 존재(Default ALC 중복 불가) — 격리 최대 |
| 진행 | **Phase 1부터 단계별** | 규모가 단일 스펙엔 과대 — 각 단계가 동작하는 단일 프로세스를 산출 |
| 계약 표면 | **하이브리드(게이트웨이 우선)** | 조회·명명쿼리 쓰기는 기존 `/query`·`/command` 게이트웨이(Dictionary, 브리지 불필요), 복잡 typed 서비스만 DTO 계약 브리지 |

## 3. 현재 사실 (실제 코드 기준, 2026-06-20 검증)

- **NexaOne.API** (`Sdk.Web`): `WebApplication`. 모듈을 **직접 DI**로 조립(`AddNexaOneServices` ~130줄, 9개 모듈 전체 리포지토리·서비스 — Default ALC). 워커는 `AddHostedService`(FdcCollectorHostedService·OutboxDispatcherService·InMemoryBusSubscriberService). Spring은 `/rule/{ruleName}` 한 경로에서 `ApplicationServer.GetBean("NexaOne", rule)`로만 사용(reflection, catch-all).
- **NexaOne.Web** (`Sdk.Web`): Blazor Server UI. `ApiBaseUrl`로 API 호출. `/spa`는 `MapFallbackToFile`로 정적 서빙(React 빌드 산출물 `wwwroot/spa`).
- **NexaOne.Server** (`Exe`, Generic Host): `ApplicationServer.CreateServer(Spring/server.xml)` + `app.xml`의 Service 9개를 `AddService(configFiles=Spring/*.xml, classPaths=./Modules/*.dll)`로 **plugin ALC** 로드. 모듈 컨텍스트에서 `IHostedService` 워커를 발견해 Generic Host에 등록. HTTP 없음.
- **모듈 xml은 얇은 슬라이스**: `Spring/mdm.xml`은 `equipmentRepository`+`equipmentService` 2개뿐. 풀 서비스 그래프는 API의 직접 DI에만 존재.
- typed 컨트롤러는 **구상 모듈 타입**(`EquipmentService`·`FdcDataService` 등)을 직접 주입.
- 공통 빈은 server.xml(부모, Default ALC): `eesDataSource`·`appConfiguration`·`opcUaDriver`·`plantController`·`cacheService`·`messageBus`·`quartzScheduler`·`outboxRepository`·`scheduledOutboxDispatchWorker`.
- xml 파일은 `src/00.Main/NexaOne.Server/Spring/`로 정리 완료(커밋 7267fc4).

## 4. 목표 아키텍처

NexaOne.Server = 유일 실행 호스트(`WebApplication`). 한 프로세스에서:
1. Spring.NET ApplicationServer가 9개 모듈을 **plugin ALC로 격리 로드**(ADR-006).
2. 모듈/서버 컨텍스트의 **IHostedService 워커** 구동(아웃박스 디스패치·FDC 수집 등).
3. **ASP.NET 파이프라인**: 컨트롤러·Swagger·JWT·SignalR·CORS·Rate Limit·HealthChecks·OTel·Serilog.
4. **Blazor Razor Components**(UI) + **`/spa` 정적**(React) 서빙.
5. `/designer` → 실시간 WYSIWYG(GrapesJS Phase 1b).

NexaOne.API·NexaOne.Web은 별도 실행 호스트에서 은퇴. 그 컨트롤러/컴포넌트는 통합 호스트로 흡수하거나 라이브러리로 참조.

## 5. 핵심(crux): plugin ALC ↔ ASP.NET DI 브리지

**하이브리드 결정(범위 축소):** 데이터 주 경로는 기존 게이트웨이(`/query`·`/command` → `IRuleDispatcher`+`IQueryRegistry`, 모두 Default ALC·Dapper·파일쿼리 — plugin 타입 무관)다. 이 경로는 **브리지가 전혀 필요 없다**(Dictionary 반환). 또한 모든 모듈 리포지토리는 감사 사용자를 `CurrentUserContext`(AsyncLocal, `RequestLogContextMiddleware`가 요청별 설정)에서 읽고 per-call 연결을 써서 **이미 싱글톤 안전**이다(요청 스코프 빈·트랜잭션 개편 불요). 따라서 아래 plugin↔DI 브리지는 **명명쿼리로 표현 불가한 복잡 typed 서비스에 한해** DTO 계약으로 적용하며, 그 시점까지 연기한다.

복잡 typed 서비스용 브리지(필요 시) — HTTP 컨트롤러(Default ALC)가 plugin ALC의 모듈 서비스를 타입 안전하게 사용하려면:

1. **모듈 서비스 계약(인터페이스)을 Default ALC 공유 어셈블리로 추출** — 신규 `NexaOne.Contracts`(또는 기존 공유 어셈블리). plugin ALC 모듈은 이 어셈블리를 *참조만* 하고 자기 사본을 로드하지 않아야(ADR-006의 공유 의존성 흐름: `.deps.json` 미복사로 Default ALC 해소) 캐스팅 타입 동일성이 성립한다.
2. **모듈 서비스 전체를 Spring 빈으로 재배선** — 현재 슬라이스(mdm.xml 2개)를 9개 모듈 전체 그래프로 확장(각 빈의 생성자 의존을 ref로 배선).
3. **브리지 등록** — 웹 호스트 DI에 각 계약을 `appServer.GetBean("Mdm","equipmentService")` 캐스팅으로 등록. 반환 인스턴스(plugin ALC 구상)는 Default ALC 공유 계약을 구현하므로 캐스팅 성립.

## 6. 핵심 위험 (정직한 평가)

- **수명주기 불일치**: Spring 빈은 싱글톤 지향(서버/워커 컨텍스트)인데 HTTP는 요청 스코프(트랜잭션 `SqlTxnContext`, 요청별 감사 사용자 `ServiceObjectProcessor`가 `HttpContext` 사용자 판독). 싱글톤 모듈 빈을 요청 스코프 컨트롤러에 브리지하려면 트랜잭션·감사-사용자 처리를 재설계해야 한다(요청 컨텍스트를 Spring 밖 accessor로 주입하거나, 모듈 서비스가 요청 스코프 협력자를 메서드 인자/AsyncLocal로 받게). **Phase 2의 1-모듈 E2E에서 우선 해결한다.**
- **ALC 경계 정합**: 계약 어셈블리가 Default ALC 공유 집합에 정확히 배치돼야 캐스팅 성립(`.deps.json` 제외 규약 준수). 어긋나면 `InvalidCastException`/타입 중복.
- **인증 병합**: API JWT Bearer(+FallbackPolicy) + Blazor 클라이언트 JWT(+dev 자동 스킴)를 한 파이프라인에서 엔드포인트별로 공존시켜야 함(API 경로 vs Blazor 페이지 vs /spa).
- **회귀 표면**: API·Web의 풍부한 파이프라인(OTel·Serilog·RateLimit·SignalR·HealthChecks·미들웨어)을 누락 없이 이전해야 함.

## 7. 단계 분해 (각 단계 = 독립 증분, 자체 스펙·플랜·구현·검증)

- **Phase 1 — 통합 호스트 셸** (저위험 기반, 본 스펙 §8 상세). Server를 `WebApplication`으로 전환, 기존 Spring 부트스트랩 + 워커 유지, ASP.NET 파이프라인(헬스/Swagger/JWT) + 진단 엔드포인트만. 컨트롤러·UI 없음. 산출: "웹+플러그인+워커가 한 프로세스 기동".
- **Phase 2 — 게이트웨이 우선 데이터 경로 + MDM E2E (하이브리드 확정 반영)**. 통합 호스트(NexaOne.Server)에 게이트웨이(`/query`·`/command`[+`/rule`]) 컨트롤러 + `AddNexaOneEES`(IRuleDispatcher·IQueryRegistry) + `CurrentUserContext` 미들웨어(RequestLogContextMiddleware 포팅) + JWT를 들이고, **MDM 명명 쿼리(예: MDM.PlantList/CreatePlant)로 SQLite E2E**(조회→저장→조회)를 입증한다. plugin↔DI 타입 브리지·DTO 계약은 **명명쿼리로 표현 불가한 복잡 typed 서비스에 한해** 후속 하위단계로 연기(MDM 단일 서비스로 먼저 검증 후 필요 모듈만 복제). 쿼리 라이브러리 고도화(§10)가 이 단계의 핵심 산출.
- **Phase 3 — 게이트웨이-최대 + 얇은 브리지 (확정)**. 18개 API 컨트롤러 중 ~14개가 plugin ALC 구상 모듈 서비스에 의존하므로, 전수 브리지 대신: ① MDM/STD/QMS 등의 CRUD·조회를 **명명 쿼리(게이트웨이)로 이전**(§10 이식 확대 — 통합 호스트가 그 데이터를 서빙) ② **인증(Auth/토큰 발급)**을 통합 호스트에 도입(게이트웨이·UI가 토큰을 얻어야 함; AuthController+JwtService+SYS 사용자 경로 — 직접 포트 또는 얇은 브리지) ③ **진짜 복잡한 서비스(LotTracking·FdcCollector·Workflow 등)만** plugin↔DI DTO 브리지(§5, MDM 1개로 먼저 E2E 검증 후 필요한 것만) ④ 공통 미들웨어(CORS/RateLimit/SignalR/OTel/Serilog) 점진 병합. NexaOne.API는 점진 은퇴(엔드포인트가 게이트웨이/브리지로 대체되는 대로). 첫 증분 권장: §10 MDM/STD 조회·콤보 배치 + 통합 호스트 인증.
- **Phase 4 — Blazor UI + /spa**. Razor Components + 정적 SPA 서빙 흡수, Blazor 인증 병합. NexaOne.Web 은퇴.
- **Phase 5 — /designer 실시간 WYSIWYG**. GrapesJS Phase 1b를 통합 호스트 `/spa`에서 `/designer`로.

## 8. Phase 1 상세 설계 (이번 증분)

**목표**: NexaOne.Server가 `WebApplication`으로 기동하면서 (a) 기존 Spring 플러그인 컨텍스트(server.xml + 모듈)를 그대로 로드하고, (b) 발견된 IHostedService 워커를 구동하고, (c) ASP.NET 웹 파이프라인(HealthChecks·Swagger·JWT 인증)을 노출하고, (d) 로드된 서비스/워커를 보여주는 진단 엔드포인트를 제공한다. **컨트롤러·모듈 HTTP·UI는 없음**(후속 단계).

**8.1 프로젝트(csproj)**: `Microsoft.NET.Sdk` → `Microsoft.NET.Sdk.Web`. `<OutputType>Exe</OutputType>` 제거(Web SDK가 Exe). 기존 ProjectReference(모듈 `ReferenceOutputAssembly=false`·NexusCom.Data 메타·OPC-UA)·`CopyDomainModulePlugins` 타깃·`Spring/*.xml` Content·`db/migrations` Content 모두 유지. 추가 패키지: `Microsoft.AspNetCore.Authentication.JwtBearer`, `Swashbuckle.AspNetCore`, `AspNetCore.HealthChecks.SqlServer`, `Serilog.AspNetCore`(선택; Phase 1 최소는 JwtBearer+Swashbuckle+HealthChecks).

**8.2 Program.cs**: `Host.CreateApplicationBuilder` → `WebApplication.CreateBuilder`. 기존 부트스트랩 보존: `EnsureSqliteSchemaIfConfigured("Spring/server.xml")` → `_server.CreateServer(["Spring/server.xml"])` → `app.xml` 파싱 → 모듈별 `_server.AddService(...)` → 워커 수집(부모+모듈 컨텍스트의 `IHostedService`, 인스턴스 중복 제거) → `builder.Services.AddSingleton<IHostedService>(w)`. 이어서 ASP.NET 서비스: `AddSingleton(_server)`, `AddHealthChecks`(DB 공급자 조건부 SqlServer), `AddEndpointsApiExplorer`+`AddSwaggerGen`, `AddAuthentication(JwtBearer)`+`AddAuthorization`(API와 동일 Jwt:SecretKey 검증·플레이스홀더 거부). `builder.Build()` → `app.UseSwagger()`(+개발 UI)·`UseAuthentication/UseAuthorization`·`MapHealthChecks("/health")`(익명)·진단 엔드포인트 `MapGet("/diag", ...)`(로드된 Service 이름·워커 수 반환, 인증 필요) → `await app.RunAsync()`. 종료 시 `_server.Dispose()`는 `app.Lifetime.ApplicationStopped`에 연결.

**8.3 포트/구성**: `ASPNETCORE_URLS`로 지정(전환기 충돌 회피로 기본 제안 5179; 최종 단일화 후 5180). DB 공급자/연결은 server.xml(Spring)과 ASP.NET `IConfiguration` 양쪽이 필요 — Phase 1은 헬스/부팅 검증이 목적이므로 SQLite 모드(server.xml [SQLite] 블록 또는 환경변수)로 외부 DB 없이 기동 가능하게 한다.

**8.4 테스트/검증**:
- 빌드 0오류/0경고.
- 기동 스모크: `dotnet run` 후 `/health` 200, `/diag`가 9개 Service + 발견 워커 수를 반환(워커 enabled=false 기본이라 등록은 되되 기동 OFF). plugin ALC로 모듈 DLL이 로드됨을 로그로 확인.
- (가능하면) `WebApplicationFactory<Program>` 통합 스모크 1건: SQLite 설정으로 `/health` 200. plugin ALC + WebApplicationFactory 상호작용이 불가하면 수동 기동 검증으로 대체하고 그 사실을 명시(무자르기 금지).
- 기존 NexaOne.API·NexaOne.Web·전체 테스트 스위트 회귀 무영향(Server 단독 변경).

**8.5 하위호환**: 이 단계는 NexaOne.Server만 변경. API/Web은 그대로 동작(아직 별도 호스트). 모듈/Spring/워커 의미론 무변경 — 호스트 종류만 Console→Web.

## 9. 미해결/후속 단계 결정
- 수명주기(싱글톤↔요청 스코프) 재설계 구체안 — Phase 2에서 MDM E2E로 확정.
- 계약 어셈블리 경계: 신규 `NexaOne.Contracts` vs 기존 공유 어셈블리 재사용 — Phase 2.
- 인증 스킴 공존(API/Blazor/SPA) 구체안 — Phase 3/4.
- NexaOne.API/Web 은퇴 방식(삭제 vs 라이브러리 흡수) — Phase 3/4.
- `/designer`가 Blazor 라우트인지 /spa 리다이렉트인지 — Phase 5(GrapesJS Phase 1b 스펙과 통합).

## 10. 쿼리 라이브러리 고도화 (병행 워크스트림)

검증은 **SQLite + NexaMes 스키마**(`db/migrations`)로 한다(외부 DB 불필요, V001 admin/admin 시드). 메타데이터 런타임(`/meta`)·GrapesJS 디자이너 카탈로그가 실데이터로 동작하도록, 레거시 명명 쿼리를 NexaMes 레지스트리로 **고도화 이식**한다.

- **출처**: `reference/legacy_3.5_20260526/Config/Query/xml/`(모듈별 ees/qms/standard/factory, 방언별 oracle/postgresql/기본=mssql). 현재 NexaMes 레지스트리는 `db/queries/{mssql,sqlite}/MDM.xml` 한 개(대표 슬라이스)뿐.
- **타깃**: `db/queries/mssql/*.xml` + `db/queries/sqlite/*.xml`(파일 기반 레지스트리, `FileQueryRegistry`가 방언 폴더 로드).
- **변환 규칙(고도화 = 단순 번역 아님, 스키마-인지 매핑)**:
  1. **보간 → 파라미터화**: `$!{X}` 문자열 보간·`#if…#end`·`${SQLFunc.IN()}` → `@param` 바운드 + `(@p IS NULL OR col=@p)` 선택필터(주입 차단).
  2. **스키마 재매핑**: 레거시 `STD_TB_*`/`MDM_TB_*`/다국어 컬럼(`*_KO_KR/EN_US/…`)/`VALID_STATE='Valid'` → **실제 NexaMes 스키마**(예: `MDM_PLANT`의 단일 `PLANT_NAME`). 레거시 다국어 CASE는 NexaMes 단일 컬럼으로 축약하거나 NexaMes 다국어 정책에 맞춤. **반드시 `db/migrations`로 실제 테이블·컬럼을 확인하고 SQLite로 실행 검증**(존재하지 않는 레거시 테이블/컬럼을 그대로 옮기지 않는다).
  3. **방언 분리**: mssql(`WITH(NOLOCK)` 등) / sqlite(힌트 제거) 두 벌, 동일 ID·동일 의미.
  4. **보안 주석**: 읽기는 선택 `requiredPermission`, 쓰기는 `kind="write"` + 필수 `requiredPermission`, 감사 컬럼은 `@currentUser/@utcNow` 게이트웨이 주입.
  5. **ID 규약**: NexaMes 점-표기(`MODULE.Action`, 예: `MDM.PlantList`) — 레거시 `MICUBE.STANDARD.*` 장황 ID는 NexaMes 규약으로 정리.
- **범위·순서**: 블라인드 전수 이식(100+ 파일) 금지. **NexaMes가 실제 보유한 모듈**(MDM/STD→MDM, WPM→POM, FDC, QMS, EMS→CMMS, DLV→SHP, EQP/RMS→RMS, EPT→EST)부터, 각 모듈의 화면·런타임이 데이터를 서빙하는 단계(Phase 2 이후)와 디자이너 카탈로그(Phase 5)에 맞춰 모듈별로 이식·검증한다. 첫 배치는 메타데이터 화면·디자이너 드롭다운이 필요로 하는 MDM/STD 조회·콤보(plant/code/item-class/equipment list·tree) 권장.
- **검증**: 이식한 각 쿼리를 SQLite NexaMes 스키마에 대해 실행해 결과/오류 0 확인(통합 스모크). 미존재 스키마 의존 쿼리는 보류하고 그 사실을 명시(무자르기 금지).
