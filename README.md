# NexaMES

NexaMES는 제조 실행과 설비 운영을 하나의 서버에서 조립하는 모듈형 MES입니다. 생산·품질·설비·레시피 같은 공통 업무 규칙은 재사용 가능한 서비스로 두고, 설비 시퀀스·고객사 연동·현장별 LOT 의미처럼 프로젝트마다 달라지는 규칙은 플러그인 경계에 둡니다.

현재 공통 서비스는 이 저장소에서 먼저 검증합니다. API·데이터 무결성·SQLite와 SQL Server 계약이 안정된 뒤에만 NexaFramework로 이관합니다.

## 구성

| 영역 | 책임 |
|---|---|
| `NexaOne.Server` | HTTP API, 인증, Spring.NET 조립 루트, 모듈 로딩, SQLite/MSSQL 부팅 |
| `NexaOne.Common` | 모듈 간 서비스 계약, 감사·멱등성·영속성 공통 기반 |
| `MDM` | Plant, 설비, 품목, 작업조, 캐리어 등 기준정보 |
| `POM` | 생산 W/O, 공정 LOT, 공정 이력과 처분 |
| `IVT` | 자재 LOT, 투입·소모 원장과 TRACE projection |
| `QMS` | 검사, 불량, 폐기와 품질 이력 |
| `EMS` | PM/BM, 보전 W/O, 예비부품, BOM·공급처, Tool 점검·교정 |
| `EST` | 설비 상태, 출력, Utility, OEE와 설비 실행 증거 |
| `RMS` | Recipe 승인, 버전, Parameter, 설비 할당과 실행 snapshot |
| `FDC` | 설비 통신 endpoint와 driver adapter, 수집 계약 |

세 개의 재사용 저장소는 `submodules/` 아래에서 고정 commit으로 참조합니다.

- `NexaFramework`: 공통 실행·확장·Spring.NET hosting 계약
- `NexaDB`: DB access, messaging와 알림 기반
- `NexaLogic`: PLC protocol driver와 선택형 hosting helper

Driver 구현은 업무 서비스나 Component에서 다시 만들지 않습니다. 제품은 driver 계약을 직접 참조하거나 조립 루트에서 주입하고, `Hosting`은 lifecycle을 편하게 묶을 때만 선택합니다.

## 공통 서비스와 플러그인 경계

공통 서비스가 소유하는 것은 여러 설비와 MES에서 의미가 같은 규칙입니다. 예를 들면 작업자 감사 이력, optimistic concurrency, 멱등 처리, PM/BM 계획과 W/O 일치, 재고 원장, Tool 장착·사용 시간축, Recipe 승인 이력, Utility 계량 경계와 OEE 계산입니다.

플러그인이 소유하는 것은 프로젝트별 정책입니다. 예를 들면 캐리어 세척 시퀀스, 고객사별 알람 해제 조건, MES payload mapping, 자재 소모 방식, LOT/Carrier 변환, 설비별 parameter 해석입니다. 플러그인은 공통 계약을 사용하지만 공통 모듈의 DB 내부 구현을 직접 참조하지 않습니다.

Spring XML은 런타임 조립에 사용합니다. 모듈 구현을 상속해 제품 로직을 끼워 넣지 않고, 안정된 interface를 구현한 adapter 또는 plugin을 `config/host/*.xml`과 `config/modules/*.xml`에서 연결합니다. 단순하고 고정된 코드 경로는 생성자 주입으로 직접 참조하고, 교체 가능성이 있는 프로젝트 정책만 XML seam을 사용합니다.

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
4. `dotnet build --warnaserror`, 전체 Unit/Server/boot 테스트, Portal 테스트·빌드·audit를 실행합니다.
5. 실제 Controller/HIL이나 라이선스 증거가 없는 driver DLL은 공개 릴리즈로 표시하지 않습니다.

상세 용어와 bounded context 정의는 [CONTEXT.md](CONTEXT.md), 구조 결정은 [docs/architecture](docs/architecture)를 참고하십시오.

릴리즈 브랜치와 DLL/manifest 운영은 [영구 release 브랜치 정책](docs/RELEASE_BRANCH_POLICY.md)을
따릅니다. 버전별 브랜치를 만들지 않고 하나의 `release` 브랜치에 승인된 버전 디렉터리만
추가하며, 실 MSSQL·CI·게시 smoke 증거 전에는 산출물을 공개 릴리즈로 표시하지 않습니다.
