using NexaOne.ServiceContracts.Sys;

namespace NexaOne.Server.Gateway;

/// <summary>SYS 소유 범용 채번 엔진을 형제 컨텍스트로 전달하는 부모 proxy입니다.</summary>
public sealed class IdRuleEngineProxy : IIdRuleEngine
{
    private readonly ModuleBeanResolver _resolver;

    public IdRuleEngineProxy(ModuleBeanResolver resolver)
        => _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public Task<string> NextIdAsync(string ruleId, CancellationToken ct = default)
        => Resolve().NextIdAsync(ruleId, ct);

    private IIdRuleEngine Resolve() =>
        _resolver.Resolve<IIdRuleEngine>("Sys", "idRuleEngine");
}
