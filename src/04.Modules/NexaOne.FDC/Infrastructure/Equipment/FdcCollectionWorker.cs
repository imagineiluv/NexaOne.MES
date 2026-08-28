using Microsoft.Extensions.Hosting;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Domain;
using NexaOne.FDC.Infrastructure.Equipment;
using NexaOne.Infrastructure.Messaging;
using NexaLogic.Plc.Abstractions.Interfaces;

namespace NexaOne.FDC.Infrastructure.Equipment;

/// <summary>
/// FDC 실시간 수집 워커 — 모듈 소유 오케스트레이션(ADR-006 Phase 2).
/// API 측 FdcCollectorHostedService의 수집 오케스트레이션을 미러링하되, best-effort 실시간 통지는 SignalR 직접 호출
/// 대신 <see cref="IMessageBus"/>(server.xml의 messageBus 빈)로 도메인 이벤트를 발행한다. 버스 구독자가
/// 이를 받아 SignalR로 전달한다(ADR-002 §2.5 — 실시간 알림을 '버스 소비'로 일원화).
/// 인터락 액션은 프로젝트별 정책을 위한 불투명 key이며, 필수 IFdcInterlockActionPort adapter가 실제 설비 제어와
/// ack/readback을 완료한 뒤에만 본 워커가 관제 알림을 발행한다.
/// </summary>
/// <remarks>
/// API/웹 타입(IEesHubNotifier·EquipmentChannelStatusRegistry 등)을 일절 참조하지 않는다 — 모듈 경계 보존.
/// 채널 상태 메트릭(§17.5)은 본 워커 범위에서 생략한다(웹 타입 의존 금지가 우선).
/// 실제 PLC 서버 연결을 시도하므로 <c>_enabled = false</c>면 즉시 no-op로 반환한다(연결 시도 안 함).
/// FdcPlcDeviceFactory/IMessageBus는 Spring 부모/모듈 컨텍스트의 빈을 호스트가 주입한다.
/// 본 워커의 이벤트 구독은 관제/UI 알림 전용이다. 운영 인터락의 실제 장치 동작은 프로젝트 action adapter가
/// 소유하며, 물리 safety PLC/STO와 HIL 검증을 대체하지 않는다.
/// 본 워커는 자신이 생성한 PLC 수집 연결만 직접 관리한다. 설비 전체 Machine 시작이나 Auto 모드 전환은
/// equipment orchestration/admission의 소유이며 본 워커가 호출하지 않는다.
/// </remarks>
public sealed class FdcCollectionWorker : BackgroundService
{
    private readonly FdcCollectorService _collector;
    private readonly IFdcEquipmentEndpointRepository _endpointRepo;
    private readonly IFdcParameterRepository _paramRepo;
    private readonly FdcPlcDeviceFactory _deviceFactory;
    private readonly IMessageBus _bus;             // server.xml messageBus 빈 — 도메인 이벤트 발행 백본
    private readonly bool _enabled;                // Worker:Fdc:Enabled 게이트(기본 false)
    private readonly string _topic;                // 발행 토픽(기본 "nexaone.events")
    private readonly TimeSpan _streamFreshnessTimeout;
    private readonly TimeSpan _driverCleanupTimeout;

    internal const string DriverCleanupFailureDataKey = "NexaOne.FDC.DriverCleanupFailure";

    public FdcCollectionWorker(
        FdcCollectorService collector,
        IFdcEquipmentEndpointRepository endpointRepo,
        IFdcParameterRepository paramRepo,
        FdcPlcDeviceFactory deviceFactory,
        IMessageBus bus,
        bool enabled,
        string topic,
        TimeSpan? streamFreshnessTimeout = null,
        TimeSpan? driverCleanupTimeout = null)
    {
        _collector = collector;
        _endpointRepo = endpointRepo;
        _paramRepo = paramRepo;
        _deviceFactory = deviceFactory;
        _bus = bus;
        _enabled = enabled;
        _topic = topic;
        _streamFreshnessTimeout = streamFreshnessTimeout ?? TimeSpan.FromSeconds(30);
        if (_streamFreshnessTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(streamFreshnessTimeout), _streamFreshnessTimeout,
                "PLC stream freshness timeout must be positive.");
        _driverCleanupTimeout = driverCleanupTimeout ?? TimeSpan.FromSeconds(10);
        if (_driverCleanupTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(driverCleanupTimeout), _driverCleanupTimeout,
                "PLC driver cleanup timeout must be positive.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 활성 플래그가 false면 즉시 no-op — PLC 연결을 시도하지 않는다(개발/CI에서 연결 실패 Abort 방지).
        if (!_enabled)
            return;

        _collector.InterlockTriggered += OnInterlockTriggered;
        _collector.InterlockResolved += OnInterlockResolved;
        _collector.AlarmRaised += OnAlarmRaised;
        _collector.AlarmCleared += OnAlarmCleared;
        var permitRevoked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRunPermitRevoked() => permitRevoked.TrySetResult(true);
        _collector.RunPermitRevoked += OnRunPermitRevoked;

        var ownedDevices = new List<PlcDeviceInterface>();
        Exception? primaryFailure = null;
        AggregateException? cleanupFailure = null;
        try
        {
            await RunCollectionAsync(ownedDevices, permitRevoked.Task, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal BackgroundService shutdown; the finally block still closes every owned driver.
        }
        catch (Exception ex)
        {
            _collector.DenyRunPermit();
            primaryFailure = ex;
        }
        finally
        {
            _collector.RunPermitRevoked -= OnRunPermitRevoked;
            _collector.InterlockTriggered -= OnInterlockTriggered;
            _collector.InterlockResolved -= OnInterlockResolved;
            _collector.AlarmRaised -= OnAlarmRaised;
            _collector.AlarmCleared -= OnAlarmCleared;
            _collector.DenyRunPermit();
            var cleanupErrors = await StopAndDisposeReverseAsync(
                ownedDevices,
                _driverCleanupTimeout);
            if (cleanupErrors.Count > 0)
            {
                cleanupFailure = new AggregateException(
                    "One or more FDC-owned PLC drivers failed to stop or dispose cleanly.",
                    cleanupErrors);
                if (primaryFailure is not null)
                {
                    // Preserve the startup/runtime exception as the thrown failure. Cleanup is still visible to
                    // host diagnostics without replacing the causal error that revoked the run permit.
                    primaryFailure.Data[DriverCleanupFailureDataKey] = cleanupFailure;
                    Trace.TraceError(
                        "FDC driver cleanup failed while preserving primary error '{0}': {1}",
                        primaryFailure.GetType().Name,
                        cleanupFailure);
                }
            }
        }

        if (primaryFailure is not null)
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        if (cleanupFailure is not null)
            throw cleanupFailure;
    }

    private async Task RunCollectionAsync(
        List<PlcDeviceInterface> ownedDevices,
        Task permitRevoked,
        CancellationToken stoppingToken)
    {

        var endpoints = await _endpointRepo.GetAllActiveAsync(stoppingToken);
        if (endpoints.Count == 0)
            throw new FdcInterlockRuntimeUnavailableException(
                "FDC collection is enabled but no active equipment endpoint topology is available.");

        var registrations = new List<(
            FdcEquipmentEndpoint Endpoint,
            IReadOnlyList<FdcParameter> Parameters,
            PlcDeviceInterface Device)>();

        if (endpoints.Select(static endpoint => endpoint.Id).Distinct(StringComparer.Ordinal).Count() != endpoints.Count)
            throw new FdcInterlockRuntimeUnavailableException(
                "FDC active endpoint topology contains duplicate endpoint IDs; run permit is denied.");

        foreach (var equipmentEndpoints in endpoints.GroupBy(
                     static endpoint => endpoint.EquipmentId,
                     StringComparer.Ordinal))
        {
            var parameters = (await _paramRepo.GetByEquipmentAsync(equipmentEndpoints.Key, stoppingToken))
                .Where(static parameter => parameter.IsActive)
                .ToArray();
            if (parameters.Length == 0)
                throw new FdcInterlockRuntimeUnavailableException(
                    $"FDC equipment '{equipmentEndpoints.Key}' has no active parameter topology; run permit is denied.");

            var activeEndpointIds = equipmentEndpoints
                .Select(static endpoint => endpoint.Id)
                .ToHashSet(StringComparer.Ordinal);
            var unmapped = parameters
                .Where(parameter => string.IsNullOrWhiteSpace(parameter.EndpointId)
                                    || !activeEndpointIds.Contains(parameter.EndpointId))
                .Select(static parameter => parameter.Id)
                .OrderBy(static parameterId => parameterId, StringComparer.Ordinal)
                .ToArray();
            if (unmapped.Length > 0)
                throw new FdcInterlockRuntimeUnavailableException(
                    $"FDC equipment '{equipmentEndpoints.Key}' has active parameters without exactly one active endpoint mapping: "
                    + $"{string.Join(", ", unmapped)}.");

            foreach (var ep in equipmentEndpoints)
            {
                var mappedParameters = parameters
                    .Where(parameter => string.Equals(parameter.EndpointId, ep.Id, StringComparison.Ordinal))
                    .ToArray();
                if (mappedParameters.Length == 0)
                    throw new FdcInterlockRuntimeUnavailableException(
                        $"FDC active endpoint '{ep.Id}' has no mapped active parameter; run permit is denied.");

                // 구성 오류(미지원 프로토콜·미등록 드라이버·중복 Machine)는 호스트 로그에 원인이
                // 남도록 예외를 전파한다. 엔드포인트를 조용히 건너뛰면 수집 누락을 탐지할 수 없다.
                var device = _deviceFactory.Create(ep);
                registrations.Add((ep, mappedParameters, device));
                ownedDevices.Add(device);
            }
        }

        // Driver 연결 전에 topology, immutable rule snapshot, project action adapter와 모든 durable
        // open EffectId의 ack/readback 재조정을 끝낸다. live 값 확인 전에는 run permit을 게시하지 않는다.
        await _collector.InitializeInterlockRuntimeAsync(
            registrations
                .GroupBy(item => item.Endpoint.EquipmentId, StringComparer.Ordinal)
                .Select(group => new FdcInterlockTopology(
                    group.Key,
                    group.SelectMany(item => item.Parameters)
                        .Select(parameter => parameter.Id)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()))
                .ToArray(),
            stoppingToken);

        // FDC가 소유한 driver만 직접 Ready로 만든다. PlantController.Initialize/Start는 전체 Machine과
        // 운전 모드를 소유하므로 telemetry worker 경계에서 호출하지 않는다.
        foreach (var registration in registrations)
            await registration.Device.InitializeAsync(stoppingToken);

        var startupBuffer = new StartupSampleBuffer(
            (equipmentId, sample) => HandleLiveSampleAsync(equipmentId, sample, stoppingToken));
        foreach (var registration in registrations)
        {
            var ep = registration.Endpoint;
            var subscription = FdcEndpointMapper.ToSubscription(ep, registration.Parameters);
            var snapshot = await registration.Device.SubscribeWithSnapshotAsync(
                subscription,
                sample => startupBuffer.DispatchAsync(ep.EquipmentId, sample),
                stoppingToken);
            await _collector.EvaluateInitialSnapshotAsync(ep.EquipmentId, snapshot, stoppingToken);
        }

        var runtimeHealth = registrations
            .Select(registration => CreateRuntimeHealthRegistration(
                registration.Endpoint,
                registration.Device,
                _streamFreshnessTimeout))
            .ToArray();
        var runtimeHealthGate = new object();
        Exception? listenerTermination = null;
        foreach (var registration in runtimeHealth)
        {
            _ = registration.Completion.ContinueWith(
                completed =>
                {
                    var failure = completed.Exception?.GetBaseException()
                                  ?? new FdcInterlockRuntimeUnavailableException(
                                      $"PLC endpoint '{registration.EndpointId}' subscription listener terminated unexpectedly.");
                    lock (runtimeHealthGate)
                    {
                        listenerTermination ??= failure;
                        _collector.DenyRunPermit();
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        // Atomic provider callbacks are causally later than their returned baseline. They may arrive before
        // StartWithSnapshotAsync returns, so all are replayed in arrival order while the permit remains closed.
        await startupBuffer.DrainPendingAsync(
            (equipmentId, sample) => _collector.OnBufferedStartupTagChangeAsync(
                equipmentId, sample, stoppingToken));

        // Ping/start only the FDC-owned device interfaces while callbacks remain buffered. Once every device is
        // Running, drain the residual queue and publish permit + live callback handoff under the same buffer gate.
        foreach (var registration in registrations)
            await registration.Device.StartAsync(stoppingToken);
        await startupBuffer.DrainAndActivateAsync(
            (equipmentId, sample) => _collector.OnBufferedStartupTagChangeAsync(
                equipmentId, sample, stoppingToken),
            () =>
            {
                lock (runtimeHealthGate)
                {
                    if (listenerTermination is not null)
                        throw new FdcInterlockRuntimeUnavailableException(
                            "A PLC subscription listener terminated before run permit publication.",
                            listenerTermination);
                    EnsureFresh(runtimeHealth);
                    _collector.CompleteInterlockRuntimeInitialization();
                }
            });

        // Do not let BackgroundService complete silently while drivers remain live. Bad quality, action failure,
        // or runtime invalidation revokes the permit and wakes this supervisor, whose finally path closes drivers.
        using var healthCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var frozenStream = MonitorFreshnessAsync(
            runtimeHealth,
            _collector.RetryPendingEffectPersistenceAsync, healthCancellation.Token);
        var completed = await Task.WhenAny(permitRevoked, frozenStream);
        if (ReferenceEquals(completed, frozenStream))
        {
            try
            {
                await frozenStream;
            }
            finally
            {
                _collector.DenyRunPermit();
            }
        }
        else
        {
            healthCancellation.Cancel();
            try
            {
                await frozenStream;
            }
            catch (OperationCanceledException) when (healthCancellation.IsCancellationRequested)
            {
            }
        }
    }

    private static async Task MonitorFreshnessAsync(
        IReadOnlyList<RuntimeHealthRegistration> registrations,
        Func<CancellationToken, Task> retryPendingEffects,
        CancellationToken ct)
    {
        var shortestDeadline = registrations.Min(static registration => registration.FreshnessDeadline);
        var checkTicks = Math.Max(
            TimeSpan.FromMilliseconds(10).Ticks,
            Math.Min(TimeSpan.FromSeconds(1).Ticks, shortestDeadline.Ticks / 4));
        var checkInterval = TimeSpan.FromTicks(checkTicks);
        while (true)
        {
            await Task.Delay(checkInterval, ct);
            EnsureFresh(registrations);
            await retryPendingEffects(ct);
        }
    }

    private static void EnsureFresh(IReadOnlyList<RuntimeHealthRegistration> registrations)
    {
        foreach (var registration in registrations)
            EnsureRuntimeHealthFresh(
                registration.EndpointId,
                registration.Health,
                registration.SubscriptionGeneration,
                registration.FreshnessDeadline);
    }

    internal static TimeSpan CalculateStreamFreshnessDeadline(
        FdcEquipmentEndpoint endpoint,
        TimeSpan additionalGrace)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (additionalGrace <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(additionalGrace), additionalGrace,
                "PLC stream freshness grace must be positive.");

        try
        {
            // Poll completion-to-completion includes the configured sampling delay, one bounded read/reconnect
            // attempt, and the worst configured disconnect recovery backoff. The global setting is retained as
            // callback/scheduler grace instead of incorrectly acting as one fixed deadline for every endpoint.
            var pollingBudgetMs = checked(
                (long)endpoint.SamplingIntervalMs
                + endpoint.PlcSettings.ReadWriteTimeoutMs
                + endpoint.PlcSettings.PollingMaxDisconnectBackoffMs);
            return additionalGrace + TimeSpan.FromMilliseconds(pollingBudgetMs);
        }
        catch (OverflowException ex)
        {
            throw new FdcInterlockRuntimeUnavailableException(
                $"PLC endpoint '{endpoint.Id}' polling freshness budget exceeds the supported duration.", ex);
        }
    }

    internal static void EnsureRuntimeHealthFresh(
        string endpointId,
        IPlcSubscriptionRuntimeHealth health,
        long expectedGeneration,
        TimeSpan freshnessDeadline)
    {
        ArgumentNullException.ThrowIfNull(health);
        if (freshnessDeadline <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(freshnessDeadline));

        if (health.SubscriptionGeneration != expectedGeneration)
            throw new FdcInterlockRuntimeUnavailableException(
                $"PLC endpoint '{endpointId}' subscription generation changed unexpectedly; run permit is denied.");
        if (!health.IsRunning)
            throw new FdcInterlockRuntimeUnavailableException(
                $"PLC endpoint '{endpointId}' subscription is not running; run permit is denied.");

        var completedPollCount = health.CompletedPollCount;
        var elapsed = health.TimeSinceLastCompletedPoll;
        if (health.SubscriptionGeneration != expectedGeneration)
            throw new FdcInterlockRuntimeUnavailableException(
                $"PLC endpoint '{endpointId}' subscription generation changed while checking freshness; run permit is denied.");

        if (completedPollCount <= 0 || elapsed is null || elapsed.Value > freshnessDeadline)
            throw new FdcInterlockRuntimeUnavailableException(
                $"PLC endpoint '{endpointId}' subscription stream is stale; "
                + $"no completed poll was observed within its endpoint deadline of {freshnessDeadline}.");
    }

    private static RuntimeHealthRegistration CreateRuntimeHealthRegistration(
        FdcEquipmentEndpoint endpoint,
        PlcDeviceInterface device,
        TimeSpan additionalGrace)
    {
        var health = device.SubscriptionRuntimeHealth
                     ?? throw new FdcInterlockRuntimeUnavailableException(
                         $"PLC endpoint '{endpoint.Id}' did not publish subscription runtime health.");
        var generation = health.SubscriptionGeneration;
        var completion = health.Completion;
        if (!health.IsRunning || health.SubscriptionGeneration != generation)
            throw new FdcInterlockRuntimeUnavailableException(
                $"PLC endpoint '{endpoint.Id}' subscription health changed during startup; run permit is denied.");

        return new RuntimeHealthRegistration(
            endpoint.Id,
            health,
            generation,
            CalculateStreamFreshnessDeadline(endpoint, additionalGrace),
            completion);
    }

    private async Task HandleLiveSampleAsync(
        string equipmentId,
        FdcTagSample sample,
        CancellationToken stoppingToken)
    {
        try
        {
            await _collector.OnTagChangeAsync(equipmentId, sample, stoppingToken);
        }
        catch
        {
            // NexaLogic providers may log and swallow callback failures. Revoke before rethrowing so the worker
            // supervisor independently closes every owned session instead of remaining silently live.
            _collector.DenyRunPermit();
            throw;
        }
    }

    internal static async Task<IReadOnlyList<Exception>> StopAndDisposeReverseAsync(
        IReadOnlyList<PlcDeviceInterface> devices,
        TimeSpan cleanupTimeout)
    {
        ArgumentNullException.ThrowIfNull(devices);
        if (cleanupTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(cleanupTimeout), cleanupTimeout,
                "PLC driver cleanup timeout must be positive.");

        var errors = new List<Exception>();
        for (var index = devices.Count - 1; index >= 0; index--)
        {
            var device = devices[index];
            var stopFailure = await ExecuteCleanupStepAsync(
                device.InterfaceName,
                "stop",
                cleanupTimeout,
                ct => device.StopAsync(ct));
            if (stopFailure is not null)
                errors.Add(stopFailure);

            var disposeFailure = await ExecuteCleanupStepAsync(
                device.InterfaceName,
                "dispose",
                cleanupTimeout,
                _ => device.DisposeAsync().AsTask());
            if (disposeFailure is not null)
                errors.Add(disposeFailure);
        }

        return errors;
    }

    private static async Task<Exception?> ExecuteCleanupStepAsync(
        string interfaceName,
        string operationName,
        TimeSpan cleanupTimeout,
        Func<CancellationToken, Task> startOperation)
    {
        using var deadline = new CancellationTokenSource(cleanupTimeout);
        Task operation;
        try
        {
            operation = startOperation(deadline.Token);
        }
        catch (Exception ex)
        {
            return new InvalidOperationException(
                $"FDC PLC interface '{interfaceName}' {operationName} failed.", ex);
        }

        try
        {
            await operation.WaitAsync(deadline.Token);
            return null;
        }
        catch (Exception ex) when (deadline.IsCancellationRequested)
        {
            if (!operation.IsCompleted)
                ObserveLateCleanupFault(operation);
            return new TimeoutException(
                $"FDC PLC interface '{interfaceName}' {operationName} exceeded "
                + $"the cleanup deadline of {cleanupTimeout}.",
                ex);
        }
        catch (Exception ex)
        {
            return new InvalidOperationException(
                $"FDC PLC interface '{interfaceName}' {operationName} failed.", ex);
        }
    }

    private static void ObserveLateCleanupFault(Task operation) =>
        _ = operation.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

    // ── 이벤트 → IMessageBus 발행. EventType/Payload는 RealtimeNotificationDispatch가 인식하는 값과 일치시킨다. ──

    private void OnInterlockTriggered(object? sender, FdcInterlockTriggeredEventArgs e) =>
        _ = PublishInterlockNotificationAsync(e);

    private void OnInterlockResolved(object? sender, FdcInterlockResolvedEventArgs e) =>
        // 정상 복귀는 인터락 사실만 통지한다. 실제 설비 상태는 EST/driver readback 소유다.
        _ = PublishSafelyAsync(
            "InterlockResolved",
            e.EquipmentId,
            JsonSerializer.Serialize(new
            {
                e.EffectId,
                e.RuleId,
                e.ParameterId,
                e.Value,
                e.ResolvedAt,
            }));

    private void OnAlarmRaised(object? sender, FdcAlarmRaisedEventArgs e) =>
        _ = PublishSafelyAsync("FdcAlarmRaised", e.EquipmentId, e.Value.ToString());

    private void OnAlarmCleared(object? sender, FdcAlarmClearedEventArgs e) =>
        _ = PublishSafelyAsync("FdcAlarmCleared", e.EquipmentId, "AlarmCleared");

    private async Task PublishInterlockNotificationAsync(FdcInterlockTriggeredEventArgs e)
    {
        // Action은 공통 FDC가 해석하지 않는 프로젝트 정책 payload다. 플러그인/소비자가 실제 정지 여부와
        // 설비 상태 전이를 결정하며, 워커는 정규화된 인터락 발동 사실만 발행한다.
        await PublishSafelyAsync(
            "InterlockTriggered",
            e.EquipmentId,
            JsonSerializer.Serialize(new
            {
                e.EffectId,
                e.Result.RuleId,
                e.ParameterId,
                e.Result.Action,
                e.Result.Message,
                e.Value,
                e.ActionResult.AcknowledgementId,
                e.ActionResult.ReadbackConfirmed,
                e.IsRecovery,
            }));
    }

    /// <summary>관제/UI용 best-effort 버스 알림 1건. EffectId가 direct/outbox 중복 전달의 멱등 키다.
    /// 물리 인터락 action의 성공 여부를 나타내지 않는다.</summary>
    private async Task PublishSafelyAsync(string eventType, string aggregateId, string payload)
    {
        try
        {
            var message = DomainEventMessage.Create(eventType, module: "FDC", aggregateId: aggregateId, payload: payload);
            await _bus.PublishAsync(_topic, message);
        }
        catch
        {
            // 통지 실패는 수집을 멈추지 않는다(버스 미가용·구독자 예외 등).
        }
    }

    private sealed class StartupSampleBuffer
    {
        internal const int Capacity = 4096;
        private readonly object _gate = new();
        private readonly Queue<BufferedSample> _pending = new();
        private readonly Func<string, FdcTagSample, Task> _liveHandler;
        private FdcInterlockRuntimeUnavailableException? _overflow;
        private bool _live;

        public StartupSampleBuffer(Func<string, FdcTagSample, Task> liveHandler) =>
            _liveHandler = liveHandler;

        public Task DispatchAsync(string equipmentId, FdcTagSample sample)
        {
            lock (_gate)
            {
                if (!_live)
                {
                    if (_pending.Count >= Capacity)
                    {
                        _overflow ??= new FdcInterlockRuntimeUnavailableException(
                            $"FDC startup callback buffer exceeded its bounded capacity of {Capacity}; run permit is denied.");
                        return Task.FromException(_overflow);
                    }
                    _pending.Enqueue(new BufferedSample(equipmentId, sample));
                    return Task.CompletedTask;
                }
            }

            return _liveHandler(equipmentId, sample);
        }

        public Task DrainPendingAsync(Func<string, FdcTagSample, Task> startupHandler) =>
            DrainAsync(startupHandler, completeInitialization: null);

        public Task DrainAndActivateAsync(
            Func<string, FdcTagSample, Task> startupHandler,
            Action completeInitialization) =>
            DrainAsync(startupHandler, completeInitialization);

        private async Task DrainAsync(
            Func<string, FdcTagSample, Task> startupHandler,
            Action? completeInitialization)
        {
            while (true)
            {
                BufferedSample? buffered;
                lock (_gate)
                {
                    if (_overflow is not null)
                        throw _overflow;

                    if (_pending.Count == 0)
                    {
                        if (completeInitialization is not null)
                        {
                            // DispatchAsync cannot enqueue while this gate is held. Permit publication and
                            // live transition are therefore one atomic handoff from the callback's view.
                            completeInitialization();
                            _live = true;
                        }
                        return;
                    }

                    buffered = _pending.Dequeue();
                }

                await startupHandler(buffered.EquipmentId, buffered.Sample);
            }
        }

        private sealed record BufferedSample(string EquipmentId, FdcTagSample Sample);
    }

    private sealed record RuntimeHealthRegistration(
        string EndpointId,
        IPlcSubscriptionRuntimeHealth Health,
        long SubscriptionGeneration,
        TimeSpan FreshnessDeadline,
        Task Completion);
}
