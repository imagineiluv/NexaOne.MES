# NexaOne 배포·운영 런북

작성 2026-07-09, 저장소 미러 2026-07-10(클라이언트 진입 경로 반영) — 원본은 옵시디언 볼트(`NexaMes/docs/design/Deployment-Operations.md`)이며
저장소만 받는 운영자/CI를 위해 여기 미러한다. **갱신 시 볼트와 함께 수정할 것**(비밀값 없음 확인 후 커밋).
게시 파이프라인은 실검증됨: publish 산출물 단독 부팅 → 11모듈 로드·/health Healthy·로그인 JWT 발급 확인.

## 1. 게시(Publish)

```bash
dotnet publish src/00.Main/NexaOne.Server/NexaOne.Server.csproj -c Release -o <배포폴더>
```

- **Portal 클라이언트** 소스는 `src/00.Main/NexaOne.Server/ClientApps/Portal/`이며, `BuildPortalClientBundle` Target이 자동 빌드해 `wwwroot/spa/`로 포함한다(Node.js 필요).
- `ClientApps/`의 소스·`node_modules`·로컬 빌드 산출물은 게시물에서 제외한다.
- **모듈 플러그인 11종**은 `Modules/`로 포함된다(`CopyDomainModulePluginsOnPublish` Target).
- 산출물 필수 구성 확인: `Modules/*.dll 11개`, `wwwroot/spa/`, `wwwroot/fonts/PretendardVariable.woff2`,
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
| `Email__Smtp__Enabled/Host/Port/Sender` | SMTP 활성/발송 정보 | Enabled=true이면 Host·Sender·1~65535 Port를 모두 명시; 누락·잘못된 값은 기동 실패 |
| `Email__Smtp__User/Password` | SMTP 자격 | 메일 기능 사용 시, **env 전용** |
| `Worker__Fdc__Enabled` | `false` | PLC/STO action adapter·ack/readback HIL 완료 전에는 반드시 OFF |
| `Worker__Fdc__InterlockActionTimeoutSeconds` | `10` | action readiness·apply·reconcile·release 최대 caller 대기; 0 이하는 기동 거부 |
| `Worker__Fdc__RuntimeHealth__FreshnessTimeoutSeconds` | `30` | 마지막 완료 PLC poll 허용 경과; 초과·listener 종료 시 permit 철회, 0 이하는 기동 거부 |
| `Worker__Fdc__DriverCleanupTimeoutSeconds` | `10` | driver별 Stop/Dispose 단계의 독립 최대 대기; timeout 뒤에도 다음 단계·다음 driver 정리를 계속 |
| `RunAdmission__Enabled` | `false` | durable shared request ledger, client/equipment별 quota, HA routing 계약 전에는 false 고정. 현재 true는 기동 거부 |
| `RunAdmission__RequireHttps` | `true` | `api/v1/run-admission/*`의 HTTPS 필수 gate. Production에서 끄지 않음 |
| `RunAdmission__RateLimit__PermitLimitPerMinute` | `3000` | acquire/keep-alive/release 전용 global rate limit |
| `RunAdmission__Clients__<key>__ClientId` | canonical client ID | 구성 key와 값의 대소문자까지 일치해야 함 |
| `RunAdmission__Clients__<key>__EquipmentIds__0` | canonical equipment ID | client별 설비 allowlist. 추가 설비는 배열 index 증가 |
| `RunAdmission__Clients__<key>__SecretSha256` | 64자리 SHA-256 hex | **env 전용**. 원문 client secret은 MES 설정·로그에 저장하지 않음 |
| `ReverseProxy__KnownProxies__0` | 고정 TLS proxy IP | loopback 외 TLS edge를 쓸 때 해당 IP만 추가. 임의 forwarded header 신뢰 금지 |
| `Worker__Fdc__RunAdmission__KeepAliveLeaseSeconds` | `6` | 서버 soft TTL. Cleaner는 2초 keep-alive/3초 응답 timeout으로 더 빨리 Stop 판단 |
| `Worker__Fdc__RunAdmission__HardLeaseSeconds` | `43200` | keep-alive로 연장되지 않는 12시간 절대 상한 |
| `Worker__Fdc__RunAdmission__TombstoneRetentionSeconds` | `86400` | 종료 lease/request 재사용을 막는 terminal record 보존시간 |
| `Worker__Fdc__RunAdmission__MaxTombstones` | `100000` | 동시 live lease와 보존 terminal record를 합친 메모리 원장 예약 상한 |
| `Worker__Fdc__Retention__Enabled` | `false` | 수집 원장 보존 정리; 주기/보존일은 `IntervalSeconds`/`RetentionDays` |
| `Worker__Fdc__Retention__BindingChangesQuiesced` | `false` | Enabled 시 필수. 프로세스 전체 실행기간 IVT binding 보호 시작점을 낮추는 변경 동결 서약 |
| `Worker__Ivt__TraceMaterialConsumption__Enabled` | `false` | 영속 FDC TRACE→자재 소비 projection. V150 gap과 feed-session PendingDrain Finalize blocker를 모두 닫기 전에는 활성화 금지 |
| `Ivt__TraceConfiguration__MaintenanceMode` | `false` | 향후 TRACE binding 변경 시 추가로 요구할 점검창. 단독으로 mutation을 열지 않음 |
| `Ivt__TraceConfiguration__BindingsEnabled` | `false` | durable cross-process binding/collection/retention/projection fence 전에는 false 고정. 현재 true는 기동 거부 |
| `Ivt__TraceConfiguration__FeedSessionsEnabled` | `false` | Mount/Unmount mutation gate. durable drain Finalize와 HIL 전에는 false 고정; API는 409 `IVT.FeedSession.FeatureDisabled` |
| `Worker__Fdc__VirtualEvent__Enabled` | `false` | FDC 가상이벤트 평가; 규칙 검증 후 명시 활성화, `IntervalSeconds`로 주기 조정 |
| `Worker__Fdc__Topic` | `nexaone.events` | 생략 시 `Events__Outbox__Topic`, 이후 기본 토픽 순으로 fallback |
| `Worker__Ems__MaintenanceDue__Enabled` | `false` | 예방정비 도래 이벤트 발행; 구독자·토픽 검증 후 활성화 |
| `Worker__Ems__MaintenanceDue__IntervalSeconds` | `3600` | 최소 60초로 제한 |
| `Worker__Ems__MaintenanceDue__Topic` | `nexaone.events` | 생략 시 `Events__Outbox__Topic`, 이후 기본 토픽 순으로 fallback |
| `Worker__Sys__LoginFailureRetention__Enabled` | `false` | 로그인 실패 이력 삭제; 보존·감사 정책과 백업 검증 후 활성화 |
| `Worker__Sys__LoginFailureRetention__IntervalSeconds` | `86400` | 최소 60초로 제한 |
| `Worker__Sys__LoginFailureRetention__RetentionDays` | `90` | 최소 1일로 제한 |
| `ApiBaseUrl` | 예: `http://localhost:8080/` | **8080 외 포트/프록시 뒤 기동 시 필수** — 호스트 자기호출 기준 |

FDC 수집 활성화 전에는 다음을 모두 확인한다.

- 활성 parameter마다 `FDC_PARAMETER.ENDPOINT_ID`가 정확히 한 활성 endpoint를 가리키고, endpoint별 `TAG_MAP_PATH`가 존재해야 한다. 상대경로는 서비스의 현재 디렉터리가 아니라 `AppContext.BaseDirectory` 기준이다. 외부 절대경로는 허용하지만 tag map에 자격증명·비밀값을 넣지 않는다.
- 현재 FDC의 원자적 구독+baseline 지원은 `ModbusTcp`, `SiemensS7`, `MitsubishiMc`, `EtherNetIp`만 해당한다. OPC UA·Modbus RTU·Omron FINS는 `Worker__Fdc__Enabled=true` 운영 대상으로 승인하지 않는다.
- 프로젝트 `IFdcInterlockActionPort`의 모든 opaque action key가 ack/readback과 cancellation/deadline fencing까지 확인되고, 전체 unresolved effect inventory에 삭제된 설비/파라미터가 없어야 한다. EffectId별 durable command journal과 controller readback을 유지하며 V146의 Prepared→Applied→ConditionNormalized→ReleasePending→Resolved 재조정에 응답해야 한다. 여러 EffectId가 같은 STOP/STO 출력을 공유하면 출력별 활성 EffectId 집합을 영속 관리하고 마지막 소유자만 deassert해야 한다. DB에 없고 adapter journal에만 남은 effect도 완전한 원 trigger 증거와 같은 EffectId로 반환해야 하며, readiness가 aggregate ownership을 확인하지 않으면 기동을 거부한다.
- 활성 effect·ReleasePending·terminal DB CAS 대기 중에는 자동운전 permit만 닫고 FDC PLC 감시와 supervisor는 유지한다. 수동 reset 후 값 변화가 없어도 모든 callback이 끝난 최신 completed-poll snapshot을 재평가하되, 물리 Release 전에 poll 전체의 interlock 입력 품질이 Good인지 먼저 검사한다. 이후 DB 영속화 대기 뒤와 adapter 호출 직전에 `StartedPollCount`/generation/current snapshot과 전체 활성 endpoint의 running/freshness를 다시 확인한다. 뒤쪽 Bad 입력, 대상/다른 endpoint freshness 초과 또는 다음 read 시작이 하나라도 관찰되면 어떤 Release도 호출하지 않는다. DB persistence supervisor는 cached 값으로 물리 Release하지 않는다. 재위반은 pending release를 취소하고 STOP을 먼저 reconcile한다. 활성 effect와 terminal pending이 모두 0이 된 뒤에만 permit이 다시 열린다. Bad 품질·apply/reconcile 미확인·release cancellation/timeout은 unknown physical outcome의 terminal runtime fault로 처리해 원인 예외를 보존하고 driver를 닫은 뒤 전체 재기동 reconciliation을 요구한다.
- caller 대기는 `InterlockActionTimeoutSeconds`로 제한되지만 cancellation을 무시한 adapter의 실제 장치 명령은 백그라운드에서 늦게 끝날 수 있다. 특히 timeout 뒤 Release가 물리 해제를 완료하지 않도록 adapter/controller 자체 deadline 또는 fencing을 HIL로 증명해야 하며, readiness가 이를 확인하지 않으면 기동을 거부한다. 각 PLC 구독은 listener completion/fault와 monotonic 진행을 제공하고, FDC용 단일 atomic stream은 별도 선택적 capability로 immutable latest completed-poll snapshot과 callback/read 시작 fence를 제공해야 한다. 일반·다중 subscription에는 completed snapshot을 게시하지 않으며 callback을 뒤로 미루는 jitter/coalescing window는 이 atomic 모드와 함께 쓰지 않는다. callback 예외는 해당 poll을 완료 처리하지 않고 listener fault로 전파한다. endpoint별 정상 poll+read timeout+최대 reconnect backoff보다 작은 freshness는 구성 오류로 기동 거부하고, 그 예산을 넘긴 frozen stream은 permit을 철회한다.
- FDC worker는 설비 `PlantController`나 Auto 모드를 시작하지 않는다. Cleaner의 acquire/keep-alive/Stop 연결 코드는 준비됐지만 현재 `RunAdmission__Enabled=false`가 고정값이고 HTTP는 503, Spring bridge 직접 호출은 `RUN_ADMISSION_FEATURE_DISABLED`로 거부한다. 따라서 이 경로를 근거로 Auto Start/Resume을 승인하지 않는다. 향후 활성화 후에도 401/403/426/503, 연결 실패·timeout, `IsCurrent=false`, authority generation/fence 변경, 인터락·서버 재시작·hard expiry는 모두 token 폐기와 Stop 사유이며 정상 정지에서만 `release`를 best-effort로 호출한다.
- admission endpoint에는 `X-Nexa-Run-Client-Secret` 원문을 HTTPS로 보내되 MES는 client별 SHA-256 digest와 canonical equipment allowlist만 보관한다. Kestrel HTTPS listener를 직접 열거나 고정 TLS edge의 IP만 `ReverseProxy:KnownProxies`에 등록하고 backend HTTP listener를 외부에 노출하지 않는다. forwarded header를 임의 client에서 신뢰하거나 secret/token을 request logging·감사 payload에 남기지 않는다. allowlist/secret 권한 회수 뒤 다음 인증 실패도 Cleaner Stop으로 처리한다.
- RunAdmission의 process-local request/tombstone 원장은 재시작·failover 후 동일 요청 재발급을 막지 못하고 전역 100,000개 상한도 한 client가 소진할 수 있다. DB 등 durable shared ledger, client/equipment별 quota, 다중 인스턴스 owner/sticky routing과 장애전환 계약을 구현한 뒤 실제 PLC/STO wiring/readback, 네트워크 분리·3초 timeout, authority/config/safety epoch 변경, stale token/fence, 권한 회수와 Stop 직렬화 HIL을 모두 통과하기 전에는 `RunAdmission__Enabled`를 true로 바꾸지 않는다.
- permit 철회와 FDC driver close는 safety PLC/STO의 물리 de-energize를 대체하지 않는다. driver health/fatal fault 전달, wiring/readback, 실제 PLC/STO HIL이 끝날 때까지 worker 기본값을 OFF로 유지한다.

TRACE binding Create/Retire API와 서비스 계약은 구현돼 있지만 현재 지원되는 mutation 절차는 없다.
`Ivt__TraceConfiguration__BindingsEnabled=false`에서는 HTTP 409 `IVT.TraceBinding.FeatureDisabled`이며 Spring
bridge 직접 호출도 동일하게 거부한다. true는 모듈 기동 자체를 실패시키므로 `MaintenanceMode=true`만으로 우회할
수 없다. binding mutation, FDC collection, retention, IVT projection이 같은 DB revision/advisory lock을 공유해
변경 중 purge·ingestion 진입을 배제하고 crash 후 상태를 복구하는 durable cross-process fence가 구현된 뒤에만
위 운영 절차를 다시 정의한다. 직접 SQL 변경은 V150 경계·감사·멱등성·CAS를 우회하므로 금지한다.

V150 이전 데이터에서 active binding의 `max(EFFECTIVE_FROM, cursor)`가 completeness boundary보다 과거이면 현재
전체-scope ingestion은 정상 scope까지 중단된다. 이 상태에서는
`Worker__Ivt__TraceMaterialConsumption__Enabled=false`를 유지한다. boundary 후퇴, pre-boundary raw INSERT 또는
직접 SQL range 변경은 지원되는 복구가 아니다. 전체 worker를 켜기 전에 ADR로 (A) strict/manual data repair,
(B) reason+evidence+전용 권한+ledger/CAS를 갖춘 audited Abandon/Rebase, (C) scope별 durable gap health와 healthy
scope 격리 중 하나를 선택하고 복원본·원장 reconciliation로 검증해야 한다. 현재 API에는 Abandon/Rebase가 없으며
이 결정은 Production release blocker다.

자재 장착·탈착은 `POST /api/v1/ivt/trace-material/feed-sessions/events`를 사용한다. 장착은 InStock 양수
LOT만 허용되고 한 투입점에는 active 또는 과거 장착 interval과 겹치는 세션을 만들 수 없다. LOT의
Move/Hold/Scrap/Adjustment와 동일 LOT 재장착은 reservation 동안 거부되고, Mount/Unmount 시각은 미래일 수 없으며
Unmount 사유가 필수다. Unmount는 물리 interval만 닫고 `ACTIVE_FEED_SESSION_ID`를 유지하는 fail-closed
`PendingDrain` 단계다. cutoff 이전 raw TRACE는 Unmount 뒤 늦게 도착해도 원래 LOT에 투영될 수 있다.

현재 FDC에는 commit/ingest upper watermark가 없으므로 inbox 0건 또는 현재 cursor만으로 지연 raw TRACE 부재를
증명할 수 없다. 이 때문에 reservation 해제 `Finalize`와 온라인 `Cancel`은 제공하지 않으며, 수동 SQL로
`ACTIVE_FEED_SESSION_ID`를 비우는 것도 금지한다. durable FDC watermark, 해당 feed point의 binding별 cursor,
cutoff 이하 inbox terminal을 함께 검증하는 Finalize 프로토콜과 실제 수집 HIL이 끝날 때까지
`Worker__Ivt__TraceMaterialConsumption__Enabled=false`와
`Ivt__TraceConfiguration__FeedSessionsEnabled=false`를 유지한다. 이 상태의 LOT는 장기 고정될 수 있으며 이는
Production release blocker다. 잘못된 장착도 사유를 남겨 Unmount하고 이미 발생한 소비 오귀속은 명시적
reversal/correction으로 정정한다. 직접 `IVT_TRACE_CONSUMPTION_BINDING` 또는 `IVT_MATERIAL_FEED_SESSION`을
수정하면 JWT actor, 멱등 원장, V150 gap, CAS 검증을 우회하므로 금지한다.

워커/게이트 기본값(샘플 파일이 프로덕션 권장값으로 켜 둠): RateLimiting·RequestLogging·AppLogging(Db)·
RefreshTokenCleanup·BatchProcess·Outbox(Dispatch+Events)·OEE Aggregation. EMS 예방정비 도래 발행과 SYS 로그인 실패
이력 삭제는 데이터 발행·삭제 정책이므로 샘플에서도 기본 OFF이며, 각각의 운영 검증 후 명시적으로 활성화한다.

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
1. 부팅 로그에 `Service 'Mdm|Est|Fdc|Rms|Qms|Ems|Ivt|Pom|Prc|Shp|Sys' registered` **11건**.
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
  — `SYS_SCHEMA_MIGRATION`이 파일명과 LF 정규화 SHA-256을 추적하며, 이미 적용된 파일의 개명·내용 변경은
  즉시 실패한다. DB에만 존재하는 미래 version이나 중간 누락 뒤의 later-applied version도 DDL·ops seed 전에
  거부해 downlevel 앱 연결과 out-of-order replay를 막는다. 파일당 적용과 이력 기록은 한 트랜잭션이다.
  `-DryRun`은 advisory lock과 metadata/history 조회만 하며 이력 table/column을 생성·변경하지 않는다.
  접속 문자열은 env/보안 저장소 전용.
- **기존 체크섬 없는 DB의 1회 전환**: 먼저 전체 백업을 만들고, 배포 DB의 마이그레이션 목록과 승인된
  release 소스가 일치하는지 DBA/릴리즈 담당자가 검토한다. staging 복원본에 동일 러너를 실행해 schema 계약과
  애플리케이션 회귀를 통과한 뒤에만 운영에서 `-AdoptMissingChecksums`를 한 번 사용한다. 이 옵션은 과거에 실제
  실행된 SQL을 역증명하지 않고 현재 승인 소스를 기준선으로 신뢰하므로 자동 CI나 일반 기동에 넣지 않는다.
- **대용량 업그레이드**: V130~V144의 신규/교체 index와 V142/V146/V147/V148/V151의 backfill·상태/전이 제약·FDC retention/TRACE material index는 hot table의 쓰기 증폭·build lock·transaction log를
  유발할 수 있다. 특히 V142 TRACE cursor 전환은 백필용 정렬 index로 full sort를 줄여도 terminal inbox 전행 갱신,
  index build/drop과 cursor backfill이 남고, V144는 `POM_LOT_HISTORY`의 TrackOut filtered/covering index를 build한다.
  운영과 유사한 행 수의 복원본에서 table/index 크기, log 여유, blocking, 300초 파일 transaction timeout과 timeout 뒤
  rollback 시간을 측정한다. 전환 중 TRACE/POM 구버전 writer를 중지하고 maintenance window·abort/rollback 기준을
  승인해야 한다. V150은 seed와 직접 DELETE guard가 한 transaction으로 commit될 때까지
  `FDC_COLLECT_DATA WITH (TABLOCKX, HOLDLOCK)`을 유지한다. 따라서 구/신 FDC collection·retention writer를 모두
  중지하고, V148 index를 사용한 `MIN(COLLECTED_AT)` 계획·lock 대기·300초 command timeout·rollback 소요를
  복원본에서 재현해야 한다. SQL Server edition별 ONLINE/RESUMABLE 지원 여부와 별도 online build 절차를 DBA가 확정하기 전에는
  운영에 적용하지 않는다. V142/V144, FDC history를 확장하는 V146, TRACE work-state를 검증하는 V147,
  effect transition/append-only와 TRACE retention index를 추가하는 V148, completeness cutover lock을 취하는 V150,
  기존 binding/feed-session/consumption 테이블과 index를 교체하는 V151이 pending이면
  러너가 기본 실패한다. 위 준비를
  실제로 완료한 승인 실행에서만 `-ApproveHighImpactMigrations`를 지정한다. 이 스위치는 준비 상태를 자동 증명하지
  않으며 CI의 빈 임시 DB에서는 계약 검증 목적으로만 명시한다.
- **V151 정확 사전검증**: 운영 백업의 복원본에서 아래 조회를 먼저 실행한다. 결과 CSV, table/index 크기,
  transaction log 여유, 예상 lock, 300초 timeout과 rollback 소요를 변경 승인에 첨부한다.

  ```sql
  SELECT EQUIPMENT_ID, PARAMETER_ID, COUNT_BIG(*) AS ACTIVE_COUNT
    FROM IVT_TRACE_CONSUMPTION_BINDING
   WHERE IS_ACTIVE = 1
   GROUP BY EQUIPMENT_ID, PARAMETER_ID
  HAVING COUNT_BIG(*) > 1;

  SELECT MATERIAL_LOT_ID, COUNT_BIG(*) AS ACTIVE_COUNT
    FROM IVT_MATERIAL_FEED_SESSION
   WHERE STATUS = 'Mounted' AND UNMOUNTED_AT IS NULL
   GROUP BY MATERIAL_LOT_ID
  HAVING COUNT_BIG(*) > 1;

  SELECT COUNT_BIG(*) AS LEGACY_UNMOUNTED_SESSION_COUNT
    FROM IVT_MATERIAL_FEED_SESSION
   WHERE STATUS = 'Unmounted' OR UNMOUNTED_AT IS NOT NULL;

  SELECT COUNT_BIG(*) AS CONSUMPTION_TOTAL,
         SUM(CASE WHEN H.CONSUMPTION_MODE = 'Trace'
                       AND H.SOURCE_SYSTEM = 'FDC'
                       AND H.CORRELATION_ID IS NOT NULL
                       AND EXISTS (
                           SELECT 1 FROM IVT_MATERIAL_FEED_SESSION S
                            WHERE S.FEED_SESSION_ID = H.CORRELATION_ID)
                  THEN CONVERT(BIGINT, 1) ELSE CONVERT(BIGINT, 0) END) AS LEGACY_CORRELATION_PROVENANCE_ROWS
    FROM IVT_MATERIAL_CONSUMPTION_HISTORY H;

  SELECT COUNT_BIG(*) AS INBOX_ROWS FROM IVT_TRACE_PROJECTION_INBOX;
  SELECT SUM(row_count) AS INBOX_PARTITION_ROWS,
         SUM(reserved_page_count) AS RESERVED_PAGES,
         SUM(used_page_count) AS USED_PAGES
    FROM sys.dm_db_partition_stats
   WHERE object_id = OBJECT_ID(N'dbo.IVT_TRACE_PROJECTION_INBOX')
     AND index_id IN (0, 1);
  ```

  첫 두 중복 조회가 한 행이라도 반환하거나 `LEGACY_UNMOUNTED_SESSION_COUNT`가 0이 아니면 migration이 임의
  winner/reservation을 고르게 하지 않는다. 현장 장착·계측 귀속을 확인해 감사된 reconciliation을 수행하고 재조회가
  0행일 때만 진행한다. `LEGACY_CORRELATION_PROVENANCE_ROWS`는 V137 append-only trigger 때문에 갱신하지 않으며,
  기존 행은 `CORRELATION_ID`를 provenance로 유지하고 새 행부터 typed `FEED_SESSION_ID`를 기록한다.
  V151은 복원본 rehearsal과 lock/log/rollback 근거 없이 실제 DB에
  적용하지 않으며 `-ApproveHighImpactMigrations`가 필수다. 파일 transaction이 실패하면 V114 기존 source index와
  schema/history가 함께 rollback되어 보존되는지 rehearsal에서 확인한다.
- **운영 초기 데이터(ops 시드 팩)**: `ops/sql/sys-menu-seed.mssql.sql`(메뉴 320행 — 빈 테이블 가드,
  원본은 `config/Seed/nexaone-menu.json` + 생성기 `tools/ops/Generate-MenuSeedSql.ps1`) ·
  `ops/sql/sys-batch-seed.mssql.sql`(로그 보존 정리 배치 2종 — 행 단위 멱등). 러너 `-IncludeOpsSeed`로 일괄 적용.
- **MSSQL 사전 검증**: 노드 접근 가능 시 `NEXAONE_MSSQL_TEST_CONN` 설정 후
  `dotnet test test/NexaOne.ServerTests/NexaOne.ServerTests.csproj -c Release --filter Category=MssqlContract` 실행.
  전 migration 적용·runtime 무결성 계약과 mssql 쿼리 `SET PARSEONLY`를 함께 확인한다. 테스트 노드 온라인 시
  수행할 필수 릴리즈 gate다.
- **성능 기준선(읽기 전용)**: 운영과 동일한 권한·부하 구간에서
  `tools/ops/Get-MssqlPerformanceBaseline.ps1 -ConnectionString $env:NEXAONE_MSSQL_CONN -LookbackDays 7`
  을 실행한다. 이 승인 기준선은 Query Store를 제공하는 SQL Server 2016 이상을 요구하며, 실제 DB의 Query Store가
  `READ_WRITE`가 아니면 실패한다. Query Store 상위 logical-read 쿼리, DB 자동 통계 생성·갱신 옵션, index read/write 사용량,
  statistics 갱신 시각·sampling·변경 건수,
  missing-index DMV 힌트, 실제 key/include/partition column·크기, View 목록과 indexed-view 실제 index 정의·사용량을 UTC run-id별
  `artifacts/mssql-performance/<run-id>/`에 덮어쓰기 없이
  수집한다. `query-store-plan-logical-reads.csv`, `query-store-window.csv`, `view-dependencies.csv`가 기본 근거이며,
  원문 query/View SQL과 plan XML은 기본 수집하지 않는다. `manifest.json`의 DB·엔진 버전·edition·DMV counter 시작 시각·보고서별 성공 여부와 최소 두 개의 대표
  구간을 함께 비교하고 실행 계획을 검토하기 전에는 자동으로 index를 생성·삭제하지 않는다. Query Store/DMV 또는
  `VIEW DEFINITION`, SQL Server 버전에 맞는 `VIEW DATABASE [PERFORMANCE] STATE` 및
  `VIEW SERVER [PERFORMANCE] STATE` 권한 때문에 필수 보고서가 빠지면 기본 실행은 실패한다. 진단 목적의 불완전 수집만
  `-AllowPartial`로 명시하고 Production 승인 근거로 사용하지 않는다. 물리 fragmentation은 기본 수집에 포함하지
  않으며 maintenance 점검 창에서만 `-IncludePhysicalStats -Top 100 -PhysicalStatsMinPageCount 1000`으로 큰 index
  후보를 명시적으로 제한해 `LIMITED` 모드로 수집한다. `AUTO_CREATE_STATISTICS` 또는
  `AUTO_UPDATE_STATISTICS`가 OFF인 DB도 기본 기준선 실행을 실패시킨다.
- **SQL Server 통계 유지보수**: DB의 `AUTO_CREATE_STATISTICS`·`AUTO_UPDATE_STATISTICS`를 기본 ON으로
  유지하고, `AUTO_UPDATE_STATISTICS_ASYNC`는 즉시 계획 정확성과 동기 재컴파일 지연을 비교해 DBA가 DB별로
  결정한다. 위 기준선의 statistics 갱신 시각·sampling·`modification_counter`와 Query Store 계획 회귀를
  같이 보고, 대량 backfill·retention·bulk load 후 편향이 확인된 테이블/통계만 점검 창에서
  `UPDATE STATISTICS [schema].[table] [stat] WITH RESAMPLE` 형태로 갱신한다. 전체 DB `FULLSCAN`을 앱
  기동·마이그레이션에 묶지 않고, 실행 전후 계획·logical read·CPU·재컴파일 영향을 기록한다.
- **SQLite 통계 유지보수**: 요청 hot path나 부팅 schema initializer에 `ANALYZE`를 넣지 않는다. 대량
  migration·retention·import 후 쓰기를 일시 중지한 점검 창에서 백업한 뒤 `PRAGMA optimize;`를 우선
  실행해 SQLite가 필요한 통계만 갱신하게 한다. 그 후에도 `EXPLAIN QUERY PLAN`의 index 선택이
  회귀하고 `sqlite_stat1`이 부정확한 경우에만 대상 테이블 `ANALYZE table_name;`을 수동 실행한다.
  `VACUUM`은 파일 공간 회수 작업이지 통계 갱신이 아니며, 별도 downtime/디스크 용량 계획 없이
  병합하지 않는다.
- **SQLite TRACE 조회 index**: cursor paging은 가변 소수 정밀도의 `COLLECTED_AT`을 7자리로 보정한
  expression과 `COLLECT_ID`를 정렬 key로 사용하며, `IX_FDC_TRACE_SOURCE`도 같은 expression을 가져야 한다.
  재개 시 seek 시작점은 `max(EFFECTIVE_FROM, cursor)`다. schema reconciliation 후 대표 cursor SQL의
  `EXPLAIN QUERY PLAN`에 `IX_FDC_TRACE_SOURCE`가 나타나고 `USE TEMP B-TREE`가 없어야 한다. 일반 parameter
  최신/기간 조회는 V017 raw 시간 index를 유지한다. 운영 SQLite를 수동 변경할 때는 식별자 대소문자가 다른
  동명 index도 충돌하므로 임의 DDL 대신 initializer 재조정을 사용한다.
- **FDC 수집 보존(V148)**: `IX_FDC_COLLECT_RETENTION(COLLECTED_AT, COLLECT_ID)`의 시간 선행 경로를
  사용해 기본 1,000행씩 별도 짧은 transaction으로 삭제하며, 한 worker 실행은 최대 100 batch
  (100,000행)에서 끝난다. 다음 주기가 같은 cutoff를 이어서 처리하므로 단일 대량 DELETE로
  lock·transaction log·SQLite writer를 장시간 점유하지 않는다. 연속으로 상한에 도달하면 최고 보존 행 시각과
  batch 소요시간을 먼저 경보하고, 운영 부하 리허설 없이 batch 크기를 키우지 않는다.
- **TRACE 소비 안전선(V150)**: FDC worker는 IVT의 활성 binding별 durable ingestion cursor를 Common
  `IFdcTraceRetentionGuard`로 조회하고, binding마다 `max(EFFECTIVE_FROM, LAST_COLLECTED_AT)`를 사용하며 cursor가
  없으면 `EFFECTIVE_FROM`을 low-watermark로 사용한다.
  SQLite에서는 활성 binding/cursor 시각을 행별 canonical UTC로 검증·파싱한 실제 최소값을 사용한다. 하나라도
  invalid/T/Z/offset 형식이면 purge가 fail-closed하는 것이 정상이며 해당 binding/cursor를 먼저 정정한다.
  guard 조회와 purge는 아직 하나의 cross-module transaction이 아니다. 따라서 Enabled 기간 전체에 binding
  INSERT/activate/reactivate, `EFFECTIVE_FROM/TO` 변경과 cursor 수동 후퇴를 금지하고
  `BindingChangesQuiesced=true`를 함께 설정해야 한다. 이 동결을 유지할 수 없으면 retention을 OFF로 둔다.
  지속 online 변경은 양쪽 mutation이 공유하는 durable revision/advisory-lock protocol 도입 전까지 지원하지 않는다.
  requested cutoff가 이 전역 안전선을 앞서지 못하게 한 뒤 `FDC_TRACE_RETENTION_STATE/GLOBAL` completeness
  boundary를 DELETE와 같은 transaction에서 먼저 단조 증가시킨다. 최초 전환은 이미 부분 삭제된 동일 timestamp batch까지
  gap으로 다루기 위해 남은 `MIN(COLLECTED_AT) + 100ns`, 빈 DB는 DB UTC 시각을 seed하고, 위 V150
  writer-quiescence/명시 승인 gate를 필수로 거친다. 이 singleton을 삭제하거나 boundary를
  뒤로 이동하지 않는다. purge와 동시 읽기 또는 신규/재활성 binding의 resume 지점이 boundary보다 과거이면
  `FdcTraceGapException`이 정상이다. 이를 빈 TRACE로 처리하거나 cursor를 자동 전진시키지 말고 retention을
  중지한다. 현재는 boundary 후퇴·pre-boundary raw 복원·binding range 직접 변경을 지원하지 않으므로 위
  stranded-binding ADR와 승인된 원장 reconciliation이 완료될 때까지 projection을 다시 켜지 않는다.
  경계보다 오래된 late/backdated INSERT, raw TRACE UPDATE와 `INSERT OR REPLACE`는 DB trigger가 거부한다.
  SQL Server INSERT guard는 RCSI에서도 `READCOMMITTEDLOCK, HOLDLOCK`으로 최신 GLOBAL 경계를 읽어 purge와
  직렬화하며 일반 INSERT끼리는 공유 잠금으로 병렬성을 유지한다. deadlock victim/lock timeout은 재시도 가능한
  수집 실패로 기록하되 행의 시각을 임의 보정하지 않는다. SQLite는 V149/V150 singleton과 raw TRACE의
  `INSERT OR REPLACE`도 `recursive_triggers=OFF`와 무관하게 BEFORE INSERT에서 거부한다.
  SQLite 기동 reconciliation은 같은 이름의 잘못된 V148 index도 정확한 시간 선행 key로 교체한다. 안전 시각은
  `yyyy-MM-dd HH:mm:ss[.fffffff]` UTC text만 허용하므로 `T`/`Z`/offset, 7자리를 넘는 소수 또는 존재하지 않는
  달력 날짜가 발견되면 기동을 실패시켜야 한다. 값을 임의 문자열 치환하지 말고 백업 후 실제 UTC instant를 확인해
  7자리 형식으로 정정한다. 정상 부팅의 검증은 invalid timestamp partial index를 사용하고, 제약/index 누락·변조
  시 전체 오염 재검증을 통과해야 trigger/index를 복구한다.
- **FDC runtime writer lease(V149)**: `FDC_RUNTIME_OWNERSHIP/GLOBAL`은 삭제하지 않는 fence counter다.
  설정의 `Worker:Fdc:Ownership:OwnerId`는 배포 instance를 식별하는 고유 prefix이며, 런타임이
  process id와 process-start nonce를 붙여 재시작마다 재사용하지 않는 실제 owner id를 만든다. 각 시작은
  canonical 설정 snapshot의 lowercase 64자리 SHA-256
  `CONFIG_REVISION`으로 acquire한다. 성공 호출자에게만 256-bit secret을 감춘 opaque grant가 반환되고 DB에는
  secret의 SHA-256 hash만 남는다. acquire/renew DB 호출 직전의 monotonic timestamp에 설정 TTL을 더한 보수적
  process-local deadline을 grant와 함께 유지하므로 응답 지연이나 DB/host wall-clock 차이가 권한을 연장하지 않는다.
  permit 조회와 startup/live 수집·DB retry·action 경계는 heartbeat continuation 전에도 이 deadline을 동기 검사한다.
  readiness/apply/reconcile/release adapter 호출은 `min(InterlockActionTimeout, wall-clock lease remainder,
  monotonic lease remainder)`로 token과 caller 대기를 제한하고, 반환 직후 동일 owner/fence/config 및 캡처 deadline을
  다시 검증한 뒤에만 결과를 수락한다. 이 구간의 timeout/authority loss는 물리 결과 미확정으로 fail-closed하고
  같은 EffectId로 controller journal을 reconciliation한다.
  heartbeat 주기는 lease TTL의 1/3 이하로 두고, renew 성공 때 반환된 최신 grant와 호출 시작 기준 deadline으로
  함께 교체한다. renew 1회 실패·DB 연결 단절·관찰한 owner/fence 변경은 즉시 소유권 상실로 처리하고 action 발행을
  중단한다. 공개 `HasOwnerTuple`은 현재 권한 판정에 사용하지 않는다. 정상 종료의 release는 best-effort이며 crash 후에는 DB UTC 만료로만
  takeover한다. 백업/복원·수동 정리 때 GLOBAL 행 삭제, `FENCE_TOKEN` 감소/0 재시드, lease tuple 직접 변경을
  금지한다. V149는 새 singleton table이라 high-impact 승인 대상은 아니지만, controller가 명령별 fence를
  영속하고 stale token을 거부하기 전에는 이 lease를 설비 자동 운전 승인으로 연결하지 않는다.
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
