namespace NexaOne.Web.Services.Meta;

/// <summary>
/// 메타 화면의 명령을 실행할 때 함께 전달되는 클라이언트 실행 경계입니다.
/// 채널과 장치 ID는 도메인 실행 이력에 기록되며, 사용자 ID와 권한은 브라우저가 아닌 서버의 JWT 게이트가 결정합니다.
/// </summary>
public sealed record MetaCommandExecutionContext(
    string UiId,
    string ClientChannel,
    string? DeviceId = null);

/// <summary>메타 명령을 현재 모델 또는 선택 행에 적용할 수 있는지 나타냅니다.</summary>
public sealed record MetaCommandAvailability(bool CanExecute, string? DisabledReason = null)
{
    public static MetaCommandAvailability Enabled { get; } = new(true);

    public static MetaCommandAvailability Disabled(string reason) => new(false, reason);
}

/// <summary>
/// 드라이버 실행 결과입니다. HTTP 상태 코드를 보존해 409 동시성 충돌을 일반 실패와 구분하고,
/// 성공 메시지/경고도 전달해 NoControl처럼 "경고 후 허용"된 결정을 사용자에게 숨기지 않습니다.
/// </summary>
public sealed record MetaCommandResult(
    bool Success,
    int? StatusCode = null,
    string? Error = null,
    string? Message = null,
    bool IsWarning = false)
{
    public static MetaCommandResult Succeeded(
        int? statusCode = 200,
        string? message = null,
        bool isWarning = false)
        => new(true, statusCode, Message: message, IsWarning: isWarning);

    public static MetaCommandResult Failed(string error, int? statusCode = null) => new(false, statusCode, error);
}

/// <summary>메타 명령이 파라미터 하나에 실행되는지, 선택 행 전체를 묶는 호스트가 필요한지 구분합니다.</summary>
public enum MetaCommandExecutionMode
{
    /// <summary>폼 모델 또는 그리드 행 하나를 드라이버에 전달합니다.</summary>
    PerRow,

    /// <summary>선택 행 전체와 추가 입력을 한 번에 묶는 전용 호스트 핸들러가 필요합니다.</summary>
    HostRequiredAggregate,
}

/// <summary>명령이 업무 데이터를 변경하는지 나타냅니다.</summary>
public enum MetaCommandEffect
{
    /// <summary>생성·수정·삭제·상태 전이처럼 업무 상태를 변경합니다.</summary>
    Mutating,

    /// <summary>내보내기·다운로드·미리보기처럼 업무 상태를 변경하지 않습니다.</summary>
    NonMutating,
}

/// <summary>
/// Designer와 검증기가 공유하는 SQL 비노출 브리지 명령 계약입니다.
/// 기존 드라이버와 생성자는 안전한 기본값인 행 단위 변경 명령으로 유지됩니다.
/// <para><c>Id</c>는 null/공백이 아닌 정규 <c>bridge:</c> ID여야 하며, 위반하면 카탈로그 생성 시 실패합니다.</para>
/// <para><c>RequiredPermission</c>의 null은 드라이버가 UI 권한 힌트를 선언하지 않았다는 뜻입니다.
/// 쓰기 화면 바인딩 검증기는 이를 보호되지 않은 계약으로 오류 처리하며, 실제 API의 서버 권한 검사는 별도로 항상 적용됩니다.</para>
/// </summary>
public sealed record MetaCommandDescriptor(
    string Id,
    string? RequiredPermission = null,
    MetaCommandExecutionMode ExecutionMode = MetaCommandExecutionMode.PerRow,
    MetaCommandEffect Effect = MetaCommandEffect.Mutating);

/// <summary>
/// <c>bridge:</c> 명령 하나를 타입이 있는 애플리케이션 API로 연결하는 확장점입니다.
/// 구현은 raw SQL을 실행하지 않고 기존 REST/Bridge 경계를 호출해야 합니다.
/// </summary>
public interface IMetaCommandDriver
{
    IReadOnlyCollection<string> CommandIds { get; }

    string? GetRequiredPermission(string commandId);

    /// <summary>
    /// 명령별 실행 방식과 변경 여부를 포함한 계약입니다.
    /// 기존 구현은 <see cref="CommandIds"/>와 <see cref="GetRequiredPermission"/>만으로도 동작합니다.
    /// </summary>
    IReadOnlyCollection<MetaCommandDescriptor> Commands
        => CommandIds
            .Select(commandId => new MetaCommandDescriptor(commandId, GetRequiredPermission(commandId)))
            .ToArray();

    /// <summary>
    /// 현재 모델에서 실행 버튼을 활성화할지 미리 판단합니다. 렌더링 중 여러 번 호출될 수 있으므로
    /// 외부 상태 변경·네트워크 호출 없이 저비용이고 무부작용이어야 합니다. 이 결과는 보안/정합성 보장이 아닙니다.
    /// </summary>
    MetaCommandAvailability CanExecute(
        string commandId,
        IReadOnlyDictionary<string, object?> parameters,
        MetaCommandExecutionContext context);

    /// <summary>
    /// 명령을 실제 실행합니다. <see cref="CanExecute"/> 이후 모델·서버 상태가 바뀔 수 있으므로 구현은
    /// 입력과 상태 전이 조건을 다시 검증해야 하며, 서버 API의 권한·동시성 검사를 우회하면 안 됩니다.
    /// </summary>
    Task<MetaCommandResult> ExecuteAsync(
        string commandId,
        IReadOnlyDictionary<string, object?> parameters,
        MetaCommandExecutionContext context,
        CancellationToken ct = default);
}

/// <summary>등록된 메타 명령 드라이버를 액션 ID로 찾는 단일 카탈로그입니다.</summary>
public interface IMetaCommandDriverCatalog
{
    IReadOnlyCollection<MetaCommandDescriptor> Commands { get; }

    bool Contains(string commandId);

    /// <summary>등록된 명령의 실행 계약을 대소문자 구분 없이 찾습니다.</summary>
    bool TryGetDescriptor(string commandId, out MetaCommandDescriptor? descriptor)
    {
        descriptor = string.IsNullOrWhiteSpace(commandId)
            ? null
            : Commands.FirstOrDefault(command =>
                string.Equals(command.Id, commandId, StringComparison.OrdinalIgnoreCase));
        return descriptor is not null;
    }

    MetaCommandAvailability CanExecute(
        string commandId,
        IReadOnlyDictionary<string, object?> parameters,
        MetaCommandExecutionContext context);

    Task<MetaCommandResult> ExecuteAsync(
        string commandId,
        IReadOnlyDictionary<string, object?> parameters,
        MetaCommandExecutionContext context,
        CancellationToken ct = default);
}

/// <summary>
/// DI의 <see cref="IMetaCommandDriver"/> 구현을 액션 ID별로 한 번 인덱싱합니다.
/// 동일 ID가 중복 등록되면 시작 시점에 실패시켜 어느 구현이 실행되는지 모호해지는 것을 막습니다.
/// </summary>
public sealed class MetaCommandDriverCatalog : IMetaCommandDriverCatalog
{
    private readonly IReadOnlyDictionary<string, IMetaCommandDriver> _drivers;
    private readonly IReadOnlyDictionary<string, MetaCommandDescriptor> _descriptors;
    private readonly IReadOnlyCollection<MetaCommandDescriptor> _commands;

    public MetaCommandDriverCatalog(IEnumerable<IMetaCommandDriver> drivers)
    {
        var indexed = new Dictionary<string, IMetaCommandDriver>(StringComparer.OrdinalIgnoreCase);
        var descriptors = new Dictionary<string, MetaCommandDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var driver in drivers)
        {
            foreach (var descriptor in driver.Commands)
            {
                var commandId = descriptor.Id;
                if (string.IsNullOrWhiteSpace(commandId) || !commandId.StartsWith("bridge:", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Meta command driver IDs must start with 'bridge:'.");
                if (!Enum.IsDefined(descriptor.ExecutionMode))
                    throw new InvalidOperationException($"Meta command '{commandId}' has an invalid execution mode.");
                if (!Enum.IsDefined(descriptor.Effect))
                    throw new InvalidOperationException($"Meta command '{commandId}' has an invalid effect.");

                var canonicalId = commandId.Trim();
                if (!indexed.TryAdd(canonicalId, driver))
                    throw new InvalidOperationException($"Meta command driver '{commandId}' is registered more than once.");
                descriptors.Add(canonicalId, descriptor with { Id = canonicalId });
            }
        }
        _drivers = indexed;
        _descriptors = descriptors;
        _commands = descriptors.Values
            .OrderBy(command => command.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyCollection<MetaCommandDescriptor> Commands => _commands;

    public bool Contains(string commandId)
        => !string.IsNullOrWhiteSpace(commandId) && _drivers.ContainsKey(commandId);

    public bool TryGetDescriptor(string commandId, out MetaCommandDescriptor? descriptor)
    {
        if (string.IsNullOrWhiteSpace(commandId))
        {
            descriptor = null;
            return false;
        }

        return _descriptors.TryGetValue(commandId, out descriptor);
    }

    public MetaCommandAvailability CanExecute(
        string commandId,
        IReadOnlyDictionary<string, object?> parameters,
        MetaCommandExecutionContext context)
    {
        if (!_drivers.TryGetValue(commandId, out var driver)
            || !_descriptors.TryGetValue(commandId, out var descriptor))
            return MetaCommandAvailability.Disabled($"등록되지 않은 브리지 명령입니다: {commandId}");

        return descriptor.ExecutionMode == MetaCommandExecutionMode.HostRequiredAggregate
            ? MetaCommandAvailability.Disabled(HostRequiredReason(descriptor.Id))
            : driver.CanExecute(commandId, parameters, context);
    }

    public Task<MetaCommandResult> ExecuteAsync(
        string commandId,
        IReadOnlyDictionary<string, object?> parameters,
        MetaCommandExecutionContext context,
        CancellationToken ct = default)
    {
        if (!_drivers.TryGetValue(commandId, out var driver)
            || !_descriptors.TryGetValue(commandId, out var descriptor))
            return Task.FromResult(MetaCommandResult.Failed($"등록되지 않은 브리지 명령입니다: {commandId}"));

        if (descriptor.ExecutionMode == MetaCommandExecutionMode.HostRequiredAggregate)
            return Task.FromResult(MetaCommandResult.Failed(HostRequiredReason(descriptor.Id), statusCode: 422));

        return driver.ExecuteAsync(commandId, parameters, context, ct);
    }

    private static string HostRequiredReason(string commandId)
        => $"명령 '{commandId}'은(는) 선택 행 전체를 전달하는 전용 호스트 일괄 핸들러가 필요합니다.";
}
