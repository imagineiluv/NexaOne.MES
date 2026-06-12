using Microsoft.AspNetCore.Http;
using NexaOne.API.Middleware;

namespace NexaOne.UnitTests.Middleware;

/// <summary>§17.3 — 요청 단위 CorrelationId 수신/생성과 응답 Header 반환을 검증한다.
/// (LogContext 필드 주입은 Serilog 파이프라인 통합 동작이라 헤더 계약만 단위 검증)</summary>
public sealed class RequestLogContextMiddlewareTests
{
    [Fact]
    public async Task Incoming_correlation_id_is_echoed_to_response()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers[RequestLogContextMiddleware.CorrelationHeader] = "corr-123";
        var mw = new RequestLogContextMiddleware(_ => Task.CompletedTask);

        await mw.InvokeAsync(ctx);

        ctx.Response.Headers[RequestLogContextMiddleware.CorrelationHeader]
            .ToString().Should().Be("corr-123", "클라이언트가 보낸 CorrelationId를 그대로 공유한다");
    }

    [Fact]
    public async Task Missing_correlation_id_is_generated_by_server()
    {
        var ctx = new DefaultHttpContext();
        var mw = new RequestLogContextMiddleware(_ => Task.CompletedTask);

        await mw.InvokeAsync(ctx);

        ctx.Response.Headers[RequestLogContextMiddleware.CorrelationHeader]
            .ToString().Should().NotBeNullOrWhiteSpace("Header가 없으면 서버가 생성해 돌려준다");
    }

    [Theory]
    [InlineData("corr id with space")]                          // 허용 외 문자(공백)
    [InlineData("корреляция-кириллица")]                        // 비ASCII
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123X")]   // 65자 — 길이 초과
    public async Task Invalid_correlation_id_is_replaced_by_server(string incoming)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers[RequestLogContextMiddleware.CorrelationHeader] = incoming;
        var mw = new RequestLogContextMiddleware(_ => Task.CompletedTask);

        await mw.InvokeAsync(ctx);

        var echoed = ctx.Response.Headers[RequestLogContextMiddleware.CorrelationHeader].ToString();
        echoed.Should().NotBe(incoming, "검증을 통과하지 못한 값은 로그 오염 방지를 위해 서버가 대체한다");
        echoed.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Next_middleware_is_always_invoked()
    {
        var nextCalled = false;
        var ctx = new DefaultHttpContext();
        var mw = new RequestLogContextMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        await mw.InvokeAsync(ctx);

        nextCalled.Should().BeTrue("로그 필드 주입은 요청 흐름을 막지 않는다");
    }
}
