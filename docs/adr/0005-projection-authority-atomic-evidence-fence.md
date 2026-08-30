# 작업 범위 투영 권한의 원자적 증거 fence를 한시 허용한다

- 상태: Accepted (temporary physical-schema exception, activation blocked)
- 결정일: 2026-08-31
- 소유자: POM·RMS·SYS / Server composition·Database security
- 검토 기한: 2026-11-30

## 배경

Cleaner 작업 범위 투영 권한은 다음 세 가지가 모두 정확히 일치할 때만 새로 만들 수 있다.

1. RMS가 실행 시점에 남긴 canonical recipe execution evidence
2. SYS가 배포 승인한 program artifact와 recipe snapshot binding
3. 같은 artifact에 대한 revocation이 아직 존재하지 않는다는 사실

directory Interface를 차례로 호출한 뒤 POM 트랜잭션에서 권한을 저장하면, 마지막 조회와 INSERT 사이에
revocation이 들어오는 TOCTOU가 생긴다. 현재 Spring.NET module context는 sibling이고 분산 트랜잭션을 제공하지
않으므로, 별도 module directory 호출로는 권한 INSERT와 revocation range lock을 하나의 DB 트랜잭션에 묶을 수
없다.

## 결정

V159 trusted-authority 경계에 한하여 POM Infrastructure가 아래 물리 테이블을 정확 조회하고 잠글 수 있도록
한시 허용한다.

| source | 허용 target | 허용 동작 |
|---|---|---|
| `WorkScopeProjectionAuthorityRepository.cs` | `RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE` | exact identity·recipe snapshot evidence 조회와 SQL Server update/range lock |
| `WorkScopeProjectionAuthorityRepository.cs` | `SYS_RELEASED_PROGRAM_ARTIFACT` | exact release coordinate·recipe binding 조회와 SQL Server update/range lock |
| `WorkScopeProjectionAuthorityRepository.cs` | `SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION` | 새 권한 생성 직전 revocation 부재 조회와 SQL Server range lock |
| `PomWorkScopeProjectionSqliteSchemaContribution.cs` | 위 세 테이블 | SQLite 기존 DB 검증 및 direct-DML trusted evidence·revocation guard 설치 |

POM은 위 RMS/SYS 테이블을 생성·수정·삭제하지 않는다. 허용 대상 이외의 RMS/SYS 물리 테이블, 다른 POM source,
업무 조회 일반화 또는 호스트 SQL로의 복제는 금지한다. architecture test가 source/target 여섯 쌍과 이 ADR의
존재를 정확 allowlist로 검증한다.

저장소의 SQL Server lock 순서는 POM work scope → 기존 authority → RMS evidence → SYS artifact → revocation
range → authority INSERT로 고정한다. 이미 생성된 정확한 authority는 사후 revocation 뒤에도 복구·재생할 수
있지만, 같은 artifact를 사용하는 새 authority는 거부한다. direct INSERT는 지원 API가 아니며, 기본 validator
reject와 worker OFF 상태를 유지한다.

## Spring 조립 경계

호스트 부모 컨텍스트의 `workScopeProjectionAuthorityValidatorProxy`는 호출할 때마다 현재 `Pom` 서비스의
`workScopeProjectionAuthorityValidator` child target을 다시 해석한다. 두 ID는 의도적으로 다르다. PomOnly처럼
child target이 없는 제품에서 같은 이름의 parent fallback이 자기 자신을 다시 호출하는 재귀를 만들지 않고,
resolve 실패·계약 type 불일치·self target을 `Projection.Authority.ValidatorUnavailable`로 거부한다. target을
찾은 뒤 발생한 DB 오류와 취소는 조립 실패로 숨기지 않고 호출자에게 전파한다. proxy는
`ApplicationServer.ReloadService`가 교체·폐기한 child context의 bean을 캐시하지 않는다.

Cleaner target은 같은 Pom child의 `workScopeAuthorityEvidenceDirectory`를 직접 사용한다. RMS와 SYS는 서로 다른
sibling context이므로 부모의 `canonicalRecipeExecutionEvidenceDirectoryProxy`와
`releasedProgramArtifactDirectoryProxy`가 각각 `Rms/canonicalRecipeExecutionEvidenceDirectory`,
`Sys/releasedProgramArtifactDirectory`를 호출 시점에 해석한다. 공유 계약은 Default ALC의 ServiceContracts에만
있고 Server proxy는 project 구현 assembly를 참조하지 않는다.

`CleanerProjectionAuthorityProfile`은 Cleaner context 생성 시 `IConfiguration`을 한 번 snapshot한다. 누락된
`Enabled`는 false이며 release coordinate 원문을 trim·정규화하지 않는다. 빈 값, 앞뒤 공백, control character,
V159 열 길이 초과 값은 incomplete로 거부한다. 운영 identity와 enable 값은 XML에 하드코딩하지 않는다. 이 조립이
추가되어도 `Worker:Pom:WorkScopeProjection:Enabled` 기본 OFF와 아래 운영 활성화 차단 조건은 바뀌지 않는다.

## 운영 활성화 조건

이 ADR은 증거 행 자체의 발행자를 신뢰하게 만드는 보안 경계가 아니다. 일반 애플리케이션 DB writer가 RMS/SYS
증거를 임의로 만들 수 있는 상태에서는 운영 활성화를 승인하지 않는다. Production 활성화 전에 다음을 별도
commissioning gate로 모두 검증한다.

1. RMS canonical evidence와 SYS release/revocation writer를 전용 DB role·signed procedure 또는 동등한 최소
   권한으로 분리한다.
2. POM runtime 계정은 해당 증거를 읽고 POM authority를 저장할 수 있지만 RMS/SYS 증거를 직접 생성·변경할 수
   없다.
3. 지원 writer의 lock order와 POM provision/revoke 동시성 테스트를 실제 SQL Server에서 통과한다.
4. direct DML 정책과 precompiled plugin 계약 버전을 배포 문서에 명시한다.
5. Spring sibling context를 가로지르는 운영 coordinator가 필요하면 작은 host Adapter로 조립하고, 판정 계약은
   공유 ServiceContracts에 유지한다.

## V160 trusted writer와 active-product 경계

V160은 위 활성화 조건 1·2를 다음 DB 경계로 구현한다. migration은 환경 credential을 만들거나 role에 넣지 않고
고정 역할 `NexaOneRmsEvidenceWriter`, `NexaOneSysReleaseWriter`, `NexaOneProjectionRuntime`과 same-owner static
procedure만 정의한다. public direct DML은 trusted evidence, release/revocation, authority 및 binding에서 거부한다.
RMS capture, SYS release/revoke, POM provision은 business actor와 별도로 실제 database principal name+SID를
append-only provenance로 남긴다. 문자열 parameter는 MAX로 받아 provider-level silent truncation을 막되, XML/control
검사 전에 DB column byte 상한을 먼저 검사한다.

POM runtime은 caller가 제공한 product/plugin coordinate를 신뢰하지 않는다. 환경별 commissioning이
principal name+SID와 Equipment/Operation/Artifact/ProductProfile/Plugin/ProductDefinitionVersion/ProgramVersion/
ProgramSchema/ProgramHash/recipe schema+hash의 정확한 binding을 저장하고, POM procedure가 SYS artifact에서 읽은
값과 BIN2+DATALENGTH로 비교한다. 한 principal에 여러 artifact binding을 허용하여 rolling upgrade 동안 이전
authority recovery를 보존한다. revocation은 새 authority만 차단하고 기존 exact replay는 허용하며, 기존 권한의 최초
provisioning provenance는 credential rotation replay가 바꾸지 않는다. 실행 중단은 해당 binding 제거로 한다.

SYS artifact의 release principal name+SID는 발행 당시의 historical provenance이고 현재 SYS writer role membership은
앞으로의 release/revoke 권한이다. Credential rotation은 과거 artifact를 소급 무효화하거나 provenance를 새 writer로
바꾸지 않는다. 현재 writer와 historical principal이 다른 artifact로 새 runtime binding을 만들 때는 full Apply가
server-read historical SID의 uppercase SHA-256 digest를 명시적으로 승인해야 한다. Exact existing binding의 idempotent
Apply/ValidateOnly에는 재승인을 요구하지 않지만 revocation은 항상 우선하며 approval digest로 우회할 수 없다.
ValidateOnly도 full Apply와 동일한 serializable artifact→revocation→binding 검사를 수행하므로, binding 생성 뒤
revocation된 artifact를 worker 활성화 상태로 보고하지 않는다. 이미 발행된 exact authority의 recovery/replay 허용은
이 commissioning 활성화 gate와 별도 계약이다.

SQL Server provisioning은 repository의 선행 검증/lock을 없애고 단일 POM procedure가 scope→authority identity/range
→RMS evidence→SYS artifact→revocation→binding→insert 순서를 소유한다. ingest/commit의 lock-sensitive authority
read도 별도 procedure가 authority→artifact→binding 순서를 명시한다. 비잠금 Get/readiness는 caller-filtered active
view를 사용한다. WorkScope command의 영구 mutation fence는 decommission 뒤에도 유지되어야 하므로 base row 전체가
아니라 WorkScope ID만 노출하는 별도 unfiltered fence view를 사용한다. lineage advance 역시 runtime에 base-table
column UPDATE를 주지 않고 active binding을 같은 순서로 다시 잠그는 static procedure에서 DB UTC로만 수행한다.
lineage procedure는 외부 transaction이 없으면 자체 transaction을 열어 마지막 binding 검증부터 UPDATE/commit까지
lock을 유지하며, 외부 transaction이 있으면 해당 caller commit까지 lock을 넘겨주지 않는다.

환경 commissioning은 writer bootstrap→writer evidence/release→full runtime binding의 2단계다. full Apply는
binding과 세 role의 exact membership/effective permission을 한 serializable transaction에서 검증하고 commit한다.
broad database/schema EXECUTE, 예상 밖 procedure EXECUTE/CONTROL/ALTER, nested·stale role membership,
세 principal을 향한 inbound database-user/server-login impersonation과 principal impersonation/ownership은
fail-closed한다. trusted table의 same-owner ownership chain을 탈출구로 만들지 않도록 V159/V160의 정적
procedure/view/trigger만 exact module allowlist에 두고, 그 밖의 dependency, `EXECUTE AS`, signature, dynamic SQL 및
object grant를 commissioning에서 거부한다. immutable JSON 증거는 CreateNew로만 기록한다. SQL Server의
dbo/db_owner/sysadmin 같은 관리자 권한은 엔진상 이 경계를 우회할 수 있으므로 신뢰 운영자/PAM 범위이며, 이 ADR은
관리자 침해를 방어한다고 주장하지 않는다.

CreateNew evidence file은 DB commit 직후 crash에서 0 byte/불완전 JSON일 수 있으므로 파일 존재만 성공으로 보지 않는다.
원 marker를 보존하고 새 path의 ValidateOnly evidence로 DB 상태를 재검증한 뒤 두 증거를 운영 감사에서 연결한다.

동일 owner chain의 경계를 다른 database나 synonym으로 우회하지 않도록 현재 DB의 `TRUSTWORTHY`·`DB_CHAINING`과
서버의 `cross db ownership chaining`은 OFF를 요구한다. trusted table로 직접 또는 같은 DB synonym chain을 통해
도달하는 user synonym이 하나라도 있으면 commissioning은 fail-closed한다.

사고 대응 decommission은 위 활성화 감사와 분리한다. writer user·role·procedure/view가 이미 손상 또는 제거됐더라도
V160 ledger와 core binding table을 확인한 뒤 runtime principal name(+존재하면 SID)과 특정 ArtifactId 또는 explicit
all 범위만 잠그고 삭제한다. 삭제 전 SID/artifact digest와 program/recipe hash는 immutable evidence에 남기며,
artifact-scoped 해제는 role을 바꾸지 않는다. 이 fail-safe 경로가 새로운 권한을 부여하거나 credential을 만들지는 않는다.
Commissioning 증거는 해당 시점의 ACL/module closure만 증명한다. worker enable 직전과 모든 DDL·role·permission·module
signature 변경 뒤 ValidateOnly를 재실행하고 DB security audit로 drift를 감시한다. 별도 DBA/DDL-admin이 이후
same-owner module을 추가하거나 allowlisted module 본문 자체를 `ALTER`할 수 있는 권한은 PAM 운영 신뢰 경계이며
V160 application role이 방어하지 않는다. 외부 실행 entry에서 도달 가능한 user module은 exact allowlist 밖이면
거부하지만, allowlisted definition 자체의 hash attestation까지 제공하지는 않는다.

V160은 provenance 없는 V159 trusted row가 하나라도 있으면 migration을 거부한다. legacy row를 자동 합성하거나
신뢰하지 않으며 새 DB cutover 후 trusted procedure로 재발행한다. SQLite는 role, SID attestation, ownership chaining의
등가물이 없으므로 V160 전체가 no-op이고 운영 보안 근거가 아니다. Production activation은 실제 SQL Server의
권한·경합·복구 Category gate가 통과할 때까지 계속 blocked다.

## 위험과 완화

- 물리 schema 결합: 정확한 세 테이블과 두 source만 자동 allowlist로 제한한다.
- lock inversion/deadlock: repository는 고정 순서와 제한된 SQL Server deadlock retry를 사용한다. 임의 direct
  DML은 운영 지원 대상에서 제외한다.
- ABI 변경: validator 계약을 배포하기 전에 외부 plugin을 모두 재빌드하고 호환 버전 정책을 확정한다.
- 증거 위조: 전용 writer credential이 검증되기 전에는 default reject/worker OFF를 해제하지 않는다.
- migration 소유 혼합: V159는 RMS·SYS·POM을 한 원자적 권한 경계로 묶는 integration migration으로 기록하며,
  이후 generic migration runner가 다중 module dependency와 단일 transaction을 표현할 수 있을 때 분리한다.

## 제거 조건

다음 중 하나가 실제 SQL Server 경합·복구 테스트를 통과하면 이 예외를 제거한다.

- coordinator가 서명한 단일 attestation/capability를 POM에 제공하여 POM이 RMS/SYS 물리 schema를 읽지 않아도
  revocation과 authority INSERT의 원자성을 보존한다.
- module-owned transaction capability가 같은 DB connection·transaction과 lock order를 안전하게 공유한다.

제거 시 repository와 SQLite contribution의 외부 테이블 SQL, architecture allowlist, 이 ADR을 같은 변경에서
정리한다. NexaFramework 이관과 Production release 승인 전에 반드시 재검토하며, 기한 연장은 새 결정 기록 없이
허용하지 않는다.
