# NexaFramework external consumer sample

이 sample은 NexaOne Server나 업무 Module을 참조하지 않는 독립 Generic Host다. 외부 소비자가 최신 NexaFramework의 공개 Driver 계약만으로 장기 protocol resource의 소유권과 호출 경계를 구성할 수 있음을 실행으로 검증한다.

## 참조 범위

project가 직접 참조하는 저장소 project는 다음 하나뿐이다.

- `submodules/NexaFramework/src/NexaFramework.Drivers.Hosting/NexaFramework.Drivers.Hosting.csproj`

이 sample은 NexaOne 제품 Hosting extension, Bridge, controller 또는 업무 DTO를 참조하지 않는다. 패키지 소비로 전환할 때는 project reference를 배포된 `NexaFramework.Drivers.Hosting` package reference로 바꾸면 된다.

## 사용하는 공개 Interface

- `IManagedDriver`: host가 소유할 장기 physical/protocol lifecycle 계약
- `AddDrivers<TDriver>`: immutable Driver plan 등록
- `DriverHost<TDriver>`: 생성·시작·호출·역순 정리의 단독 소유자
- `DriverContext`: health/fault 보고 경계
- `DriverHostSnapshot`: lifecycle, health, bounded diagnostics 관찰 경계

`ExternalProbeDriver`는 외부 소비자가 소유하는 product contract와 구현이다. 구현 인스턴스는 일반 DI로 노출되지 않으며, 제품 operation은 `DriverHost.InvokeAsync` callback 안에서만 실행된다. sample은 `StartAsync`, `Running/Healthy` snapshot, typed read, terminal cleanup까지 확인한다.

DB 연결을 한 번 열어보거나 Spring 소유 PLC catalog를 조회하는 readiness probe는 이 모델의 대상이 아니다. 그런 진단은 제품 소유 health/probe 계약으로 두고, `IManagedDriver`는 host가 실제 자원의 전체 lifecycle과 disposal을 소유할 때만 사용한다.

## 실행

저장소 루트에서 다음 명령을 순서대로 실행한다.

```powershell
dotnet restore samples/NexaOne.ExternalConsumer/NexaOne.ExternalConsumer.csproj
dotnet build samples/NexaOne.ExternalConsumer/NexaOne.ExternalConsumer.csproj --configuration Release --no-restore
dotnet run --project samples/NexaOne.ExternalConsumer/NexaOne.ExternalConsumer.csproj --configuration Release --no-build
```

성공 시 마지막에 다음 문장이 출력된다.

```text
External consumer self-check passed: external.probe | Running/Healthy | external-consumer-sample
```

## 외부 소비자 계약

- Driver ID는 case-sensitive ordinal 기준으로 고유하고 안정적이어야 한다.
- factory는 DI에서 가져온 기존 인스턴스가 아니라 새 host-owned 인스턴스를 반환해야 한다.
- constructor는 vendor I/O를 시작하지 않고 실제 resource 획득은 `StartAsync`에서 수행한다.
- 외부 호출은 raw Driver를 보관하지 않고 `DriverHost.InvokeAsync`로 제한한다.
- health/fault 메시지에는 연결 문자열, 암호, 인증서 원문, vendor payload 또는 raw exception text를 넣지 않는다.
- terminal `StopAsync` 뒤에는 같은 host generation을 다시 시작하지 않는다.
- 종료 시 `CleanupCompletion`으로 host-owned cleanup이 끝났는지 확인한다.
