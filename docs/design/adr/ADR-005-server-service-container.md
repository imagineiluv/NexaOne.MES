# ADR-005 — NexaOne.Server의 역할: 서비스 빈 컨테이너

- **Status**: Accepted (채택)
- **Date**: 2026-06-16
- **구현현황**: 구현 완료 — 9개 모듈의 도메인 서비스 19개 + 의존 리포지토리를 빈으로 등록, SQLite 모드로 NexaOne.Server 풀 부팅(전 빈 인스턴스화) 검증. 전체 스위트 그린(단위 1067, 통합 260/1스킵).
- **관련**: [ADR-004](ADR-004-server-host-runtime.md)(호스트 런타임·DB 전환·플러그인 로딩), 설계문서 §3.1, §6.1
- **결정자**: 사용자 승인

## 컨텍스트

`NexaOne.Server`(NexusFramework Spring.NET 콘솔 호스트)의 런타임 목적이 비어 있었다 — 부팅 후 컨텍스트만 올리고 `Ctrl+C`를 대기했으며, 실제 워크로드(REST·SignalR·백그라운드 워커)는 전부 `NexaOne.API`(ASP.NET 호스트)에 있었다. 또한 `nexaone.xml`은 모듈당 서비스 1개만 등록한 대표 슬라이스였다.

사용자 결정으로 역할을 확정한다: **NexaOne.Server는 "서비스 빈 컨테이너"다.** bean 객체(서비스)를 담아 서비스를 관리하고, 각 서비스는 "서버 빈"(공통 빈)을 공통으로 호출한다. (대안인 "백그라운드/워크플로 처리 호스트"는 인메모리 버스+SignalR가 API 프로세스에 공존해 cross-process 이벤트 전달이 필요해지는 부담이 있어 채택하지 않았다.)

## 결정

**(1) 부모 컨텍스트(server.xml) = 공통 서버 빈, 자식 컨텍스트(nexaone.xml) = 서비스 빈.** 모든 도메인 서비스가 공통으로 의존하는 빈을 부모에 두고, 자식의 서비스/리포 빈이 `ref`로 공통 호출한다. 공통 서버 빈: `eesDataSource`·`eesDialect`·`dbProvider`·`workflowManager`·`appConfiguration`·`opcUaDriver`. 외부/교차 조회는 `ApplicationServer.GetServerBean(name)` / `GetBean(service, name)`.

**(2) `appConfiguration`(IConfiguration)을 부모로 이동.** 거의 모든 리포지토리가 outbox 게이트 판독에 공통 사용하므로, 자식이 아니라 공통 서버 빈으로 둔다.

**(3) OPC-UA 드라이버를 부모 빈(`opcUaDriver`, lazy-init)으로.** FDC 모듈이 설비 수집 시 `GetServerBean("opcUaDriver")`로 공통 호출한다(드라이버는 서버 빈, 모듈에서 호출). `OpcUaDriver`는 전 선택적 파라미터 생성자라 Spring zero-arg 생성을 위해 무인자 ctor를 추가했다(NexusLogic 서브모듈). `lazy-init`으로 참조 시에만 생성해 부팅 시 OPC-UA 초기화·연결을 회피한다.

**(4) 전체 도메인 서비스(19개) + 의존 리포지토리를 등록한다.** 9개 모듈을 전수 대조해 등록: Equipment / EquipmentAlarm·EquipmentState / Fdc(Data·ParameterGroup·Interlock·Alarm·Collector) / Recipe / Qms / Cmms·MaintenancePlan / Pom·ProductionOrder / Shp / User·UserRegistration·UserMenu.

**(5) API/웹 인프라에 결합된 서비스 5개는 콘솔 컨테이너에서 제외한다**(빈을 지어내지 않음 — 해당 인프라가 있는 NexaOne.API에서 제공):
- `MdmMasterService` — `ICacheService`(캐시)
- `LotTrackingService` — `ITrackingMasterGateway`(API 조립 계층 어댑터)
- `MenuService` — `ILogger<>`(로깅)
- `ConditionSettingService` — `int`(스칼라 설정값)
- `DeployService` — `IDeployFileStorage`(파일시스템 저장소, API 계층 구현)

**(6) Spring.NET은 C# 선택적 파라미터를 인식하지 못하므로** 모든 빈의 constructor-arg는 대상 생성자의 전 인자를 명시한다(예: FdcInterlockService/FdcAlarmService/FdcCollectorService의 선택적 의존도 실제 빈 주입).

## 결과

- **장점**: 호스트 역할이 명확해짐(서비스 빈 컨테이너). 공통 빈을 부모에 모아 서비스가 일관되게 공통 호출. 전 도메인 서비스가 컨테이너에서 조립·조회 가능. SQLite로 외부 DB 없이 전 그래프 부팅 검증.
- **비용/위험**: 인프라 결합 서비스 5개는 콘솔 컨테이너로 조립 불가(API 전용). nexaone.xml이 커져(약 50빈) 신규 서비스/리포 추가 시 ctor 정합을 유지해야 함(전수 대조 절차).
- **비채택**: 백그라운드/워크플로 처리 호스트(cross-process 이벤트 부담), 인프라 결합 서비스의 빈 날조, 대표 슬라이스 유지(역할 미완).
