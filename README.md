# NexaMES

NexaMES는 제조 실행과 설비 운영을 하나의 서버에서 조립하는 모듈형 MES입니다. 생산·품질·설비·레시피 같은 공통 업무 규칙은 재사용 가능한 서비스로 두고, 설비 시퀀스·고객사 연동·현장별 LOT 의미처럼 프로젝트마다 달라지는 규칙은 플러그인 경계에 둡니다.

현재 공통 서비스는 이 저장소에서 먼저 검증합니다. API·데이터 무결성·SQLite와 SQL Server 계약이 안정된 뒤에만 NexaFramework로 이관합니다.

## 구성

| 영역 | 책임 |
|---|---|
| `NexaOne.Server` | HTTP API, 인증, Spring.NET 조립 루트, 모듈 로딩, SQLite/MSSQL 부팅 |
| `NexaOne.Common` | 모듈 간 서비스 계약, 감사·멱등성·영속성 공통 기반 |
| `MDM` | Plant, 설비, 품목, 작업조, 캐리어 등 기준정보 |
| `POM` | 생산 W/O, WorkScope, 공정 LOT, 공정 이력과 처분 |
| `IVT` | 자재 LOT, 투입·소모 원장과 TRACE projection |
| `QMS` | 검사, 불량, 폐기와 품질 이력 |
| `EMS` | PM/BM, 보전 W/O, 예비부품, BOM·공급처, Tool 점검·교정 |
| `EST` | 설비 상태, 출력, Utility, OEE와 설비 실행 증거 |
| `RMS` | Recipe 승인, 버전, Parameter, 설비 할당과 실행 snapshot |
| `FDC` | 설비 통신 endpoint와 driver adapter, 수집 계약 |
| `NexaOne.Project.Cleaner` | Cleaner의 Carrier Pair 증거를 WorkScope 전이로 해석하는 순수 프로젝트 정책 |

세 개의 재사용 저장소는 `submodules/` 아래에서 고정 commit으로 참조합니다.

- `NexaFramework`: 공통 실행·확장·Spring.NET hosting 계약
- `NexaDB`: DB access, messaging와 알림 기반
- `NexaLogic`: PLC protocol driver와 선택형 hosting helper

Driver 구현은 업무 서비스나 Component에서 다시 만들지 않습니다. 제품은 driver 계약을 직접 참조하거나 조립 루트에서 주입하고, `Hosting`은 lifecycle을 편하게 묶을 때만 선택합니다.

## 공통 서비스와 플러그인 경계

공통 서비스가 소유하는 것은 여러 설비와 MES에서 의미가 같은 규칙입니다. 예를 들면 작업자 감사 이력, optimistic concurrency, 멱등 처리, PM/BM 계획과 W/O 일치, 재고 원장, Tool 장착·사용 시간축, Recipe 승인 이력, Utility 계량 경계와 OEE 계산입니다.

플러그인이 소유하는 것은 프로젝트별 정책입니다. 예를 들면 캐리어 세척 시퀀스, 고객사별 알람 해제 조건, MES payload mapping, 자재 소모 방식, LOT/Carrier 변환, 설비별 parameter 해석입니다. 플러그인은 공통 계약을 사용하지만 공통 모듈의 DB 내부 구현을 직접 참조하지 않습니다.

Spring XML은 런타임 조립에 사용합니다. 모듈 구현을 상속해 제품 로직을 끼워 넣지 않고, 안정된 interface를 구현한 adapter 또는 plugin을 `config/host/*.xml`과 `config/modules/*.xml`에서 연결합니다. 단순하고 고정된 코드 경로는 생성자 주입으로 직접 참조하고, 교체 가능성이 있는 프로젝트 정책만 XML seam을 사용합니다.

Cleaner WorkScope 연결은 이 경계를 실제로 사용합니다. HTTP 수신은 V156 불변 inbox/current와 V157
application 행까지만 한 트랜잭션으로 기록하고 즉시 수락 결과를 반환합니다. POM worker가 DB lease를
획득한 뒤 `NexaOne.Project.Cleaner`의 순수 policy를 트랜잭션 밖에서 호출하고, 반환된 effect 전체와
`POM_WORK_SCOPE_EXECUTION`, application 상태·감사 이력을 하나의 serializable transaction으로 반영합니다.
따라서 `202 Accepted`는 업무 반영 완료를 뜻하지 않으며, 프로세스 재시작이나 정책 예외가 있어도
`Pending`/`Retry` 상태에서 이어집니다. LOT는 만들지 않고 terminal cleanup이 영속 완료된
`Completed`/`Abandoned`만 WorkScope 종결 후보가 됩니다. Cleaner 작업은 캐리어별 상태가 아니라
pair 단위 상태를 가지므로 `ScopeType=Other`, `TargetId=PairRunId`, `PlanQty=2`인 WorkScope 하나를 사용합니다.
두 Carrier ID·lane·cleaning run은 V157의 불변 정규화 증거로 보존해 캐리어별 이력을 조회하되 서로 다른
완료 상태를 만들지 않습니다. 프로젝트 정책 교체 지점은 `config/projects/cleaner.xml`, 선택형 application
runtime 조립 지점은 `config/modules/pom-projection.xml`입니다. 코어 `pom.xml`에는 inbox/current 수신 bridge와
SQLite schema contribution만 남으므로 projection 적용 기능을 쓰지 않는 POM 제품은 프로젝트 policy 없이도
조립됩니다. POM의 저장소·lease 구현은 프로젝트 assembly에 노출하지 않습니다.
하나의 WorkScope는 최초 수락된 `(SourceClientId, EquipmentId, SequenceRunId)` stream 하나에만 결박됩니다.
다른 stream이 같은 WorkScope를 사용하면 `409 Projection.WorkScopeBindingConflict`로 거부하며 inbox·carrier·
application에 부분 증거를 남기지 않습니다. V157 unique index가 동시 최초 결박까지 최종 차단합니다.
호스트의 기본 service manifest는 기존 Cleaner 구성을 담은 `config/app.xml`입니다. 다른 project 제품은
core host source를 수정하지 않고 별도 manifest와 그 manifest가 참조하는 plugin DLL을 함께 배포합니다.
빌드 시 `NexaOneProductProfile`이 선택한 manifest는 산출물의 `config/app.xml`로 연결되고,
`Server:ApplicationManifest`(환경변수 `Server__ApplicationManifest`)는 배포 뒤에도 다른 로컬 manifest를
선택할 수 있는 runtime override seam으로 유지됩니다.
업그레이드 직후 과거 current 증거가 자동으로 업무 상태를 바꾸지 않도록 worker 기본값은 OFF입니다.
V157 복원본 리허설과 프로젝트 정책·대상 WorkScope 검증을 마친 배포에서만
`Worker:Pom:WorkScopeProjection:Enabled=true`로 명시 활성화합니다. 활성화된 worker는 HTTP readiness가
열리기 전에 worker가 사용하는 V157 필수 schema, DB read/write 권한과 WorkScope 단일-stream unique fence를 무변경
preflight하고 실패하면 호스트 기동을 중단합니다.
V158 authority와 applied-version lineage가 projection-owned WorkScope의 일반 명령을 차단하고,
수신·claim·commit에서 recipe/program evidence와 scope version을 다시 대조합니다. 다만 기본 Cleaner profile은
아직 RMS recipe execution과 released program artifact를 해석하는 제품 coordinator 대신
`RejectingWorkScopeProjectionAuthorityValidator`를 사용합니다. 따라서 기본 조립의 worker-ON 스모크는 빈 queue에서
plugin/hosted-service/schema readiness가 연결됐다는 증거일 뿐, authority 발급 가능·실설비 HIL 승인·운영 활성화를
뜻하지 않습니다. 제품 profile에 신뢰된 validator를 조립하고 실제 recipe/program 불변 evidence와 교차-process
복구를 검증하기 전에는 worker를 켜지 않습니다.
활성 제품의 POM service manifest는 policy를 application runtime보다 먼저 선언해야 합니다.

```xml
<Service name="Pom"
  classPaths="./Modules/NexaOne.POM.dll;./Modules/NexaOne.Project.Cleaner.dll"
  configFiles="config/modules/pom.xml;config/projects/cleaner.xml;config/modules/pom-projection.xml" />
```

`Enabled=true`인데 `pom-projection.xml`이 빠졌거나 runtime marker가 중복되거나 marker와 hosted worker가
같은 객체가 아니면 호스트가 HTTP를 열기 전에 실패합니다. 반대로 기능이 OFF인 POM-only 제품은
`config/modules/pom.xml`만 사용하며 application runtime marker가 없어도 됩니다.

### 제품 패키징 profile

`eng/product-profiles/Core.props`가 공통 도메인 plugin을 한 번만 선언하고,
`eng/product-profiles/profiles/<Profile>.props`가 제품 전용 project DLL과 application manifest만 추가합니다.
Server와 ServerTests는 같은 `@(NexaOneProductPlugin)` item을 사용하므로 build 순서, `Modules/` copy/publish,
hash 비교와 exact file-set smoke에 별도 DLL 배열이 없습니다. 빌드는 선택 결과를
`config/product-profile.manifest`에 기록하며 profile을 바꿀 때 이전 제품 DLL과 plugin `.deps.json`을 제거합니다.

기본값은 기존 산출물과 동일한 `Cleaner`입니다. 프로젝트 정책이 없는 POM-only 산출물은 property 하나로
빌드·게시·부팅 검증할 수 있습니다.

```powershell
dotnet build src/00.Main/NexaOne.Server/NexaOne.Server.csproj -c Release `
  -p:NexaOneProductProfile=PomOnly

pwsh -NoProfile -File tools/ops/Test-Publish.ps1 -ProductProfile PomOnly
```

새 제품은 별도 `<Product>.props`와 Spring application manifest를 추가하고, profile에 제품 project의
경로와 assembly 이름을 한 번 선언합니다. `Server.csproj`, 테스트 코드, publish 스크립트의 DLL 목록은
수정하지 않습니다. profile의 manifest `classPaths`와 선언 plugin 집합이 다르면 정적 계약 및 실제 bundle
smoke가 실패합니다.

## 설비 운영 원칙

- PM/BM은 현재 수동 실행입니다. 보전 W/O의 시작·완료·부품 사용은 로그인 작업자와 시각을 남깁니다.
- 초기 예비부품 재고도 `Opening` 원장으로 생성하며, 조정·예약·소모는 master 수량과 원장을 한 transaction으로 처리합니다.
- Tool은 master, 장착·해제, 사용, 점검, 교정 이력을 분리합니다. 사용·해제 시각은 장착 시각보다 빠를 수 없습니다.
- Recipe는 승인 상태 전이와 parameter 변경을 경쟁 안전하게 처리하고, 실행 시 실제 version·parameter snapshot을 고정합니다.
- Utility 판독은 당시 meter 설정 version과 요율을 snapshot으로 보존합니다. 설정 경계를 넘는 집계는 기간을 나누도록 실패 처리합니다.
- OEE는 계획시간·상태·생산·불량의 근거 window와 실행자를 보존합니다. 서로 다른 수량 단위는 임의로 합산하지 않습니다.
- 설비별 sequence recovery, 알람 reset 조건, Carrier 처리 규칙은 제품 플러그인에서 공통 recovery·audit 계약을 사용합니다.

## DB 조회 성능 원칙

DB 성능은 테이블 수가 아니라 실제 조회 계약으로 관리합니다. Repository와 named query의 `WHERE`·`JOIN`·`ORDER BY`를 기준으로 복합/filtered index를 만들고, SQLite 회귀 테스트에서는 대표 쿼리의 `EXPLAIN QUERY PLAN`까지 확인합니다. 같은 left-prefix를 제공하는 PK·unique index가 있으면 중복 index를 추가하지 않습니다.

일반 View는 SQL 계약을 캡슐화하지만 결과를 저장하지 않으므로 그 자체로 빨라지지 않습니다. 반복되는 안정적 read model이 생겼을 때만 View를 추가하고, OEE·Takt·Utility처럼 계산 비용이 큰 화면은 summary table을 materialized projection으로 유지합니다. SQL Server indexed view는 쓰기 증폭과 SQLite 비동등성을 감수할 만큼 운영 부하가 측정된 뒤에만 도입합니다.

공통 Dapper 조회 경로는 cancellation과 timeout을 실제 DB 명령에 전달하며, NexaDB 진단 sink를 통해 duration·row count·outcome을 수집합니다. SQL 원문, parameter, LOT·설비·사용자 식별자와 DB 오류 메시지는 진단 이벤트에 기록하지 않습니다.

## 개발 시작

필수 환경은 .NET SDK `8.0.419`, Node.js 20, Git submodule 접근 권한입니다. private
서브모듈 인증값은 저장소 파일에 넣지 않고 Git Credential Manager/SSH 또는 `gh auth login`으로
로컬 자격 증명을 구성합니다.

```powershell
git clone --recurse-submodules https://github.com/imagineiluv/NexaOne.MES.git NexaMes
cd NexaMes
pwsh -File tools/Initialize-Submodules.ps1
dotnet restore NexaOne.sln
./tools/ci/Verify-NuGetVulnerabilities.ps1 -NoRestore
dotnet build NexaOne.sln --configuration Release --no-restore
dotnet test NexaOne.sln --configuration Release --no-build
```

개발 환경에서 인증 방식만 바꾸려면 추적하지 않는 `config/submodules.local.json`을
`config/submodules.local.example.json`에서 복사해 사용합니다. 이 파일에는 토큰을 기록하지
않습니다. `credentialSource`는 `Auto`(환경변수 → GitHub CLI → Git credential helper),
`GitHubCli`, `Environment`, `GitCredentialManager` 중 하나이며, 일회성 환경변수는 현재
PowerShell 프로세스에만 설정합니다. CI는 Checkout 전에 인증해야 하므로
`.github/workflows/ci.yml`의 `secrets.NEXA_SUBMODULE_TOKEN` 경계를 그대로 사용합니다.

Portal 검증:

```powershell
cd src/00.Main/NexaOne.Server/ClientApps/Portal
npm ci
npm test
npm run build
npm audit --audit-level=moderate
```

로컬 개발 진입점은 SQLite와 `server.sqlite.xml`을 함께 설정해 실제 Spring module graph와 Portal을 부팅합니다.

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-dev.ps1
```

SQLite는 개발·회귀 테스트용이며, 배포 전에는 GitHub의 `mssql-contract`과 같은 SQL Server migration/trigger 계약 테스트도 통과해야 합니다.

### 개발 단계 CI

private 서브모듈 Secret을 아직 등록하지 않은 동안에도 PR은 `development-check`로
action pin·migration catalog·Portal 테스트/빌드/audit·whitespace 검증을 수행합니다.
이 모드는 전체 .NET/PLC/MSSQL 검증을 대체하지 않으며, `NEXA_SUBMODULE_TOKEN`이 제공된
경우에만 기존 `build-test`와 `mssql-contract` 게이트가 실행됩니다. Secret을 추가하면
같은 PR에서 CI를 재실행해 전체 게이트를 확인해야 합니다.

## 변경 시 필수 확인

1. migration version은 중복시키지 않고 fresh DB와 기존 DB 증분 경로를 함께 테스트합니다.
2. 서비스 검증만 믿지 않고 저장소의 조건부 write와 DB constraint/trigger로 최종 무결성을 지킵니다.
3. 업무 명령에는 로그인 작업자, 멱등키, 필요 시 expected version을 포함합니다.
4. direct·transitive NuGet 취약점 게이트, `dotnet build --warnaserror`, 전체 Unit/Server/boot 테스트,
   Portal 테스트·빌드·audit를 실행합니다.
5. 실제 Controller/HIL이나 라이선스 증거가 없는 driver DLL은 공개 릴리즈로 표시하지 않습니다.

상세 용어와 bounded context 정의는 [CONTEXT.md](CONTEXT.md), 구조 결정은 [docs/architecture](docs/architecture)를 참고하십시오.

릴리즈 브랜치와 DLL/manifest 운영은 [영구 release 브랜치 정책](docs/RELEASE_BRANCH_POLICY.md)을
따릅니다. 버전별 브랜치를 만들지 않고 하나의 `release` 브랜치에 승인된 버전 디렉터리만
추가하며, 실 MSSQL·CI·게시 smoke 증거 전에는 산출물을 공개 릴리즈로 표시하지 않습니다.

승인된 버전의 DLL/ZIP/manifest 생성은 다음 명령으로 수행합니다. 스크립트는 기존 버전
디렉터리를 덮어쓰지 않고, `Test-Publish.ps1` 스모크와 Release publish를 다시 실행한 뒤
`release/<version>/dll`, `release/<version>/artifacts`, `release/<version>/release-manifest.json`을
생성합니다. 버전은 반드시 승인된 SemVer를 전달해야 합니다.

```powershell
pwsh -NoProfile -File tools/ops/Publish-ReleaseBundle.ps1 -Version 1.0.0
```

제품별 릴리즈는 같은 영구 release 브랜치 정책을 유지하면서 `-ProductProfile <Profile>`을 추가합니다.
생성되는 `release-manifest.json`에는 `packagingProfile`이 기록됩니다. 제품 프로필 도입 전에
생성되어 이 필드가 없는 기존 매니페스트는 검증 시 `Cleaner`로 해석하므로 과거 릴리즈의
해시·서브모듈 핀 검증도 계속 재현할 수 있습니다. 새 매니페스트의 누락 또는 잘못된 프로필을
허용한다는 뜻은 아니며, 현재 게시 스크립트는 항상 선택한 프로필을 기록합니다. 이 필드가 있는
현재 형식은 ZIP 내부 `config/product-profile.manifest`의 프로필, `Modules/` 전체 파일 집합,
`config/app.xml`의 플러그인 `classPaths`가 모두 정확히 일치해야 독립 검증을 통과합니다.

생성된 manifest의 source commit·submodule pin·각 DLL/ZIP SHA-256을 검토한 뒤에만
release 브랜치에 커밋하고 annotated tag와 GitHub Release를 발행합니다.

반영 전후의 산출물 무결성은 다음 명령으로 독립 검증할 수 있습니다.

```powershell
pwsh -NoProfile -File tools/ops/Verify-ReleaseBundle.ps1 -Version 1.0.0
```
