using Microsoft.Extensions.Hosting;
using NexaOne.Infrastructure.Diagnostics;
using NexaOne.Server.Gateway;

namespace NexaOne.Server;

/// <summary>
/// Generic Host 수명주기 단계에 MES 런타임을 연결한다.
/// 스키마·보안·모듈 준비는 일반 HostedService보다 먼저 완료하고, 모듈과 Spring 컨텍스트는
/// 일반 서비스가 모두 멈춘 뒤 정리한다. HostOptions의 동시 시작·종료 설정에서도 이 경계를 유지한다.
/// </summary>
internal sealed class NexaOneMesStartupHostedService : IHostedLifecycleService, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly IHostEnvironment _environment;
    private readonly NexaOneMesRuntimeState _runtime;
    private readonly ExternalDependencyProbeCatalog _dependencies;
    private readonly object _startupLock = new();
    private Task? _startupTask;
    private bool _stoppingViaLifecycle;

    /// <summary>호스트 서비스 공급자와 MES 런타임 상태를 연결한다.</summary>
    public NexaOneMesStartupHostedService(
        IServiceProvider services,
        IHostEnvironment environment,
        NexaOneMesRuntimeState runtime,
        ExternalDependencyProbeCatalog dependencies)
    {
        _services = services;
        _environment = environment;
        _runtime = runtime;
        _dependencies = dependencies;
    }

    /// <summary>일반 HostedService가 시작되기 전에 MES 런타임을 준비한다.</summary>
    public Task StartingAsync(CancellationToken cancellationToken)
        => EnsureStartupAsync(cancellationToken);

    /// <summary>
    /// IHostedLifecycleService를 직접 사용하지 않는 호출 경로에서도 동일한 초기화를 보장한다.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
        => EnsureStartupAsync(cancellationToken);

    /// <summary>MES 초기화 후에는 별도의 후속 시작 작업이 없어 즉시 완료한다.</summary>
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>수명주기 기반 종료임을 표시해 실제 정리를 Stopped 단계까지 지연한다.</summary>
    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        _stoppingViaLifecycle = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 일반 HostedService 종료 단계에서는 모듈을 유지하고, 직접 호출된 경우에만 즉시 정리한다.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
        => _stoppingViaLifecycle ? Task.CompletedTask : _runtime.StopAsync(cancellationToken);

    /// <summary>다른 HostedService가 모두 멈춘 뒤 MES 워커와 Spring 컨텍스트를 정리한다.</summary>
    public Task StoppedAsync(CancellationToken cancellationToken)
        => _runtime.StopAsync(cancellationToken);

    /// <summary>비정상 종료 경로에서도 남아 있는 모듈 자원의 정리를 시도한다.</summary>
    public void Dispose() => _runtime.DisposeModules();

    /// <summary>StartingAsync와 StartAsync가 같은 초기화 작업을 공유하도록 최초 작업을 캐시한다.</summary>
    private Task EnsureStartupAsync(CancellationToken cancellationToken)
    {
        lock (_startupLock)
            return _startupTask ??= StartCoreAsync(cancellationToken);
    }

    /// <summary>스키마, 운영 보안 정책, 모듈 워커 순서로 MES 시작 절차를 수행한다.</summary>
    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            // 새 SQLite DB에도 SYS_USER가 먼저 생성되어야 운영 관리자 보안 강화 쿼리가 안전하다.
            await _runtime.InitializeAsync(_services, cancellationToken).ConfigureAwait(false);

            // Spring contexts must exist before their adapters can be probed, but no module worker
            // may perform side effects until every required external dependency has passed validation.
            await ValidateRequiredDependenciesAsync(cancellationToken).ConfigureAwait(false);

            if (_environment.IsProduction())
            {
                var hardened = await DefaultAdminHardening.HardenAsync(_services, cancellationToken)
                    .ConfigureAwait(false);
                if (hardened > 0)
                {
                    Console.WriteLine(
                        "[NexaOne.Server] ⚠ 기본 admin 자격 감지 — PASSWORD_STATE='Create'로 강제(첫 로그인 시 변경 필수).");
                }
            }

            // 보안 정책이 확정된 뒤에만 업무 워커가 요청과 이벤트를 처리하게 한다.
            await _runtime.StartWorkersAsync(_services, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception startupError)
        {
            await _runtime.AbortStartupAsync(startupError).ConfigureAwait(false);
            throw;
        }
    }

    private async Task ValidateRequiredDependenciesAsync(CancellationToken cancellationToken)
    {
        var failures = (await _dependencies.CheckAllAsync(cancellationToken).ConfigureAwait(false))
            .Where(static snapshot => snapshot.Descriptor.RequiredAtStartup
                && snapshot.Health.Status == ExternalDependencyHealthStatus.Unhealthy)
            .Select(static snapshot => $"{snapshot.Descriptor.Id}: {snapshot.Health.Summary}")
            .ToArray();
        if (failures.Length > 0)
        {
            throw new InvalidOperationException(
                "Required external dependency validation failed before MES workers started: "
                + string.Join("; ", failures));
        }
    }
}
