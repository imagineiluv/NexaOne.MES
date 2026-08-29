using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Infrastructure.Equipment;
using NexaFramework.Resource;
using NexaLogic.Plc.Abstractions.Interfaces;
using NexaLogic.Plc.Abstractions.Models;

namespace NexaOne.UnitTests.Fdc;

/// <summary>§3.6.3/§10.4 — NexaLogic IPlcDriver를 NexaFramework IDeviceInterface로
/// 노출하는 프로토콜 중립 어댑터의 lifecycle, 상태 전이, 에러 전파를 검증한다.</summary>
public sealed class PlcDeviceInterfaceTests
{
    private static PlcEndpoint OpcUaEndpoint(string id = "EQ-001") =>
        new(id, PlcDriverKind.OpcUa, "opc.tcp://localhost:4840");

    private static (Mock<IPlcDriver> driver, Mock<IPlcConnection> conn) MockDriver()
    {
        var subscriptions = new Mock<IPlcSubscriptionProvider>();
        subscriptions.Setup(provider => provider.StopAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var conn = new Mock<IPlcConnection>();
        conn.Setup(c => c.OpenAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        conn.Setup(c => c.PingAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        conn.Setup(c => c.CloseAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        conn.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
        conn.SetupGet(c => c.SubscriptionProvider).Returns(subscriptions.Object);

        var driver = new Mock<IPlcDriver>();
        driver.SetupGet(d => d.Kind).Returns(PlcDriverKind.OpcUa);
        driver.SetupGet(d => d.Name).Returns("fake-opcua");
        driver.Setup(d => d.ConnectAsync(It.IsAny<PlcEndpoint>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(conn.Object);
        return (driver, conn);
    }

    [Fact]
    public void Driver_kind_must_match_endpoint_kind()
    {
        var modbus = new PlcEndpoint("EQ-X", PlcDriverKind.ModbusTcp, "127.0.0.1");
        var opcUaDriver = new Mock<IPlcDriver>();
        opcUaDriver.SetupGet(driver => driver.Kind).Returns(PlcDriverKind.OpcUa);
        opcUaDriver.SetupGet(driver => driver.Name).Returns("fake-opcua");

        var act = () => new PlcDeviceInterface("if-1", modbus, opcUaDriver.Object);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*fake-opcua*OpcUa*ModbusTcp*");
    }

    [Fact]
    public void Initial_state_is_created()
    {
        var (driver, _) = MockDriver();
        var sut = new PlcDeviceInterface("if-1", OpcUaEndpoint(), driver.Object);
        sut.State.Should().Be(ResourceState.Created);
        sut.Connection.Should().BeNull("초기화 전에는 연결이 없다");
    }

    [Fact]
    public async Task Initialize_connects_opens_and_becomes_ready()
    {
        var (driver, conn) = MockDriver();
        var sut = new PlcDeviceInterface("if-1", OpcUaEndpoint(), driver.Object);

        await sut.InitializeAsync();

        sut.State.Should().Be(ResourceState.Ready);
        sut.Connection.Should().BeSameAs(conn.Object, "초기화 후 NexaLogic 연결이 노출된다");
        driver.Verify(d => d.ConnectAsync(It.IsAny<PlcEndpoint>(), It.IsAny<CancellationToken>()), Times.Once);
        conn.Verify(c => c.OpenAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Start_pings_and_becomes_running()
    {
        var (driver, conn) = MockDriver();
        var sut = new PlcDeviceInterface("if-1", OpcUaEndpoint(), driver.Object);

        await sut.InitializeAsync();
        await sut.StartAsync();

        sut.State.Should().Be(ResourceState.Running);
        conn.Verify(c => c.PingAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Stop_closes_and_becomes_stopped()
    {
        var (driver, conn) = MockDriver();
        var sut = new PlcDeviceInterface("if-1", OpcUaEndpoint(), driver.Object);

        await sut.InitializeAsync();
        await sut.StartAsync();
        await sut.StopAsync();

        sut.State.Should().Be(ResourceState.Stopped);
        conn.Verify(c => c.CloseAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Stop_stops_subscription_provider_before_closing_transport()
    {
        var lifecycle = new List<string>();
        var subscriptions = new Mock<IPlcSubscriptionProvider>();
        subscriptions.Setup(provider => provider.StopAsync(
                "EQ-001", It.IsAny<CancellationToken>()))
            .Callback(() => lifecycle.Add("subscription"))
            .Returns(Task.CompletedTask);
        var (driver, conn) = MockDriver();
        conn.SetupGet(connection => connection.SubscriptionProvider).Returns(subscriptions.Object);
        conn.Setup(connection => connection.CloseAsync(It.IsAny<CancellationToken>()))
            .Callback(() => lifecycle.Add("transport"))
            .Returns(Task.CompletedTask);
        var sut = new PlcDeviceInterface("if-1", OpcUaEndpoint(), driver.Object);
        await sut.InitializeAsync();

        await sut.StopAsync();

        lifecycle.Should().Equal("subscription", "transport");
        sut.State.Should().Be(ResourceState.Stopped);
    }

    [Fact]
    public async Task Stop_attempts_transport_close_and_aggregates_both_failures()
    {
        var subscriptions = new Mock<IPlcSubscriptionProvider>();
        subscriptions.Setup(provider => provider.StopAsync(
                "EQ-001", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("listener stop failed"));
        var (driver, conn) = MockDriver();
        conn.SetupGet(connection => connection.SubscriptionProvider).Returns(subscriptions.Object);
        conn.Setup(connection => connection.CloseAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transport close failed"));
        var sut = new PlcDeviceInterface("if-1", OpcUaEndpoint(), driver.Object);
        await sut.InitializeAsync();

        var act = () => sut.StopAsync();

        var error = await act.Should().ThrowAsync<AggregateException>();
        error.Which.InnerExceptions.Should().HaveCount(2);
        error.Which.InnerExceptions.Select(exception => exception.Message)
            .Should().Contain(["listener stop failed", "transport close failed"]);
        conn.Verify(connection => connection.CloseAsync(It.IsAny<CancellationToken>()), Times.Once);
        sut.State.Should().Be(ResourceState.Error);
    }

    [Fact]
    public async Task Stop_cancellation_fences_a_hung_provider_and_still_attempts_transport_close()
    {
        var never = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriptions = new Mock<IPlcSubscriptionProvider>();
        subscriptions.Setup(provider => provider.StopAsync(
                "EQ-001", It.IsAny<CancellationToken>()))
            .Returns(never.Task);
        var (driver, conn) = MockDriver();
        conn.SetupGet(connection => connection.SubscriptionProvider).Returns(subscriptions.Object);
        var sut = new PlcDeviceInterface("if-1", OpcUaEndpoint(), driver.Object);
        await sut.InitializeAsync();
        using var deadline = new CancellationTokenSource();

        var stop = sut.StopAsync(deadline.Token);
        deadline.Cancel();
        var error = await Record.ExceptionAsync(
            () => stop.WaitAsync(TimeSpan.FromSeconds(5)));

        error.Should().BeOfType<AggregateException>();
        conn.Verify(connection => connection.CloseAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Start_before_initialize_throws()
    {
        var (driver, _) = MockDriver();
        var sut = new PlcDeviceInterface("if-1", OpcUaEndpoint(), driver.Object);

        var act = () => sut.StartAsync();

        await act.Should().ThrowAsync<InvalidOperationException>("초기화 전 Start는 허용되지 않는다");
    }

    [Fact]
    public async Task Connect_failure_sets_error_state_and_raises_event()
    {
        var driver = new Mock<IPlcDriver>();
        driver.SetupGet(d => d.Kind).Returns(PlcDriverKind.OpcUa);
        driver.SetupGet(d => d.Name).Returns("fake-opcua");
        driver.Setup(d => d.ConnectAsync(It.IsAny<PlcEndpoint>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new TimeoutException("connect timeout"));
        var sut = new PlcDeviceInterface("if-err", OpcUaEndpoint("EQ-9"), driver.Object);

        DeviceErrorEventArgs? captured = null;
        sut.ErrorOccurred += (_, e) => captured = e;

        var act = () => sut.InitializeAsync();

        await act.Should().ThrowAsync<TimeoutException>();
        sut.State.Should().Be(ResourceState.Error);
        captured.Should().NotBeNull("드라이버 오류는 FDC worker가 감독할 수 있게 표준 이벤트로 전파된다");
        captured!.InterfaceName.Should().Be("if-err");
        captured.DeviceName.Should().Be("EQ-9");
    }

    [Fact]
    public async Task Dispose_releases_connection()
    {
        var (driver, conn) = MockDriver();
        var sut = new PlcDeviceInterface("if-1", OpcUaEndpoint(), driver.Object);

        await sut.InitializeAsync();
        await sut.DisposeAsync();

        conn.Verify(c => c.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task SubscribeWithSnapshot_rejects_a_provider_without_atomic_cutover_capability()
    {
        var (driver, conn) = MockDriver();
        conn.SetupGet(candidate => candidate.SubscriptionProvider)
            .Returns(Mock.Of<IPlcSubscriptionProvider>());
        var sut = new PlcDeviceInterface("if-1", OpcUaEndpoint(), driver.Object);
        await sut.InitializeAsync();
        var subscription = new PlcSubscription(
            "sub-1", "EQ-001", ["TEMP01"], TimeSpan.FromSeconds(1));

        var act = () => sut.SubscribeWithSnapshotAsync(
            subscription, _ => Task.CompletedTask);

        await act.Should().ThrowAsync<FdcInterlockRuntimeUnavailableException>()
            .WithMessage("*atomic subscription snapshot capability*");
        conn.Verify(candidate => candidate.PingAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SubscribeWithSnapshot_rejects_an_atomic_provider_without_runtime_health()
    {
        var provider = new Mock<IPlcSubscriptionProvider>();
        provider.As<IPlcAtomicSubscriptionSnapshotProvider>()
            .Setup(candidate => candidate.StartWithSnapshotAsync(
                It.IsAny<PlcEndpoint>(), It.IsAny<PlcSubscription>(),
                It.IsAny<Func<PlcTagChangeEvent, Task>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PlcTagValue("TEMP01", 20m, PlcQuality.Good, DateTimeOffset.UtcNow, "40001")]);
        var (driver, conn) = MockDriver();
        conn.SetupGet(candidate => candidate.SubscriptionProvider).Returns(provider.Object);
        var sut = new PlcDeviceInterface("if-1", OpcUaEndpoint(), driver.Object);
        await sut.InitializeAsync();

        var act = () => sut.SubscribeWithSnapshotAsync(
            new PlcSubscription("sub-1", "EQ-001", ["TEMP01"], TimeSpan.FromSeconds(1)),
            _ => Task.CompletedTask);

        await act.Should().ThrowAsync<FdcInterlockRuntimeUnavailableException>()
            .WithMessage("*runtime health capability*");
    }
}
