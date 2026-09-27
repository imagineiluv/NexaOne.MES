using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Moq;
using NexaOne.Application.Messaging;
using NexaOne.Application.Query;
using NexaOne.Server.Gateway;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class QueryGatewayNumberParameterTests
{
    [Fact]
    public async Task Json_integer_keeps_its_integer_type_and_fraction_keeps_its_precision()
    {
        QueryDefinition? definition = new("numbers", "SELECT @whole, @fraction", "test", IsPublic: true);
        var registry = new Mock<IQueryRegistry>();
        registry.Setup(r => r.TryGet("numbers", out definition)).Returns(true);
        IDictionary<string, object>? captured = null;
        var dispatcher = new Mock<IRuleDispatcher>();
        dispatcher.Setup(d => d.QueryAsync(It.IsAny<string>(), It.IsAny<IDictionary<string, object>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IDictionary<string, object>, CancellationToken>((_, values, _) => captured = values)
            .ReturnsAsync(Array.Empty<Dictionary<string, object?>>());
        var controller = new QueryGatewayController(dispatcher.Object, registry.Object,
            new ConfigurationBuilder().Build())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        var parameters = JsonSerializer.Deserialize<Dictionary<string, object>>(
            """{"whole":3,"fraction":3.125}""")!;

        (await controller.ExecuteQuery("numbers", parameters, default)).Should().BeOfType<OkObjectResult>();
        captured.Should().NotBeNull();
        captured!["whole"].Should().BeOfType<long>().Which.Should().Be(3);
        captured["fraction"].Should().BeOfType<decimal>().Which.Should().Be(3.125m);
    }
}
