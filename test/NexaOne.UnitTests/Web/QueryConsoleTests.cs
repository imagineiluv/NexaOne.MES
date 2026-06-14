using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.Web.Pages.LowCode;
using NexaOne.Web.Services.Api;

namespace NexaOne.UnitTests.Web;

/// <summary>저코드 쿼리 콘솔(QueryConsole) — 쿼리 게이트웨이(ExecuteQueryAsync)를 호출해 동적 행을 렌더하는지 검증.</summary>
public sealed class QueryConsoleTests
{
    [Fact]
    public void Renders_dynamic_rows_from_query_gateway()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;   // NexaUI 입력 컴포넌트의 JS 상호작용 허용(렌더만 검증)

        var api = new Mock<IApiClient>();
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["PLANT_ID"] = "P-1", ["PLANT_NAME"] = "Plant One" },
            new() { ["PLANT_ID"] = "P-2", ["PLANT_NAME"] = "Plant Two" },
        };
        api.Setup(a => a.ExecuteQueryAsync(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(rows);
        ctx.Services.AddSingleton(api.Object);

        var cut = ctx.RenderComponent<QueryConsole>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("PLANT_ID").And.Contain("Plant One").And.Contain("Plant Two");
            cut.FindAll("table tbody tr").Count.Should().Be(2, "동적 행 2건이 표로 렌더되어야 한다");
        }, TimeSpan.FromSeconds(2));

        // 진입 시 기본 쿼리 ID로 게이트웨이를 호출한다(저코드 경로 end-to-end의 UI측).
        api.Verify(a => a.ExecuteQueryAsync("MDM.PlantList", It.IsAny<object?>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }
}
