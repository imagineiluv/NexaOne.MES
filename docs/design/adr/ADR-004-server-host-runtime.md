# ADR-004 — NexaOne.Server 호스트: DB 공급자 전환 + 도메인 모듈 플러그인 로딩

- **Status**: Accepted (채택)
- **Date**: 2026-06-16
- **구현현황**: 구현 완료 — NexaOne.Server를 SQLite 모드로 풀 부팅(스키마 자동생성 → 부모·자식 컨텍스트 9개 모듈 전 서비스 인스턴스화 → Ready) 검증. 전체 스위트 그린(단위 1067, 통합 260/1스킵). MSSQL 실부팅은 사용자 환경 검증 잔여(자식 컨텍스트 와이어링은 DB 무관).
- **관련**: 설계문서 §3.1(호스트 구성), §18.6.1(AssemblyLoadContext 격리 로딩)
- **결정자**: 사용자 승인

## 컨텍스트

`NexaOne.Server`는 NexusFramework Spring.NET 콘솔 호스트다(설계 §3.1, 레거시 `SmartEES.App`(WinForms) → `NexaOne.Server` + `NexaOne.Web`). `ApplicationServer.CreateServer`가 **부모 컨텍스트**(`server.xml`)를, `AddService`가 **자식 컨텍스트**(`nexaone.xml`)를 `ClassLoader`(플러그인 `AssemblyLoadContext`)로 로드한다. DB 공급자는 NexusCom `IDatabaseProvider` 추상화를 따른다.

두 가지 요구가 있었다:
1. **코드 재빌드 없이 DB 전환** — 로컬/테스트는 외부 DB가 필요 없는 SQLite, 운영은 MSSQL. 전환은 설정(XML)만으로.
2. **도메인 모듈(9개)을 플러그인으로 로드** — `./Modules/`에 게시(설계 §3.1/§18.6.1).

구현 중 드러난 핵심 제약: Spring.NET은 빈을 `"Namespace.Type, AssemblyName"` 문자열로 정의하고 `TypeResolver`가 `Assembly.LoadWithPartialName`으로 해석한다. .NET은 **수집형(collectible) ALC**에 로드된 어셈블리를 partial-name으로 바인딩하는 것을 금지한다("Resolving to a collectible assembly is not supported"). 또한 Spring.NET은 **C# 선택적 파라미터 기본값을 인식하지 못한다**.

## 결정

**(1) DB 전환은 `server.xml` 단일 파일로 한다.** `[MSSQL]`(기본)과 `[SQLite]`(주석) 두 블록을 두고 하나만 활성화한다. 세 객체 id(`dbProvider`/`eesDialect`/`eesDataSource`)를 양 블록에서 동일하게 유지해, `nexaone.xml`의 참조(`ref="eesDataSource"`, `ref="eesDialect"`)가 불변이다. `eesDialect`(SQL 방언, `INexaOneEESDbCapability`)는 부모 컨텍스트(`server.xml`)에 두고 자식이 parent ref로 주입받는다 — 전환이 한 파일로 끝나게.

**(2) 공급자는 `NexusCom.Data` 메타 패키지 '하나'로 참조한다.** 이 단일 참조가 `NexusCom.Data.MsSql`·`NexusCom.Data.Sqlite` 두 공급자 어셈블리를 함께 출력에 끌어온다(어셈블리는 분리 유지, 런타임에 XML로 선택). NexusCom 루트가 두 공급자를 역참조하면 순환이라(공급자→루트 의존 존재), 루트가 아닌 별도 메타에서 집약한다.

**(3) `SqliteProvider`에 무인자 생성자를 추가한다.** 전 선택적 파라미터 생성자만으로는 Spring.NET zero-arg 리플렉션이 실패하므로 명시한다(`MsSqlProvider`는 이미 무인자 ctor 보유). 이로써 server.xml이 공급자를 factory 우회 없이 직접 생성한다.

**(4) SQLite 모드는 스키마를 자동 부트스트랩한다.** `SqliteSchemaInitializer`(NexaOne.Infrastructure)가 `db/migrations`의 MSSQL DDL을 SQLite 방언으로 변환해 **빈 DB일 때만** 생성한다(idempotent). 통합 테스트(`SqliteSchemaBootstrapper`)와 단일 구현을 공유한다. MSSQL 모드는 부트스트랩하지 않는다(운영은 마이그레이션 외부 적용).

**(5) 플러그인 ALC를 비수집형(`isCollectible: false`)으로 한다.** Spring.NET 문자열 타입 해석과 양립하기 위한 필수 조건이다(위 .NET 제약). `FileSystemApplicationContext`가 등록한 `AssemblyResolve` 폴백은 비수집형 어셈블리만 반환할 수 있다.

**(6) `nexaone.xml` 빈은 대상 생성자의 모든 인자를 명시한다.** Spring.NET이 C# 선택적 파라미터를 인식하지 못하므로, 선택적 인자도 실제 빈 주입 또는 `<null/>`로 채운다(예: `FdcInterlockService`의 2번째 `IFdcInterlockHistoryRepository`를 실제 이력 리포로 주입).

## 결과

- **장점**: 코드 변경 없이 server.xml 한 파일로 MSSQL↔SQLite 전환. 외부 DB 없이 로컬/테스트 풀 부팅. 공급자 단일 참조로 소비측 단순화. 플러그인 모델(`./Modules/`)·`AddService` 메커니즘 보존.
- **비용/위험**: 비수집형 ALC라 핫리로드(`ReloadService`)가 메모리를 회수하지 못한다(`ClassLoader.Unload`는 그 예외를 무시; 새 ALC는 적재되나 구 ALC 잔존). 현재 핫리로드 미사용이라 수용. SQLite 부트스트랩 스키마는 운영 MSSQL과 1:1이 아니라 구조 동등 수준(테스트/로컬용).
- **비채택**: 수집형 ALC(핫리로드 우선 — Spring 타입 해석과 비양립) / NexaOne 쪽 `SqliteProviderFactory`(공급자는 NexusCom 소관이라는 요구와 어긋남, SqliteProvider 무인자 ctor로 대체) / 공급자 소스 물리 통합(SqlClient+Sqlite를 NexusCom 루트에 상시 포함 — 무거움) / ProjectReference 직접 참조(플러그인 격리 포기).

## 적응(설계문서 대비)

§18.6.1의 `PluginLoadContext`는 `isCollectible: true` 청사진을 제시하나, **DI 컨테이너(Spring.NET)가 플러그인 타입을 문자열로 해석하는 호스트(NexaOne.Server)에서는 `false`로 적응한다.** 격리·`AssemblyDependencyResolver` 위임 등 나머지 패턴은 유지한다.
