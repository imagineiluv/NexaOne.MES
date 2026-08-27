# NexaOne 배포·운영 런북

작성 2026-07-09, 저장소 미러 2026-07-10(클라이언트 진입 경로 반영) — 원본은 옵시디언 볼트(`NexaMes/docs/design/Deployment-Operations.md`)이며
저장소만 받는 운영자/CI를 위해 여기 미러한다. **갱신 시 볼트와 함께 수정할 것**(비밀값 없음 확인 후 커밋).
게시 파이프라인은 실검증됨: publish 산출물 단독 부팅 → 9모듈 로드·/health Healthy·로그인 JWT 발급 확인.

## 1. 게시(Publish)

```bash
dotnet publish src/00.Main/NexaOne.Server/NexaOne.Server.csproj -c Release -o <배포폴더>
```

- **Portal 클라이언트** 소스는 `src/00.Main/NexaOne.Server/ClientApps/Portal/`이며, `BuildPortalClientBundle` Target이 자동 빌드해 `wwwroot/spa/`로 포함한다(Node.js 필요).
- `ClientApps/`의 소스·`node_modules`·로컬 빌드 산출물은 게시물에서 제외한다.
- **모듈 플러그인 9종**은 `Modules/`로 포함된다(`CopyDomainModulePluginsOnPublish` Target).
- 산출물 필수 구성 확인: `Modules/*.dll 9개`, `wwwroot/spa/`, `wwwroot/fonts/PretendardVariable.woff2`,
  `wwwroot/css/nexaone.css`, `config/`(app.xml·host/·modules/), `db/`(migrations·queries 양 방언).
- **자동 검증**: `tools/ops/Test-Publish.ps1` — 게시→산출물 구성 검사→단독 부팅→/health→로그인까지 무인 검증.

> ⚠ 2026-07-09 배포 검증에서 잡은 실버그 2건(재발 주의):
> ① EMS 모듈 ProjectReference 누락(CMMS→EMS 리네임 잔재) — Debug는 테스트 체인이 우연히 빌드해 은폐, Release publish에서만 노출.
> ② 모듈 Copy 타깃이 Build 훅뿐이라 publish 산출물에 Modules/ 누락 — Publish 훅 타깃 추가로 해소.

### CI 비공개 서브모듈 인증

GitHub Actions의 `GITHUB_TOKEN`은 현재 저장소 범위만 읽을 수 있으므로, 비공개
`NexaFramework`·`NexaDB`·`NexaLogic` 서브모듈 checkout에는 저장소 secret
`NEXA_SUBMODULE_TOKEN`이 필요하다. 토큰은 세 upstream 저장소의 **Contents: Read-only**만 허용한
fine-grained PAT 또는 동등한 GitHub App 설치 토큰을 사용하고, 비밀값을 XML·설정 파일·로그에 기록하지 않는다.
CI는 secret이 없으면 checkout 전에 명시적으로 실패하며, 토큰이 준비된 뒤 실패 run을 재실행한다.

## 2. 구성(Configuration)

`config/appsettings.Production.sample.json`(저장소)을 `appsettings.Production.json`으로 복사해 조정한다.
**비밀값은 파일 금지 — 환경변수 전용**(저장소·산출물에 평문 두지 않는 표준 제약):

| 환경변수 | 값 | 비고 |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` | dev 시드(관리자 기본계정·데모 행)는 Development 전용 |
| `ConnectionStrings__NexaOne` | MSSQL 연결문자열 | **env 전용** |
| `Jwt__SecretKey` | 32바이트+ 랜덤 | **env 전용** |
| `Server__SpringConfig` | `config/host/server.xml`(MSSQL) / `config/host/server.sqlite.xml` | 방언 전환 |
| `Database__Provider` | `MsSql` / `Sqlite` | SpringConfig와 짝 |
| `Email__Smtp__User/Password` | SMTP 자격 | 메일 기능 사용 시, **env 전용** |
| `Worker__Fdc__VirtualEvent__Enabled` | `true` | FDC 가상이벤트 워커(Spring 상수라 env만 유효) |
| `ApiBaseUrl` | 예: `http://localhost:8080/` | **8080 외 포트/프록시 뒤 기동 시 필수** — 호스트 자기호출 기준 |

워커/게이트 기본값(샘플 파일이 프로덕션 권장값으로 켜 둠): RateLimiting·RequestLogging·AppLogging(Db)·
RefreshTokenCleanup·BatchProcess·Outbox(Dispatch+Events)·OEE Aggregation.

**기본 관리자 하드닝(자동)**: Production 기동 시 admin이 V001 기본 해시 그대로면 `PASSWORD_STATE='Create'`로
강제 전이 — 첫 로그인 시 비밀번호 변경이 강제된다(`DefaultAdminHardening`).

## 3. 기동·확인

저장소에서 통합 Server(SQLite, `http://localhost:5173`)와 Portal Vite HMR(`http://localhost:5174`)을 함께 실행:

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-dev.ps1
```

사용자 진입점은 MES `/`, 디자이너 `/Designer`, 모바일 `/Mobile`, 키오스크·POP `/POP`이다. Portal 구조는 원본 Wiki의 `docs/design/Portal-Client-Structure.md`, DB 채널 규칙은 `docs/design/Client-Entry-Routes.md`를 따른다.

게시 산출물은 다음과 같이 단독 기동한다.

```bash
cd <배포폴더>
dotnet NexaOne.Server.dll --urls http://localhost:8080
```

기동 확인 체크리스트:
1. 부팅 로그에 `Service 'Mdm|Est|Fdc|Rms|Qms|Ems|Pom|Shp|Sys' registered` **9건**.
2. `GET /health` → `Healthy`.
3. `/login` 렌더 → 관리자 로그인 → 사이드바 메뉴 트리 렌더.
4. `/Designer` 디자이너 로그인·화면 목록 렌더.
5. `/Mobile`, `/POP` 로그인 후 채널별 DB 화면 목록 렌더.
6. (모듈 검증) 임의 마스터 화면(`/meta/FACTORY_MDM_PLANT`) 그리드 행 렌더.

## 4. 데이터베이스

- **스키마**: SQLite는 부팅 시 자동(`SqliteSchemaInitializer` — 빈 DB 전체/기존 DB 누락 테이블 증분;
  ⚠ ALTER·UPDATE형 마이그레이션은 증분 경로 미적용 — dev DB는 재생성으로 반영).
- `V089__SYS_SCREEN_TARGET.sql`은 신규 테이블 방식이므로 기존 SQLite DB에도 증분 적용된다. 화면의 `TARGET_CHANNEL`과 `ENTRY_PATH`를 저장한다.
- **MSSQL 적용 러너**: `tools/ops/Apply-MssqlMigrations.ps1 -ConnectionString $env:... [-DryRun] [-IncludeOpsSeed]`
  — `SYS_SCHEMA_MIGRATION` 버전 추적, 파일당 단일 트랜잭션, 멱등. 접속 문자열은 env/보안 저장소 전용.
- **운영 초기 데이터(ops 시드 팩)**: `ops/sql/sys-menu-seed.mssql.sql`(메뉴 320행 — 빈 테이블 가드,
  원본은 `config/Seed/nexaone-menu.json` + 생성기 `tools/ops/Generate-MenuSeedSql.ps1`) ·
  `ops/sql/sys-batch-seed.mssql.sql`(로그 보존 정리 배치 2종 — 행 단위 멱등). 러너 `-IncludeOpsSeed`로 일괄 적용.
- **MSSQL 사전 검증**: 노드 접근 가능 시 `NEXAONE_MSSQL_TEST_CONN` 설정 후 `MssqlDialectSyntaxTests` 실행
  (전 mssql 쿼리 SET PARSEONLY) — 테스트 노드 온라인 시 수행할 미결 항목.
- **백업**: SQLite=DB 파일 복사(정지 후), MSSQL=표준 백업 절차.

## 5. 롤백·업그레이드

- 산출물 폴더 단위 교체(블루/그린식 폴더 스왑) — 설정은 env라 산출물 교체와 독립.
- DB 마이그레이션은 additive 관례(V*.sql) — 롤백 시 앱만 이전 산출물로 되돌리면 신 테이블은 무해하게 잔존.
- SPA는 해시 청크 — 재배포 후 열린 세션의 구 청크 404는 1회 자동 새로고침으로 복구된다(vite:preloadError).

## 6. 미결(환경 확정 후)

- 호스팅 방식: Windows 서비스(sc/NSSM) vs IIS(ANCM) vs 컨테이너 — 대상 서버 확정 필요.
- 리버스 프록시/TLS 종단, 도메인, 방화벽.
- MSSQL 운영 인스턴스 연결·마이그레이션 적용 리허설(§4, env-gate 테스트 포함).
- 로그 수집(현재 콘솔+DB 앱로그) 외부 싱크 여부.

관련: 설계문서 §20.13/§20.13.1 · `config/appsettings.Production.sample.json` · `tools/ops/` · `ops/sql/`
