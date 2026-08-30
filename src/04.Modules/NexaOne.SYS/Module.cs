using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Ems;
using NexaOne.ServiceContracts.Sys;
using NexaOne.SYS.Application.Deploys;
using NexaOne.SYS.Application.Users;
using NexaOne.SYS.Infrastructure;
using NexaDB.Data.Abstractions.Interfaces;
using NexaFramework.Scheduling;

namespace NexaOne.SYS;

/// <summary>SYS 내부 저장소·사용자 그래프를 숨기고 공개 bridge, directory와 worker만 노출하는 조립 진입점입니다.</summary>
public sealed class Module
{
    private readonly ISysBridge _sysBridge;
    private readonly IDeployBridge _deployBridge;
    private readonly IMaintenanceIdentityDirectory _maintenanceIdentityDirectory;
    private readonly IUserDirectory _userDirectory;
    private readonly IReleasedProgramArtifactDirectory _releasedProgramArtifactDirectory;
    private readonly ISqliteSchemaContribution _trustedAuthoritySqliteSchemaContribution;
    private readonly IHostedService _loginFailureRetentionWorker;

    public Module(
        EesDataSource dataSource,
        INexaOneEESDbCapability dialect,
        IConfiguration configuration,
        IRecurringScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(scheduler);

        var options = SysModuleOptions.FromConfiguration(configuration);

        var users = new UserRepository(dataSource, configuration);
        var loginFailures = new LoginFailureHistoryRepository(dataSource, dialect);
        var userService = new UserService(
            users,
            new RoleRepository(dataSource),
            new MultiLanguageResourceRepository(dataSource),
            loginFailures);
        var registration = new UserRegistrationService(
            new UserRequestRepository(dataSource, configuration),
            users);
        _sysBridge = new SysBridge(userService, registration);
        _deployBridge = new DeployBridge(new DeployService(
            new DeployFileRepository(dataSource),
            new FileSystemDeployFileStorage("data/deploy-files")));
        _maintenanceIdentityDirectory = new MaintenanceIdentityDirectory(dataSource);
        _userDirectory = new UserDirectory(dataSource);
        _releasedProgramArtifactDirectory = new ReleasedProgramArtifactDirectory(dataSource);
        _trustedAuthoritySqliteSchemaContribution =
            new SysTrustedAuthoritySqliteSchemaContribution();
        _loginFailureRetentionWorker = new LoginFailureRetentionWorker(
            scheduler,
            loginFailures,
            enabled: options.LoginFailureRetentionEnabled,
            intervalSeconds: options.LoginFailureRetentionIntervalSeconds,
            retentionDays: options.LoginFailureRetentionDays);
    }

    public ISysBridge GetSysBridge() => _sysBridge;
    public IDeployBridge GetDeployBridge() => _deployBridge;
    public IMaintenanceIdentityDirectory GetMaintenanceIdentityDirectory() => _maintenanceIdentityDirectory;
    public IUserDirectory GetUserDirectory() => _userDirectory;
    public IReleasedProgramArtifactDirectory GetReleasedProgramArtifactDirectory() => _releasedProgramArtifactDirectory;
    public ISqliteSchemaContribution GetTrustedAuthoritySqliteSchemaContribution() =>
        _trustedAuthoritySqliteSchemaContribution;
    public IHostedService GetLoginFailureRetentionWorker() => _loginFailureRetentionWorker;
}

/// <summary>
/// Spring XML에 실행 정책 상수를 노출하지 않도록 SYS 로그인실패 보존 worker 설정을 정규화합니다.
/// 활성화 키가 없으면 OFF이고, 삭제 주기와 보존기간은 보수적인 최소값으로 제한합니다.
/// </summary>
internal sealed record SysModuleOptions(
    bool LoginFailureRetentionEnabled,
    int LoginFailureRetentionIntervalSeconds,
    int LoginFailureRetentionDays)
{
    public static SysModuleOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new SysModuleOptions(
            LoginFailureRetentionEnabled: configuration.GetValue(
                "Worker:Sys:LoginFailureRetention:Enabled",
                false),
            LoginFailureRetentionIntervalSeconds: Math.Max(
                configuration.GetValue("Worker:Sys:LoginFailureRetention:IntervalSeconds", 86_400),
                60),
            LoginFailureRetentionDays: Math.Max(
                configuration.GetValue("Worker:Sys:LoginFailureRetention:RetentionDays", 90),
                1));
    }
}
