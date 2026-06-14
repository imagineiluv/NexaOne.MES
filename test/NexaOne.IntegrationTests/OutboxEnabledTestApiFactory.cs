using Microsoft.AspNetCore.Hosting;

namespace NexaOne.IntegrationTests;

/// <summary>
/// Events:Outbox:Enabled=true로 브로커리스 인메모리 Event Bus를 켠 테스트 팩토리(ADR-002 대표 슬라이스 검증용).
/// 이 플래그를 켜면 EST 상태 변경 시 리포가 도메인 이벤트를 같은 트랜잭션에 EES_OUTBOX로 기록하고,
/// 인메모리 디스패처/구독자도 함께 기동한다. 기본 TestApiFactory(플래그 off)와 대비해 게이트를 검증한다.
/// </summary>
public sealed class OutboxEnabledTestApiFactory : TestApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Events:Outbox:Enabled", "true");
    }
}
