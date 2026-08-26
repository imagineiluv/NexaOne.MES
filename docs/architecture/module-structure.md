# 업무 Module 구조와 의존 규칙

이 문서는 `src/04.Modules/NexaOne.*` 아래 업무 Module의 표준 구조와 의존 규칙을 정의한다. 신규 Module은 처음부터 이 규칙을 따르고, 기존 Module은 기능을 변경하는 slice부터 같은 구조로 수렴한다.

여기서 **Module**은 호출자가 알아야 할 작은 **Interface** 뒤에 업무 규칙과 implementation을 숨기는 단위다. **Seam**은 Interface가 놓이는 위치이고, 외부 저장소·메시지 버스·설비 통신을 연결하는 구체 구현은 **Adapter**다. Module의 Interface에는 타입뿐 아니라 불변식, 호출 순서, 오류, 설정과 성능 제약도 포함된다.

## 목표

- 업무 계산과 불변식을 한 Module에 모아 변경의 locality를 높인다.
- 호출자는 작은 Interface만 학습하고 Module implementation의 세부사항을 알지 않는다.
- 제품 호스트는 조립과 transport만 담당하고 업무 계산을 소유하지 않는다.
- Module 사이의 결합은 공유 계약 또는 이벤트로 드러내며 프로젝트 참조로 숨기지 않는다.
- 외부 I/O는 Interface와 Adapter가 만나는 Seam에서 교체하고 검증한다.

## 표준 디렉터리

```text
src/04.Modules/NexaOne.<NAME>/
├── Module.cs
├── Domain/
├── Application/
├── Infrastructure/
├── Api/
├── Resources/
└── NexaOne.<NAME>.csproj
```

기존 Module에 일부 디렉터리가 아직 없다면 빈 디렉터리를 만들지 않는다. 새 책임이 생길 때 아래 소유 규칙에 맞는 위치를 만들고, 다른 위치에 임시로 두지 않는다.

| 위치 | 소유 책임 | 허용되는 의존 | 금지되는 내용 |
|---|---|---|---|
| `Module.cs` | Module의 단일 조립 진입점, 공개 Interface와 Adapter 등록, 필요한 capability 선언 | 같은 Module의 `Application`, `Infrastructure`, `Api` | 업무 계산, SQL, transport 처리, 다른 업무 Module 탐색 |
| `Domain/` | aggregate, value object, domain event, 순수 계산과 불변식 | BCL과 최소 공통 primitive | DB·HTTP·메시지·파일 I/O, Framework/호스트 타입, 다른 Module 타입 |
| `Application/` | use case, 트랜잭션 흐름, 외부 의존 port, 결과와 오류 계약 | `Domain`, 공유 계약 | 구체 DB/브로커/장비 구현, transport DTO, 다른 Module implementation |
| `Infrastructure/` | persistence, 메시징, PLC 등 외부 의존 Adapter | `Application`의 port, `Domain`, 외부 라이브러리 | 다른 Module의 물리 테이블 직접 접근, 호스트 호출, 업무 불변식 재구현 |
| `Api/` | HTTP/Bridge/명령 transport Adapter, 입력 변환과 상태 코드 매핑 | `Application`, 공개 공유 계약 | 업무 계산, 직접 SQL, 다른 Module orchestration |
| `Resources/` | Module 소유 query, migration, screen seed와 정적 설정 | Module이 정의한 자산 계약 | 다른 Module 자산 수정, 전역 ID 무소유 등록 |

`Module.cs`는 작은 Interface 뒤에 조립 복잡성을 숨겨야 한다. 단순히 내부 타입을 모두 다시 노출하는 얕은 Module은 표준 구조를 지킨 것으로 보지 않는다.

## 의존 방향

허용 방향은 다음과 같다.

```text
NexaOne.Server (composition root)
        │
        ├──> Module.cs ──> Api ──> Application ──> Domain
        │        └───────> Infrastructure ────────┘
        │
        └──> shared contracts / reusable Framework

Infrastructure Adapter ──implements──> Application port
Api Adapter ──────────────calls───────> Application Interface
```

강제 규칙은 다음과 같다.

1. `Domain`은 안쪽 끝이다. `Application`, `Infrastructure`, `Api`, `NexaOne.Server` 또는 다른 업무 Module을 참조하지 않는다.
2. `Application`은 `Domain`과 공유 계약만 참조한다. 외부 시스템을 생성하지 않고 port를 입력받는다.
3. `Infrastructure`와 `Api`는 바깥쪽 Adapter다. 둘 사이를 직접 호출하지 않고 `Application` Interface를 통한다.
4. `NexaOne.Server`는 composition root다. Module과 Adapter를 조립할 수 있지만 업무 계산이나 Module 소유 SQL을 구현하지 않는다.
5. 업무 Module은 `src/00.Main/NexaOne.Server`를 참조하지 않는다.
6. 업무 Module은 다른 `src/04.Modules/NexaOne.*` 프로젝트를 직접 참조하지 않는다.
7. 재사용 Framework인 `NexaFramework`와 `NexaFramework.Hosting`은 `NexaOne`, `NexusOne`, `NexaMes`, `MES` 제품 프로젝트를 참조하지 않는다.

테스트 프로젝트는 여러 Module을 함께 참조해 계약을 검증할 수 있다. 이 허용은 production 프로젝트의 의존 방향을 완화하지 않는다.

## 공유 계약과 Module 간 협력

Module 간 협력의 기본 Seam은 `src/02.Backend/NexaOne.Common/ServiceContracts`의 공유 계약이다.

- 공유 계약에는 Interface, 요청/응답 DTO, 식별자와 이벤트 스키마만 둔다.
- 공유 계약은 Module implementation, persistence 타입, Spring bean, ASP.NET 타입을 노출하지 않는다.
- 요청/응답 협력은 공유 Interface를 통해 호출하고, 비동기 협력은 versioned domain/integration event를 사용한다.
- 호스트가 여러 Module의 결과를 묶어야 하면 orchestration은 호스트 Adapter에 두되, 각 업무 판정은 소유 Module Interface로 되돌린다.
- 다른 Module의 테이블을 직접 join하거나 갱신하지 않는다. 데이터 소유 Module이 query Interface, Bridge 또는 이벤트 projection을 제공한다.
- 공통 기술 기능은 `src/02.Backend` 또는 재사용 Framework로 올릴 수 있지만 업무 용어와 규칙이 포함되면 원래 Module에 남긴다.

두 개 이상의 production/test Adapter가 실제로 필요한 Seam만 만든다. 한 구현만 있는 가상 Seam을 추가하거나, Module 내부 테스트 편의를 위해 내부 Seam을 외부 Interface로 노출하지 않는다.

## 외부 I/O Adapter

DB, Kafka, PLC/OPC-UA, 파일 시스템과 원격 시스템은 다음 규칙을 따른다.

1. `Application`이 필요한 동작과 오류를 port로 정의한다.
2. `Infrastructure`가 production Adapter를 구현한다.
3. 테스트는 같은 port의 in-memory 또는 simulator Adapter를 사용한다.
4. 연결 문자열, 인증서, 암호와 원시 예외는 Module Interface나 health 응답에 노출하지 않는다.
5. 취소, timeout, 재시도, 멱등성과 트랜잭션 범위는 Interface 계약에 포함한다.

## query·migration·screen seed 소유

최종 소유 위치는 각 Module의 `Resources/`다. 중앙 경로를 사용하는 전환 기간에는 다음 규칙을 적용한다.

- query ID는 `<MODULE>.<Name>`, DB 객체와 migration 설명은 `<MODULE>_`, 화면 ID는 소유 Module prefix를 사용한다.
- 파일 또는 루트 metadata에 소유 Module을 선언한다.
- 한 자산은 한 Module만 쓴다. 공유 조회가 필요하면 소유 Module Interface 또는 별도 projection을 만든다.
- 중앙 `NexaOne.Server/config`에 있는 자산도 호스트 자산으로 간주하지 않는다. 업무 의미와 변경 승인은 해당 Module이 소유한다.
- 자산을 `Resources/`로 이동할 때 검색 순서와 ID를 바꾸지 않고, 충돌 검사를 먼저 통과시킨다.

## 예외 절차

의존 규칙은 편의를 이유로 인라인 suppress할 수 없다. 예외가 필요하면 다음 순서를 모두 따른다.

1. ADR에 필요한 의존 edge, 대안이 불가능한 이유, 위험, 소유자, 제거 조건과 검토 기한을 기록한다.
2. 공유 계약이나 호스트 Adapter로 Seam을 옮길 수 없는지 먼저 검토한다.
3. 예외는 프로젝트 전체가 아니라 정확한 source/target 한 쌍으로 제한한다.
4. 같은 변경에서 architecture test를 좁게 갱신하고, 허용 edge와 금지 edge를 모두 검증한다. 주석만 추가해 테스트를 우회하지 않는다.
5. 제거 조건이 충족되면 예외와 ADR 상태를 함께 정리한다.

보안, 트랜잭션 원자성 또는 운영 복구를 위해 임시 orchestration이 필요하면 호스트 Adapter가 맡고 업무 계산은 Module Interface에 유지한다. 긴급 변경도 사후 문서화가 아니라 같은 변경 묶음에서 예외 근거를 남긴다.

## 자동 검증

`ModuleDependencyBoundaryTests`는 다음 회귀를 막는다.

- 모든 `src/04.Modules/NexaOne.*` 디렉터리가 정확히 하나의 project를 소유하는지 확인
- 업무 Module에서 `NexaOne.Server`로 향하는 참조 차단
- 업무 Module 사이의 직접 참조 차단
- `NexaFramework`와 `NexaFramework.Hosting`에서 제품/MES 참조 차단

집중 실행 명령:

```powershell
dotnet test test/NexaOne.UnitTests/NexaOne.UnitTests.csproj --configuration Release --filter "FullyQualifiedName~NexaOne.UnitTests.Architecture.ModuleDependencyBoundaryTests"
```

새 Module, 프로젝트 참조, 공유 계약 또는 Framework 의존을 추가하는 변경은 이 테스트를 필수 검증으로 실행한다.
