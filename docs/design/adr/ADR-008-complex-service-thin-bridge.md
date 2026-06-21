# ADR-008 — 복잡 서비스 얇은 브리지: 타입드 인터페이스(공유 계약 어셈블리)

- **Status**: Accepted (채택 — Phase 3c, 대표 슬라이스 EST 구현)
- **Date**: 2026-06-20
- **관련**: [ADR-001](ADR-001-query-gateway.md)(명명쿼리 게이트웨이), [ADR-003](ADR-003-security-pep.md)(권한 PEP), [ADR-005](ADR-005-server-service-container.md)(Server=서비스 빈 컨테이너), [ADR-006](ADR-006-web-worker-separation.md)(웹/워커 분리·모듈 독립 plugin ALC), [통합 호스트 설계](../specs/2026-06-20-unified-host-design.md) §5·§7, [Phase 3c 설계](../specs/2026-06-20-unified-host-phase3c-bridge-design.md)
- **결정자**: 사용자 승인

## 컨텍스트

통합 호스트(NexaOne.Server)는 도메인 모듈을 Spring.NET ApplicationServer로 **plugin ALC**에 격리 로드한다(ADR-006). Phase 2~3b는 데이터 경로를 **명명쿼리 게이트웨이**(ADR-001, `IRuleDispatcher`+`IQueryRegistry`, Default-ALC, Dictionary in/out)로 노출해 plugin↔DI 브리지를 회피해 왔다. 그러나 NexaOne.API 20개 컨트롤러 중 14개가 아직 미커버이고, 그중 일부는 **단일 SQL 디스패치로 재현 불가능한 복잡 도메인 서비스**다(다중 트랜잭션·인메모리 상태기계·낙관적 동시성·하드웨어/실시간·도메인 불변식). 대표적으로 EST 설비상태(`EquipmentStateService.ChangeStateAsync`)는 낙관적 동시성(`StateVersion`), 매트릭스 기반 전이 검증, 상태+이력 원자 쓰기를 갖고 `Result<T>`의 Conflict/InvalidTransition/Validation/Success 분기를 HTTP로 충실히 전달해야 한다.

게이트웨이의 리플렉션 범용 디스패처(`NexaFrameworkRuleDispatcher.DispatchAsync`)는 이 요구를 못 채운다: (a) 입출력이 `IDictionary<string,object>`/`object?`로 고정되어 `Result<T>` 분기를 손실 없이 전달 불가, (b) **모든 예외를 catch해 null을 반환**([NexaOneEesServiceExtensions.cs](../../../src/02.Backend/NexaOne.Application/NexaOneEesServiceExtensions.cs))해 실패 트랜잭션과 결과없음이 구분되지 않는다(쓰기 경로에서 부분 커밋·무음 손상 위험).

## 결정

**(1) 복잡 서비스는 "타입드 인터페이스 브리지(공유 계약 어셈블리)"로 노출한다.** 리플렉션 범용 디스패처는 **게이트웨이(조회·명명쓰기) 전용으로 동결**하고 복잡 쓰기에 절대 사용하지 않는다. 신규 어셈블리 **`NexaOne.ServiceContracts`**(Default-ALC 전용)에 서비스 인터페이스(예: `IEquipmentStateBridge`)와 경량 DTO를 둔다. 결과 타입은 `NexaOne.Common.Result<T>`(이미 양 ALC 공유)를 그대로 사용한다.

**(2) 타입 동일성은 ADR-006 deps-제외 메커니즘으로 보장한다.** `NexaOne.ServiceContracts`는 NexaOne.Server가 직접 ProjectReference해 **Default ALC 출력 루트**에 둔다. 모듈(예: NexaOne.EST)도 ProjectReference하되, 모듈 DLL은 `CopyDomainModulePlugins`로 `./Modules/`에 **`.deps.json` 없이** 게시되므로(NexaOne.Server.csproj) plugin ALC의 `AssemblyDependencyResolver`가 자체 복사본을 찾지 못해 **Default ALC 복사본으로 흘려보낸다**(NexaOne.Common/Application/Infrastructure와 동일 흐름). 따라서 plugin ALC에서 생성된 구현 인스턴스를 Default ALC 컨트롤러가 공유 인터페이스로 캐스트할 때 `InvalidCastException`이 발생하지 않는다.

**(3) 모듈은 어댑터 빈으로 계약을 구현한다.** 모듈에 `EquipmentStateBridge : IEquipmentStateBridge` 어댑터를 두고 기존 `EquipmentStateService`에 위임하며 **도메인 엔티티→DTO 매핑**을 수행한다(도메인 엔티티를 직렬화 계약으로 노출 금지 — ALC/버전 결합 차단). 어댑터 빈은 모듈 xml(est.xml)에 배선한다.

**(4) 호스트는 부팅 시 DI 어댑터로 등록한다(fail-fast).** Program.cs가 모듈 컨텍스트 생성 후 `ApplicationServer.GetBean("Est","equipmentStateBridge")`를 공유 인터페이스로 캐스트해 `AddSingleton<IEquipmentStateBridge>`로 등록한다. 캐스트/빈 부재는 **기동 시점에** 명확히 실패시켜 무음 런타임 실패를 막는다. 모듈 비활성(웹 셸/테스트, `Server:Modules:Enabled=false`) 시에는 등록을 건너뛴다.

**(5) 진입점은 전용 타입드 컨트롤러.** 게이트웨이(Dictionary)는 조회·명명쓰기 전용으로 역할을 유지하고, 복잡 서비스는 모듈별 전용 컨트롤러(예: `EstBridgeController`, `api/v1/est`)가 `IEquipmentStateBridge`를 주입받아 `Result<T>`를 HTTP로 매핑한다(NexaOne.API의 `ControllerResultExtensions`와 동일: Conflict→409, NotFound→404, Validation/Failure→400, 성공→200). 권한은 호스트의 기존 패턴(QueryGatewayController의 수동 permission 클레임 검사 + 와일드카드 `*`)을 따른다(쓰기는 `est:manage` 필요). 감사 사용자는 `CurrentUserContext`(AsyncLocal, AuditUserContextMiddleware가 JWT에서 설정)가 plugin ALC까지 전파되므로 추가 배선 불필요하며, 도메인 의미가 있는 `requestedBy`는 토큰에서 취해 명시 전달한다(비-부인성).

## 비채택

- **리플렉션 범용 디스패처 확장**: 예외 무음화·Dictionary 고정으로 복잡 쓰기에 부적합(부분 커밋/무음 실패 위험). 게이트웨이 전용 동결.
- **도메인 엔티티 직접 직렬화**: plugin 내부 타입을 계약으로 굳혀 ALC/버전 결합 유발. DTO 매핑으로 차단.
- **`GetBean` 컨트롤러 직접 호출**: 테스트 모킹 곤란 + 캐스트 실패 지연 검출. 부팅 시 DI 어댑터 등록으로 대체.
- **계약을 NexaOne.Common/Application에 혼재**: 전용 어셈블리로 응집해 plugin 참조-전용 빌드 가드(`Private=false`/`ExcludeAssets=runtime` 또는 deps-제외)를 명확히 한다.

## 결과

- **장점**: 복잡 도메인 결과(`Result<T>`)를 손실 없이 HTTP로 매핑, 컴파일타임 타입 안전 + 컨트롤러 단위테스트 모킹 가능, 게이트웨이-최대 원칙 유지(브리지는 최소), ADR-006 격리 보존.
- **비용/위험**: plugin ALC 타입 동일성은 deps-제외 규칙 준수에 의존(위반 시 `InvalidCastException`) → 부팅 fail-fast로 즉시 검출. plugin ALC 로드는 WebApplicationFactory 테스트로 재현 곤란 → ALC 동일성은 **수동 기동 검증**으로 확인(Phase 1 패턴), 어댑터 로직·Result 매핑·컨트롤러는 자동 테스트.
- **확장**: 동일 패턴으로 RMS 레시피 승인(완료)·**SHP 출하주문 생명주기(완료 2026-06-21, main a8660e6 — `IShipmentBridge`/`ShipmentBridge`/`ShpBridgeController` api/v1/shp, DeliveryOrder Confirm/Ship/Cancel 상태전이 Result→409/400/200, 실 modules-ON 부팅 캐스트는 HostModulesBootSmokeTests가 검증)**·Lot 추적(보류 — 다단계 트랜잭션 원자성 선결)을 후속 슬라이스로 복제. "모듈별 API 소유" 확장의 대표 패턴: CRUD·조회는 게이트웨이 명명쿼리(ADR-001), 상태기계·불변식 등 복잡 서비스는 본 브리지. 후속 후보: CMMS 작업지시·POM 생산오더 상태전이(단일 애그리거트=브리지), MDM/SYS/QMS/FDC설정 CRUD(=게이트웨이). FDC 실시간 수집은 하드웨어/라이브 구독 의존이라 REST 브리지 대상이 아니라 워커 소유 유지(ADR-006). 워크플로 엔진은 ADR-006 Phase 4 보류 유지.
