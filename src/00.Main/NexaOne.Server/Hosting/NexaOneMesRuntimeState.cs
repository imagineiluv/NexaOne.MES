using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NexaOne.Infrastructure.Messaging;
using NexaOne.Infrastructure.Persistence;
using NexaOne.Server.Realtime;
using NexaOne.ServiceContracts;
using NexaOne.ServiceContracts.Pom;
using NexaFramework;
using NexaFramework.Scheduling;
using NexaFramework.Utils;

namespace NexaOne.Server;

/// <summary>
/// Spring 모듈 컨텍스트와 모듈 워커의 전체 수명주기를 소유한다.
/// 생성자는 의도적으로 부작용이 없으며, Generic Host가 시작되고 <c>WebApplication.Build()</c>가
/// 성공한 뒤에만 실제 컨텍스트를 만든다.
/// </summary>
internal sealed class NexaOneMesRuntimeState : IDisposable
{
    private readonly ApplicationServer _server;
    private readonly IConfiguration _configuration;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly List<IHostedService> _startedWorkers = new();
    private readonly List<Task> _backgroundServiceMonitors = new();
    private IReadOnlyList<IHostedService> _moduleWorkers = Array.Empty<IHostedService>();
    private IReadOnlyList<string> _loadedServices = Array.Empty<string>();
    private IReadOnlyList<NexaOneModuleBridgeRuntimeInfo> _loadedBridges = Array.Empty<NexaOneModuleBridgeRuntimeInfo>();
    private string? _temporaryConfigPath;
    private int _pipelineConfigured;
    private int _disposed;
    private bool _moduleContextsCreated;
    private bool _initialized;
    private bool _started;
    private bool _stopWorkersConcurrently;
    private int _workerCount;

    /// <summary>지연 초기화할 MES 런타임 상태를 생성한다.</summary>
    public NexaOneMesRuntimeState(
        ApplicationServer server,
        IConfiguration configuration,
        bool modulesEnabled)
    {
        _server = server;
        _configuration = configuration;
        ModulesEnabled = modulesEnabled;
    }

    /// <summary>이미 검색된 워커를 사용하는 수명주기 단위 테스트용 런타임 상태를 생성한다.</summary>
    internal NexaOneMesRuntimeState(
        ApplicationServer server,
        IConfiguration configuration,
        IReadOnlyList<IHostedService> initializedWorkers)
        : this(server, configuration, modulesEnabled: true)
    {
        _moduleWorkers = initializedWorkers;
        _workerCount = initializedWorkers.Count;
        _initialized = true;
    }

    /// <summary>Spring 서버 및 모듈 Bean에 접근하는 애플리케이션 서버다.</summary>
    public ApplicationServer Server => _server;

    /// <summary>설정에서 MES Spring 모듈 사용이 활성화되었는지 나타낸다.</summary>
    public bool ModulesEnabled { get; }

    /// <summary>성공적으로 로드된 MES 서비스 모듈 이름 목록이다.</summary>
    public IReadOnlyList<string> LoadedServices => _loadedServices;

    /// <summary>Spring 컨텍스트에서 검색한 백그라운드 워커 수다.</summary>
    public int WorkerCount => _workerCount;

    /// <summary>Spring contexts and their beans are available, even if module workers have not started yet.</summary>
    internal bool IsInitialized => Volatile.Read(ref _initialized) && Volatile.Read(ref _disposed) == 0;

    /// <summary>Spring 컨텍스트와 모듈 워커의 시작 장벽이 완료됐는지 나타낸다.</summary>
    internal bool IsStarted => Volatile.Read(ref _started) && Volatile.Read(ref _disposed) == 0;

    /// <summary>계약 catalog와 Spring Bean의 타입 일치까지 검증된 모듈 Bridge 목록이다.</summary>
    public IReadOnlyList<NexaOneModuleBridgeRuntimeInfo> LoadedBridges => _loadedBridges;

    /// <summary>HTTP 파이프라인이 한 번만 구성되도록 중복 호출을 검사한다.</summary>
    public void MarkPipelineConfigured()
    {
        if (Interlocked.Exchange(ref _pipelineConfigured, 1) != 0)
            throw new InvalidOperationException("UseNexaOneMes() can only be called once for an application.");
    }

    /// <summary>
    /// Gateway 스키마를 준비하고, 활성화된 경우 Spring 서버와 MES 서비스 컨텍스트를 생성한다.
    /// </summary>
    public async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_initialized) return;

            // 운영 보안 강화는 모듈 OFF 호스트에서도 바로 다음 단계에 실행된다.
            // 따라서 Spring과 무관하게 Gateway SQLite 스키마를 먼저 준비해 SYS_USER를 보장한다.
            EnsureGatewaySqliteSchemaIfConfigured(_configuration);

            if (!ModulesEnabled)
            {
                Console.WriteLine(
                    "[NexaOne.Server] Server:Modules:Enabled=false — 웹 셸만 기동(플러그인/워커 비활성).");
                _initialized = true;
                return;
            }

            InitializeModules(services);
            _initialized = true;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// 검색된 모듈 워커를 HostOptions의 직렬/동시 시작 정책에 따라 시작하고 오류 감시를 연결한다.
    /// </summary>
    public async Task StartWorkersAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_started) return;
            if (!_initialized)
                throw new InvalidOperationException("InitializeAsync must complete before module workers are started.");

            var hostOptions = services.GetService<IOptions<HostOptions>>()?.Value ?? new HostOptions();
            _stopWorkersConcurrently = hostOptions.ServicesStopConcurrently;

            if (hostOptions.ServicesStartConcurrently)
            {
                // Generic Host와 동일하게 시작을 시도한 모든 워커를 롤백 대상으로 기록하고,
                // 여러 시작 오류는 하나의 AggregateException에 보존한다.
                _startedWorkers.AddRange(_moduleWorkers);
                var startTasks = _moduleWorkers
                    .Select(worker => StartAndSuperviseWorkerAsync(
                        worker, services, hostOptions, cancellationToken))
                    .ToArray();
                var allStarts = Task.WhenAll(startTasks);
                try
                {
                    await allStarts.ConfigureAwait(false);
                }
                catch when (allStarts.Exception is not null)
                {
                    throw new AggregateException(
                        "One or more MES module workers failed to start.",
                        allStarts.Exception.InnerExceptions);
                }
            }
            else
            {
                foreach (var worker in _moduleWorkers)
                {
                    // StartAsync가 일부 작업 후 실패할 수도 있으므로 await 전에 종료 대상에 포함한다.
                    _startedWorkers.Add(worker);
                    await StartAndSuperviseWorkerAsync(
                        worker, services, hostOptions, cancellationToken).ConfigureAwait(false);
                }
            }

            _started = true;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// 시작 실패 시 이미 시작한 워커와 모듈 컨텍스트를 정리하되 최초 시작 예외를 변경하지 않는다.
    /// </summary>
    public async Task AbortStartupAsync(Exception primaryError)
    {
        ArgumentNullException.ThrowIfNull(primaryError);

        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var cleanupErrors = await StopWorkersCollectErrorsAsync(CancellationToken.None)
                .ConfigureAwait(false);
            cleanupErrors.AddRange(CleanupModulesCollectErrors());
            foreach (var cleanupError in cleanupErrors)
                ReportCleanupFailure("startup rollback", cleanupError, primaryError);
        }
        catch (Exception cleanupError)
        {
            // 정리 코드 자체가 예상 밖으로 실패해도 호출자가 받아야 할 최초 시작 예외를 보존한다.
            ReportCleanupFailure("startup rollback", cleanupError, primaryError);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// 시작된 워커를 역순으로 멈추고 모든 Spring/임시 설정 자원을 정리한다.
    /// 가능한 정리를 모두 시도한 뒤 실패가 있으면 집계 예외를 발생시킨다.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // 종료 요청 토큰이 이미 취소되어도 게이트 획득과 정리 시도 자체는 생략하지 않는다.
        // 토큰은 개별 워커 StopAsync에 전달해 워커별 종료 제한은 존중한다.
        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0) return;

            var cleanupErrors = await StopWorkersCollectErrorsAsync(cancellationToken)
                .ConfigureAwait(false);
            cleanupErrors.AddRange(CleanupModulesCollectErrors());
            if (cleanupErrors.Count > 0)
            {
                throw new AggregateException(
                    "One or more MES module resources failed to stop cleanly.",
                    cleanupErrors);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>호스트 시작 이후 지정 Spring 모듈의 Bean을 계약 인터페이스로 반환한다.</summary>
    /// <typeparam name="TContract">호스트와 모듈이 공유하는 Bridge 계약 형식이다.</typeparam>
    public TContract GetBridge<TContract>(string module, string beanName)
        where TContract : class
    {
        return (TContract)GetBridge(typeof(TContract), module, beanName);
    }

    /// <summary>동적으로 발견한 계약 형식에 맞는 Spring 모듈 Bean을 반환한다.</summary>
    public object GetBridge(Type contractType, string module, string beanName)
    {
        ArgumentNullException.ThrowIfNull(contractType);
        EnsureStarted();

        var bean = _server.GetBean(module, beanName);
        return contractType.IsInstanceOfType(bean)
            ? bean
            : throw BridgeTypeMismatch(contractType, module, beanName, bean.GetType());
    }

    /// <summary>호스트 시작 이후 Spring 루트 컨텍스트가 소유한 공용 스케줄러를 반환한다.</summary>
    public IRecurringScheduler GetScheduler()
    {
        EnsureStarted();
        return _server.GetServerBean("quartzScheduler") as IRecurringScheduler
            ?? throw new InvalidOperationException("Spring server bean 'quartzScheduler' is not an IRecurringScheduler.");
    }

    /// <summary>
    /// Spring context 초기화 직후 필수 driver를 검증할 수 있도록 루트 Bean을 반환한다.
    /// 업무 Bridge와 scheduler는 worker 시작 장벽을 통과한 별도 접근 경로를 계속 사용한다.
    /// </summary>
    internal TContract GetInitializedServerBean<TContract>(string beanName)
        where TContract : class
    {
        EnsureInitialized();
        return _server.GetServerBean<TContract>(beanName);
    }

    /// <summary>동기 Dispose 경로에서 MES 모듈 정리를 시도하고 정리 오류는 진단 로그로 남긴다.</summary>
    public void DisposeModules()
    {
        try
        {
            StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception cleanupError)
        {
            // Dispose는 시작/빌드 중 이미 전파 중인 예외를 정리 오류로 덮어쓰면 안 된다.
            ReportCleanupFailure("runtime shutdown", cleanupError, primaryError: null);
        }
    }

    /// <inheritdoc />
    public void Dispose() => DisposeModules();

    /// <summary>
    /// BackgroundService 실행 작업의 지연 실패를 관찰하고 HostOptions 정책에 따라 호스트 중지를 요청한다.
    /// </summary>
    internal static async Task ObserveBackgroundServiceAsync(
        Task executeTask,
        Type workerType,
        IHostApplicationLifetime? lifetime,
        BackgroundServiceExceptionBehavior behavior,
        ILogger logger)
    {
        try
        {
            await executeTask.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (error is OperationCanceledException
                && lifetime?.ApplicationStopping.IsCancellationRequested == true)
            {
                return;
            }

            logger.LogError(error, "Module BackgroundService {WorkerType} failed.", workerType.FullName);
            if (behavior == BackgroundServiceExceptionBehavior.StopHost)
            {
                logger.LogCritical(
                    "Stopping the host because a module BackgroundService failed and " +
                    "HostOptions.BackgroundServiceExceptionBehavior is StopHost.");
                lifetime?.StopApplication();
            }
        }
    }

    /// <summary>워커를 시작하고 BackgroundService인 경우 실행 작업의 종료 감시를 등록한다.</summary>
    private async Task StartAndSuperviseWorkerAsync(
        IHostedService worker,
        IServiceProvider services,
        HostOptions hostOptions,
        CancellationToken cancellationToken)
    {
        await worker.StartAsync(cancellationToken).ConfigureAwait(false);
        if (worker is not BackgroundService backgroundService
            || backgroundService.ExecuteTask is not { } executeTask)
        {
            return;
        }

        var lifetime = services.GetService<IHostApplicationLifetime>();
        var logger = services.GetService<ILogger<NexaOneMesRuntimeState>>()
            ?? NullLogger<NexaOneMesRuntimeState>.Instance;
        var monitor = ObserveBackgroundServiceAsync(
            executeTask,
            worker.GetType(),
            lifetime,
            hostOptions.BackgroundServiceExceptionBehavior,
            logger);
        lock (_backgroundServiceMonitors) _backgroundServiceMonitors.Add(monitor);
    }

    internal static IHostedService? CreateRealtimeSubscriber(
        IMessageBus messageBus,
        IConfiguration configuration,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(messageBus);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(services);

        var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
        var screenRefresh = services.GetService<ScreenRefreshNotifier>();
        var alertFeed = services.GetService<RealtimeAlertFeed>();

        if (messageBus is InMemoryMessageBus inMemoryBus)
            return new InMemoryBusSubscriberService(inMemoryBus, scopeFactory, screenRefresh, alertFeed);

        if (messageBus is not KafkaMessageBus kafkaBus)
            return null;

        var configuredTopics = configuration.GetSection("Kafka:Topics")
            .GetChildren()
            .Select(section => section.Value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var configuredDefaultTopic = configuration["Events:Outbox:Topic"]?.Trim();
        var defaultTopic = string.IsNullOrWhiteSpace(configuredDefaultTopic)
            ? "nexaone.events"
            : configuredDefaultTopic;
        var topics = configuredTopics.Length > 0
            ? configuredTopics
            : new[] { defaultTopic };
        var configuredGroupId = configuration["Kafka:GroupId"]?.Trim();
        var options = new KafkaConsumerOptions
        {
            GroupId = string.IsNullOrWhiteSpace(configuredGroupId) ? "nexaone-mes" : configuredGroupId,
            Topics = topics,
        };
        var dispatcher = new RealtimeBusMessageDispatcher(scopeFactory, screenRefresh, alertFeed);
        var logger = services.GetService<ILogger<KafkaConsumerService>>()
            ?? NullLogger<KafkaConsumerService>.Instance;
        return new KafkaConsumerService(
            kafkaBus,
            options,
            dispatcher.DispatchAsync,
            logger);
    }

    /// <summary>
    /// 서버 Spring 컨텍스트와 배포별 애플리케이션 매니페스트의 서비스 컨텍스트를 순서대로 생성하고 HostedService를 수집한다.
    /// </summary>
    private void InitializeModules(IServiceProvider services)
    {
        var springConfig = ServerSpringConfigResolver.Resolve(_configuration);
        springConfig = UnifySqliteDataSourceIfConfigured(
            springConfig, _configuration, out _temporaryConfigPath);
        NexaOne.Common.Configuration.SpringHostConfiguration.Root = _configuration;

        var sqliteConnectionString = EnsureSqliteSchemaIfConfigured(springConfig);

        var serverContext = _server.CreateServer(new[] { springConfig });
        _moduleContextsCreated = true;
        Console.WriteLine("[NexaOne.Server] Server context initialized.");

        var workers = new List<IHostedService>();
        workers.AddRange(serverContext.GetObjectsOfType(typeof(IHostedService)).Values.Cast<IHostedService>());
        var projectionRuntimes = serverContext
            .GetObjectsOfType(typeof(IWorkScopeProjectionRuntime))
            .Values
            .Cast<IWorkScopeProjectionRuntime>()
            .ToList();
        var sqliteSchemaContributions = serverContext
            .GetObjectsOfType(typeof(ISqliteSchemaContribution))
            .Values
            .Cast<ISqliteSchemaContribution>()
            .ToList();

        var loadedServices = new List<string>();
        var applicationManifest = ServerApplicationManifestResolver.Resolve(_configuration);
        var document = XDomUtility.Load(applicationManifest);
        var root = XDomUtility.GetRoot(document);
        var serviceElements = XDomUtility.GetElement(root, "Services");
        var splitOptions = StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries;
        foreach (var service in XDomUtility.GetElements(serviceElements, "Service"))
        {
            var name = service.Attribute("name")?.Value
                ?? throw new InvalidOperationException("Service element missing 'name' attribute.");
            var configFiles = (service.Attribute("configFiles")?.Value
                ?? throw new InvalidOperationException($"Service '{name}' missing 'configFiles' attribute."))
                .Split(';', splitOptions);
            var classPaths = (service.Attribute("classPaths")?.Value
                ?? throw new InvalidOperationException($"Service '{name}' missing 'classPaths' attribute."))
                .Split(';', splitOptions);

            var context = _server.AddService(name, configFiles, classPaths);
            loadedServices.Add(name);
            workers.AddRange(context.GetObjectsOfType(typeof(IHostedService)).Values.Cast<IHostedService>());
            projectionRuntimes.AddRange(context
                .GetObjectsOfType(typeof(IWorkScopeProjectionRuntime))
                .Values
                .Cast<IWorkScopeProjectionRuntime>());
            sqliteSchemaContributions.AddRange(context
                .GetObjectsOfType(typeof(ISqliteSchemaContribution))
                .Values
                .Cast<ISqliteSchemaContribution>());
            Console.WriteLine($"[NexaOne.Server] Service '{name}' registered ({classPaths.Length} module(s)).");
        }

        ValidateWorkScopeProjectionRuntime(
            _configuration.GetValue("Worker:Pom:WorkScopeProjection:Enabled", false),
            projectionRuntimes,
            workers);

        if (sqliteConnectionString is not null)
            SqliteSchemaInitializer.ApplyContributions(sqliteConnectionString, sqliteSchemaContributions);

        var distinctWorkers = workers.Distinct().ToList();
        _workerCount = distinctWorkers.Count;
        _loadedServices = loadedServices.AsReadOnly();

        // 모든 선언형 Bridge를 HTTP 요청 전에 실제 Spring Bean과 대조한다. 신규 계약의 XML 누락이나
        // plugin ALC 계약 중복은 첫 API 호출까지 숨기지 않고 호스트 시작 단계에서 실패시킨다.
        _loadedBridges = ValidateModuleBridges(
            services.GetRequiredService<INexaModuleBridgeCatalog>(),
            loadedServices);

        if (_server.GetServerBean("messageBus") is IMessageBus messageBus
            && CreateRealtimeSubscriber(messageBus, _configuration, services) is { } subscriber)
            distinctWorkers.Add(subscriber);

        _moduleWorkers = distinctWorkers.AsReadOnly();
        Console.WriteLine($"[NexaOne.Server] {_workerCount} background worker(s) discovered.");
    }

    /// <summary>
    /// Validates the optional WorkScope projection feature across Spring plugin ALC boundaries.
    /// An enabled deployment must expose exactly one Default-ALC marker and that exact object must
    /// participate in the hosted-service lifecycle. Disabled POM-only deployments may expose none.
    /// </summary>
    internal static void ValidateWorkScopeProjectionRuntime(
        bool enabled,
        IEnumerable<IWorkScopeProjectionRuntime> runtimes,
        IEnumerable<IHostedService> workers)
    {
        ArgumentNullException.ThrowIfNull(runtimes);
        ArgumentNullException.ThrowIfNull(workers);

        var distinctRuntimes = runtimes
            .Distinct(ReferenceEqualityComparer.Instance)
            .ToList();
        if (distinctRuntimes.Count > 1)
        {
            throw new InvalidOperationException(
                "WorkScope projection composition exposed more than one " +
                $"{nameof(IWorkScopeProjectionRuntime)} marker. Configure exactly one application runtime.");
        }

        if (distinctRuntimes.Count == 0)
        {
            if (enabled)
            {
                throw new InvalidOperationException(
                    "Worker:Pom:WorkScopeProjection:Enabled=true requires exactly one " +
                    $"{nameof(IWorkScopeProjectionRuntime)} marker. Add the project policy and " +
                    "config/modules/pom-projection.xml to the POM service manifest.");
            }

            return;
        }

        var runtime = distinctRuntimes[0];
        var matchingWorkers = workers
            .Distinct(ReferenceEqualityComparer.Instance)
            .Where(worker => ReferenceEquals(worker, runtime))
            .ToList();
        if (matchingWorkers.Count != 1)
        {
            throw new InvalidOperationException(
                $"The single {nameof(IWorkScopeProjectionRuntime)} marker must be the same object " +
                "as exactly one discovered IHostedService. Check pom-projection.xml factory exports.");
        }
    }

    /// <summary>
    /// 시작 역순으로 모든 워커의 종료를 시도하고, 중단하지 않고 각 실패를 수집한다.
    /// </summary>
    private async Task<List<Exception>> StopWorkersCollectErrorsAsync(CancellationToken cancellationToken)
    {
        List<Exception> errors;
        if (_stopWorkersConcurrently)
        {
            // 동시 종료에서도 StopAsync 호출 자체는 역순으로 만들어 의존성 해제 의도를 보존한다.
            var stopResults = await Task.WhenAll(_startedWorkers.AsEnumerable().Reverse()
                .Select(worker => TryStopWorkerAsync(worker, cancellationToken)))
                .ConfigureAwait(false);
            errors = stopResults.Where(error => error is not null).Cast<Exception>().ToList();
        }
        else
        {
            errors = new List<Exception>();
            for (var index = _startedWorkers.Count - 1; index >= 0; index--)
            {
                if (await TryStopWorkerAsync(_startedWorkers[index], cancellationToken).ConfigureAwait(false)
                    is { } error)
                {
                    errors.Add(error);
                }
            }
        }

        _startedWorkers.Clear();
        lock (_backgroundServiceMonitors) _backgroundServiceMonitors.Clear();
        return errors;
    }

    /// <summary>개별 워커 종료 오류를 다른 워커 정리에 영향을 주지 않는 값으로 변환한다.</summary>
    private static async Task<Exception?> TryStopWorkerAsync(
        IHostedService worker,
        CancellationToken cancellationToken)
    {
        try
        {
            await worker.StopAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception cleanupError)
        {
            return new InvalidOperationException(
                $"Module worker {worker.GetType().FullName} failed to stop.",
                cleanupError);
        }
    }

    /// <summary>
    /// Spring 컨텍스트, 전역 설정 참조 및 임시 XML을 각각 정리하고 모든 실패를 수집한다.
    /// </summary>
    private List<Exception> CleanupModulesCollectErrors()
    {
        var errors = new List<Exception>();
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return errors;

        if (_moduleContextsCreated)
        {
            try
            {
                _server.Dispose();
            }
            catch (Exception cleanupError)
            {
                errors.Add(new InvalidOperationException("Spring module contexts failed to dispose.", cleanupError));
            }
        }

        if (ReferenceEquals(NexaOne.Common.Configuration.SpringHostConfiguration.Root, _configuration))
            NexaOne.Common.Configuration.SpringHostConfiguration.Root = null;

        if (!string.IsNullOrWhiteSpace(_temporaryConfigPath))
        {
            try
            {
                File.Delete(_temporaryConfigPath);
            }
            catch (Exception cleanupError)
            {
                errors.Add(new InvalidOperationException(
                    $"Temporary Spring configuration failed to delete: {_temporaryConfigPath}",
                    cleanupError));
            }
        }

        _temporaryConfigPath = null;
        _loadedBridges = Array.Empty<NexaOneModuleBridgeRuntimeInfo>();
        return errors;
    }

    /// <summary>선언된 Bridge catalog와 로드된 서비스·Bean·계약 타입의 일치 여부를 검증한다.</summary>
    private IReadOnlyList<NexaOneModuleBridgeRuntimeInfo> ValidateModuleBridges(
        INexaModuleBridgeCatalog catalog,
        IReadOnlyCollection<string> loadedServices)
    {
        var serviceNames = loadedServices.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var validated = new List<NexaOneModuleBridgeRuntimeInfo>(catalog.Descriptors.Count);

        foreach (var descriptor in catalog.Descriptors)
        {
            if (!serviceNames.Contains(descriptor.Module))
            {
                throw new InvalidOperationException(
                    $"Module bridge contract '{descriptor.ContractType.FullName}' references service " +
                    $"'{descriptor.Module}', but that service is not loaded.");
            }

            object bean;
            try
            {
                bean = _server.GetBean(descriptor.Module, descriptor.BeanName);
            }
            catch (Exception error)
            {
                throw new InvalidOperationException(
                    $"Module bridge bean '{descriptor.Module}/{descriptor.BeanName}' for contract " +
                    $"'{descriptor.ContractType.FullName}' could not be resolved.",
                    error);
            }

            var implementationType = bean.GetType();
            if (!descriptor.ContractType.IsInstanceOfType(bean))
            {
                throw BridgeTypeMismatch(
                    descriptor.ContractType,
                    descriptor.Module,
                    descriptor.BeanName,
                    implementationType);
            }

            // 진단 모델에는 Type/Bean 인스턴스 대신 문자열만 보관해 plugin ALC 수명주기를 붙잡지 않는다.
            validated.Add(new NexaOneModuleBridgeRuntimeInfo(
                descriptor.Module,
                descriptor.BeanName,
                descriptor.ContractType.FullName ?? descriptor.ContractType.Name,
                implementationType.FullName ?? implementationType.Name));
        }

        return validated.AsReadOnly();
    }

    /// <summary>Bridge 계약 중복 로드나 잘못된 Spring Bean 형식을 설명하는 공통 예외를 생성한다.</summary>
    private static InvalidOperationException BridgeTypeMismatch(
        Type contractType,
        string module,
        string beanName,
        Type implementationType) =>
        new(
            $"Module bridge bean '{module}/{beanName}' is '{implementationType.FullName}', not " +
            $"'{contractType.FullName}'. Check the shared-contract plugin ALC identity " +
            "(ADR-008 and module publishing without dependency copies).");

    /// <summary>Driver 검증이 Spring context 초기화 전 또는 종료 후 실행되지 않도록 상태를 검증한다.</summary>
    private void EnsureInitialized()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_initialized)
        {
            throw new InvalidOperationException(
                "MES module runtime is not initialized. Resolve server drivers only after Spring contexts are ready.");
        }
    }

    /// <summary>Bridge나 스케줄러가 초기화 전 또는 종료 후 접근되지 않도록 상태를 검증한다.</summary>
    private void EnsureStarted()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_started)
        {
            throw new InvalidOperationException(
                "MES module runtime is not started. Resolve module bridges only after the host has started.");
        }
    }

    /// <summary>
    /// Gateway와 Spring이 같은 SQLite DB를 사용하도록 Spring XML의 연결 문자열을 임시 복사본에서 통일한다.
    /// 원본 설정 파일은 변경하지 않는다.
    /// </summary>
    private static string UnifySqliteDataSourceIfConfigured(
        string serverXmlPath,
        IConfiguration configuration,
        out string? temporaryConfigPath)
    {
        temporaryConfigPath = null;
        var gatewayConnection = configuration.GetConnectionString("NexaOne");
        if (string.IsNullOrWhiteSpace(gatewayConnection)) return serverXmlPath;

        XNamespace spring = "http://www.springframework.net";
        var document = XDocument.Load(serverXmlPath);
        var objects = document.Root?.Elements(spring + "object").ToList() ?? new List<XElement>();
        var dataSource = objects.FirstOrDefault(element => (string?)element.Attribute("id") == "eesDataSource");
        var connectionAttribute = dataSource?.Elements(spring + "property")
            .FirstOrDefault(property => (string?)property.Attribute("name") == "ConnectionString")
            ?.Attribute("value");
        if (connectionAttribute is null) return serverXmlPath;

        var providerReference = dataSource!.Elements(spring + "property")
            .FirstOrDefault(property => (string?)property.Attribute("name") == "Provider")
            ?.Attribute("ref")?.Value;
        var providerType = objects.FirstOrDefault(element => (string?)element.Attribute("id") == providerReference)
            ?.Attribute("type")?.Value ?? string.Empty;
        if (!providerType.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)) return serverXmlPath;
        if (string.Equals(connectionAttribute.Value, gatewayConnection, StringComparison.Ordinal)) return serverXmlPath;

        connectionAttribute.Value = gatewayConnection;
        var patchedPath = Path.Combine(Path.GetTempPath(), $"nexaone-server-unified-{Guid.NewGuid():N}.xml");
        try
        {
            document.Save(patchedPath);
        }
        catch (Exception saveError)
        {
            TryDeleteTemporaryConfigPreservingPrimary(patchedPath, saveError);
            throw;
        }

        temporaryConfigPath = patchedPath;
        // Connection strings can contain credentials and absolute host paths. Never emit the raw
        // value; successful unification is sufficient operational evidence.
        Console.WriteLine("[NexaOne.Server] SQLite 통일 — eesDataSource를 구성된 게이트웨이 DB로 재지정했습니다.");
        return patchedPath;
    }

    /// <summary>Spring 서버 설정이 SQLite를 사용할 때 모듈용 스키마를 멱등 생성한다.</summary>
    private static string? EnsureSqliteSchemaIfConfigured(string serverXmlPath)
    {
        XNamespace spring = "http://www.springframework.net";
        var document = XDocument.Load(serverXmlPath);
        var objects = document.Root?.Elements(spring + "object").ToList() ?? new List<XElement>();
        var dataSource = objects.FirstOrDefault(element => (string?)element.Attribute("id") == "eesDataSource");
        if (dataSource is null) return null;

        var properties = dataSource.Elements(spring + "property").ToList();
        var connectionString = properties
            .FirstOrDefault(property => (string?)property.Attribute("name") == "ConnectionString")
            ?.Attribute("value")?.Value;
        var providerReference = properties
            .FirstOrDefault(property => (string?)property.Attribute("name") == "Provider")
            ?.Attribute("ref")?.Value;
        var providerType = objects.FirstOrDefault(element => (string?)element.Attribute("id") == providerReference)
            ?.Attribute("type")?.Value ?? string.Empty;

        if (!providerType.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("SQLite eesDataSource ConnectionString is empty.");
        SqliteSchemaInitializer.EnsureSchema(connectionString);
        return connectionString;
    }

    /// <summary>Gateway 설정이 SQLite인 경우 Spring 모듈 사용 여부와 무관하게 기본 스키마를 준비한다.</summary>
    private static void EnsureGatewaySqliteSchemaIfConfigured(IConfiguration configuration)
    {
        if (!string.Equals(
                configuration["Database:Provider"],
                "Sqlite",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var connectionString = configuration.GetConnectionString("NexaOne");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("ConnectionStrings:NexaOne is required for SQLite hosting.");

        SqliteSchemaInitializer.EnsureSchema(connectionString);
    }

    /// <summary>임시 설정 삭제 실패를 기록하되 설정 저장 중 발생한 최초 예외는 그대로 보존한다.</summary>
    private static void TryDeleteTemporaryConfigPreservingPrimary(string? path, Exception primaryError)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            File.Delete(path);
        }
        catch (Exception cleanupError)
        {
            ReportCleanupFailure($"temporary Spring config deletion ({path})", cleanupError, primaryError);
        }
    }

    /// <summary>정리 실패와 보존 중인 최초 오류를 함께 진단 로그에 기록한다.</summary>
    private static void ReportCleanupFailure(
        string operation,
        Exception cleanupError,
        Exception? primaryError)
    {
        var preserved = primaryError is null
            ? string.Empty
            : $"; preserving primary {primaryError.GetType().Name}: {primaryError.Message}";
        Console.Error.WriteLine(
            $"[NexaOne.Server] Cleanup failed during {operation}: {cleanupError.Message}{preserved}");
    }
}

/// <summary>시작 단계에서 실제 Spring 구현과 계약 일치가 확인된 Bridge의 안전한 진단 정보다.</summary>
/// <param name="Module">Bridge를 소유한 Spring 서비스 모듈 이름이다.</param>
/// <param name="BeanName">모듈 XML에 등록된 Spring Bean 이름이다.</param>
/// <param name="Contract">Default ALC에서 공유하는 Bridge 계약의 전체 형식 이름이다.</param>
/// <param name="Implementation">Plugin ALC가 생성한 구현의 전체 형식 이름이다.</param>
internal sealed record NexaOneModuleBridgeRuntimeInfo(
    string Module,
    string BeanName,
    string Contract,
    string Implementation);

/// <summary>
/// 지연 시작되는 Spring 런타임 소유 스케줄러를 DI에서 안정적으로 노출하는 어댑터다.
/// HostedService 생성 중에는 Spring을 시작하지 않고, 실제 호출 시 시작 완료된 스케줄러로 위임한다.
/// </summary>
internal sealed class NexaOneMesRecurringScheduler(NexaOneMesRuntimeState runtime) : IRecurringScheduler
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
        => runtime.GetScheduler().StartAsync(cancellationToken);

    /// <inheritdoc />
    public Task ScheduleRecurringAsync(
        string name,
        TimeSpan interval,
        Func<CancellationToken, Task> job,
        CancellationToken cancellationToken = default)
        => runtime.GetScheduler().ScheduleRecurringAsync(name, interval, job, cancellationToken);

    /// <inheritdoc />
    public Task ScheduleRecurringCronAsync(
        string name,
        string cron,
        Func<CancellationToken, Task> job,
        CancellationToken cancellationToken = default)
        => runtime.GetScheduler().ScheduleRecurringCronAsync(name, cron, job, cancellationToken);

    /// <inheritdoc />
    public Task UnscheduleAsync(string name, CancellationToken cancellationToken = default)
        => runtime.GetScheduler().UnscheduleAsync(name, cancellationToken);

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
        => runtime.GetScheduler().StopAsync(cancellationToken);
}
