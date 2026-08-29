using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NexaOne.Common.Caching;
using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Infrastructure;
using NexaOne.FDC.Infrastructure.Equipment;
using NexaOne.Infrastructure.Messaging;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Fdc;
using NexaDB.Data.Abstractions.Interfaces;
using NexaFramework.Scheduling;
using NexaLogic.Plc.Hosting;

namespace NexaOne.FDC;

/// <summary>
/// FDC 저장소·평가·PLC 수집 그래프를 숨기고 공개 bridge, TRACE source와 worker만 노출하는 조립 진입점입니다.
/// 프로젝트 action adapter는 필수 외부 의존성이며, collector run permit 검증 전에 내부 기본 구현으로 대체하지 않습니다.
/// </summary>
public sealed class Module
{
    private readonly IFdcBridge _fdcBridge;
    private readonly IFdcTraceSource _traceSource;
    private readonly IFdcRuntimeLease _runtimeLease;
    private readonly IRunAdmissionService _runAdmissionService;
    private readonly IHostedService _collectionWorker;
    private readonly IHostedService _retentionWorker;
    private readonly IHostedService _virtualEventWorker;

    /// <summary>
    /// 이전 8-argument binary constructor ABI를 보존한다. TRACE retention이 꺼진 legacy 조립만
    /// 허용하며, 활성화된 구성은 IVT guard가 있는 신규 constructor를 사용해야 한다.
    /// </summary>
    public Module(
        EesDataSource dataSource,
        INexaOneEESDbCapability dialect,
        IConfiguration configuration,
        ICacheService cache,
        IFdcInterlockActionPort actionPort,
        IPlcDriverFactory plcDriverFactory,
        IMessageBus messageBus,
        IRecurringScheduler scheduler)
        : this(
            dataSource,
            dialect,
            configuration,
            cache,
            actionPort,
            plcDriverFactory,
            messageBus,
            scheduler,
            RequireDisabledLegacyRetentionGuard(configuration))
    {
    }

    public Module(
        EesDataSource dataSource,
        INexaOneEESDbCapability dialect,
        IConfiguration configuration,
        ICacheService cache,
        IFdcInterlockActionPort actionPort,
        IPlcDriverFactory plcDriverFactory,
        IMessageBus messageBus,
        IRecurringScheduler scheduler,
        IFdcTraceRetentionGuard traceRetentionGuard)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(actionPort);
        ArgumentNullException.ThrowIfNull(plcDriverFactory);
        ArgumentNullException.ThrowIfNull(messageBus);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(traceRetentionGuard);

        var options = FdcModuleOptions.FromConfiguration(configuration);

        var interlockRules = new FdcInterlockRuleRepository(dataSource);
        var interlockHistory = new FdcInterlockHistoryRepository(dataSource, configuration);
        var alarmConfigs = new FdcAlarmConfigRepository(dataSource);
        var alarmHistory = new FdcAlarmHistoryRepository(dataSource, configuration);
        var parameters = new FdcParameterRepository(dataSource);
        var endpoints = new FdcEquipmentEndpointRepository(dataSource);
        var collectData = new FdcCollectDataRepository(dataSource, dialect);
        _runtimeLease = new FdcRuntimeLease(dataSource);
        var groups = new FdcParameterGroupRepository(dataSource);
        var virtualEvents = new VirtualEventRepository(dataSource, dialect);

        var dataService = new FdcDataService(parameters, collectData, cache);
        var interlockService = new FdcInterlockService(interlockRules, interlockHistory);
        var alarmService = new FdcAlarmService(alarmConfigs, alarmHistory, cache);
        var virtualEventService = new VirtualEventService(virtualEvents);
        var collector = new FdcCollectorService(
            dataService,
            interlockService,
            alarmService,
            actionPort,
            actionTimeout: TimeSpan.FromSeconds(options.InterlockActionTimeoutSeconds),
            requireRuntimeAuthority: true);

        _runAdmissionService = CreateRunAdmissionService(configuration);

        _fdcBridge = new FdcBridge(
            new FdcParameterGroupService(groups),
            alarmService,
            interlockService,
            virtualEventService);
        _traceSource = new FdcTraceSource(collectData);
        _collectionWorker = new FdcCollectionWorker(
            collector,
            endpoints,
            parameters,
            new FdcPlcDeviceFactory(plcDriverFactory),
            messageBus,
            _runtimeLease,
            options.RuntimeLease,
            enabled: options.CollectionEnabled,
            topic: options.EventTopic,
            streamFreshnessTimeout: TimeSpan.FromSeconds(options.RuntimeHealthFreshnessTimeoutSeconds),
            driverCleanupTimeout: TimeSpan.FromSeconds(options.DriverCleanupTimeoutSeconds));
        _retentionWorker = new FdcCollectDataRetentionWorker(
            scheduler,
            collectData,
            traceRetentionGuard,
            enabled: options.RetentionEnabled,
            bindingChangesQuiesced: options.RetentionBindingChangesQuiesced,
            intervalSeconds: options.RetentionIntervalSeconds,
            retentionDays: options.RetentionDays);
        _virtualEventWorker = new VirtualEventEvaluationWorker(
            virtualEventService,
            enabled: options.VirtualEventEnabled,
            intervalSeconds: options.VirtualEventIntervalSeconds);
    }

    private static IFdcTraceRetentionGuard RequireDisabledLegacyRetentionGuard(
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.GetValue("Worker:Fdc:Retention:Enabled", false))
        {
            throw new InvalidOperationException(
                "FDC TRACE retention cannot be enabled through the legacy Module constructor without an IVT retention guard.");
        }

        return DisabledFdcTraceRetentionGuard.Instance;
    }

    public IFdcBridge GetFdcBridge() => _fdcBridge;
    public IFdcTraceSource GetTraceSource() => _traceSource;
    public IFdcRuntimeLease GetRuntimeLease() => _runtimeLease;
    public IRunAdmissionService GetRunAdmissionService() => _runAdmissionService;
    public IHostedService GetCollectionWorker() => _collectionWorker;
    public IHostedService GetRetentionWorker() => _retentionWorker;
    public IHostedService GetVirtualEventWorker() => _virtualEventWorker;

    internal static IRunAdmissionService CreateRunAdmissionService(IConfiguration configuration)
    {
        FdcModuleOptions.EnsureRunAdmissionUnavailable(configuration);
        return DisabledRunAdmissionService.Instance;
    }
}

/// <summary>
/// Spring XML에 실행 정책 상수를 다시 노출하지 않도록 FDC worker 설정을 한곳에서 정규화합니다.
/// 일반 정리 주기는 보수적인 최소값으로 제한하고, 안전 관련 action/freshness timeout의 0 이하는 기동을 거부합니다.
/// 활성화 키가 없으면 모든 worker는 OFF입니다.
/// </summary>
internal sealed record FdcModuleOptions(
    bool CollectionEnabled,
    string EventTopic,
    bool RetentionEnabled,
    bool RetentionBindingChangesQuiesced,
    int RetentionIntervalSeconds,
    int RetentionDays,
    bool VirtualEventEnabled,
    int VirtualEventIntervalSeconds,
    int InterlockActionTimeoutSeconds,
    int RuntimeHealthFreshnessTimeoutSeconds,
    int DriverCleanupTimeoutSeconds,
    FdcLeaseOptions RuntimeLease,
    RunAdmissionOptions RunAdmission)
{
    private const int MaximumOwnerIdLength = 100;
    private static readonly string ProcessOwnerSuffix =
        $":{Environment.ProcessId:x}:{Guid.NewGuid():N}";

    private const string DefaultEventTopic = "nexaone.events";

    public static FdcModuleOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        EnsureRunAdmissionUnavailable(configuration);

        var collectionEnabled = configuration.GetValue("Worker:Fdc:Enabled", false);

        return new FdcModuleOptions(
            CollectionEnabled: collectionEnabled,
            EventTopic: FirstNonBlank(
                configuration["Worker:Fdc:Topic"],
                configuration["Events:Outbox:Topic"],
                DefaultEventTopic),
            RetentionEnabled: configuration.GetValue("Worker:Fdc:Retention:Enabled", false),
            RetentionBindingChangesQuiesced: configuration.GetValue(
                "Worker:Fdc:Retention:BindingChangesQuiesced", false),
            RetentionIntervalSeconds: PositiveOrMinimum(
                configuration.GetValue("Worker:Fdc:Retention:IntervalSeconds", 86_400),
                minimum: 60),
            RetentionDays: PositiveOrMinimum(
                configuration.GetValue("Worker:Fdc:Retention:RetentionDays", 30),
                minimum: 1),
            VirtualEventEnabled: configuration.GetValue("Worker:Fdc:VirtualEvent:Enabled", false),
            VirtualEventIntervalSeconds: PositiveOrMinimum(
                configuration.GetValue("Worker:Fdc:VirtualEvent:IntervalSeconds", 30),
                minimum: 5),
            InterlockActionTimeoutSeconds: RequiredPositive(
                configuration, "Worker:Fdc:InterlockActionTimeoutSeconds", 10),
            RuntimeHealthFreshnessTimeoutSeconds: RequiredPositive(
                configuration, "Worker:Fdc:RuntimeHealth:FreshnessTimeoutSeconds", 30),
            DriverCleanupTimeoutSeconds: RequiredPositive(
                configuration, "Worker:Fdc:DriverCleanupTimeoutSeconds", 10),
            RuntimeLease: ReadRuntimeLease(configuration, collectionEnabled),
            RunAdmission: ReadRunAdmission(configuration));
    }

    internal static void EnsureRunAdmissionUnavailable(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.GetValue("RunAdmission:Enabled", false))
        {
            throw new InvalidOperationException(
                "RunAdmission:Enabled=true is not supported until a durable shared request ledger, "
                + "per-client/equipment quotas, and an HA routing contract are implemented.");
        }
    }

    private static RunAdmissionOptions ReadRunAdmission(IConfiguration configuration)
    {
        var options = new RunAdmissionOptions(
            TimeSpan.FromSeconds(RequiredPositive(
                configuration, "Worker:Fdc:RunAdmission:KeepAliveLeaseSeconds", 6)),
            TimeSpan.FromSeconds(RequiredPositive(
                configuration, "Worker:Fdc:RunAdmission:HardLeaseSeconds", 43_200)),
            TimeSpan.FromSeconds(RequiredPositive(
                configuration, "Worker:Fdc:RunAdmission:TombstoneRetentionSeconds", 86_400)),
            RequiredPositive(
                configuration, "Worker:Fdc:RunAdmission:MaxTombstones", 100_000));
        RunAdmissionOptions.Validate(options);
        return options;
    }

    private static FdcLeaseOptions ReadRuntimeLease(
        IConfiguration configuration,
        bool collectionEnabled)
    {
        if (!collectionEnabled)
        {
            return new FdcLeaseOptions(
                "disabled",
                new string('0', 64),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(10));
        }

        var ownerPrefix = configuration["Worker:Fdc:Ownership:OwnerId"]?.Trim();
        if (string.IsNullOrWhiteSpace(ownerPrefix))
            throw new InvalidOperationException(
                "FDC setting 'Worker:Fdc:Ownership:OwnerId' is required as a deployment-instance prefix when collection is enabled.");
        var maximumPrefixLength = MaximumOwnerIdLength - ProcessOwnerSuffix.Length;
        var ownerId = string.Concat(
            ownerPrefix.AsSpan(0, Math.Min(ownerPrefix.Length, maximumPrefixLength)),
            ProcessOwnerSuffix);

        var configDigest = configuration["Worker:Fdc:Ownership:ConfigRevisionSha256"]?.Trim();
        if (configDigest is null
            || configDigest.Length != 64
            || configDigest.Any(static character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException(
                "FDC setting 'Worker:Fdc:Ownership:ConfigRevisionSha256' must be a 64-character SHA-256 hexadecimal digest when collection is enabled.");

        var durationSeconds = RequiredPositive(
            configuration, "Worker:Fdc:Ownership:LeaseDurationSeconds", 30);
        var renewSeconds = RequiredPositive(
            configuration, "Worker:Fdc:Ownership:RenewIntervalSeconds", 10);
        if (durationSeconds < 3 || durationSeconds > 86_400 || renewSeconds > durationSeconds / 3)
            throw new InvalidOperationException(
                "FDC ownership lease duration must be 3..86400 seconds and renew interval must be no more than one third of it.");

        return new FdcLeaseOptions(
            ownerId,
            configDigest.ToLowerInvariant(),
            TimeSpan.FromSeconds(durationSeconds),
            TimeSpan.FromSeconds(renewSeconds));
    }

    private static int PositiveOrMinimum(int value, int minimum) => Math.Max(value, minimum);

    private static int RequiredPositive(IConfiguration configuration, string key, int defaultValue)
    {
        var value = configuration.GetValue(key, defaultValue);
        if (value <= 0)
            throw new InvalidOperationException($"FDC setting '{key}' must be positive.");
        return value;
    }

    private static string FirstNonBlank(params string?[] candidates) =>
        candidates
            .Select(static candidate => candidate?.Trim())
            .First(static candidate => !string.IsNullOrWhiteSpace(candidate))!;
}
