namespace NexaOne.Web.Services.Meta;

/// <summary>MRP 계획오더의 실오더 전환 명령 ID입니다.</summary>
public static class MrpConversionMetaCommands
{
    public const string Convert = "bridge:pom.mrp.convert";
}

/// <summary>
/// MRP 전환 명령을 공통 bridge catalog에 등록합니다.
/// 실제 실행은 여러 선택 행을 한 요청으로 묶고 생산 제안별 설비 배정을 받아야 하므로
/// HostMrpPlanning의 명시적 BridgeBulkHandler가 담당합니다. 일반 MetaScreen의 행 단위 catalog fallback은
/// 부분 전환을 만들 수 있어 명확한 사유와 함께 거부합니다.
/// </summary>
public sealed class MrpConversionMetaCommandDriver : IMetaCommandDriver
{
    public IReadOnlyCollection<string> CommandIds { get; } = [MrpConversionMetaCommands.Convert];

    /// <summary>
    /// MRP 전환은 선택 제안 전체와 설비 배정을 한 트랜잭션으로 전달해야 하므로
    /// 일반 행 단위 실행 경로에서 실행할 수 없습니다.
    /// </summary>
    public IReadOnlyCollection<MetaCommandDescriptor> Commands { get; } =
    [
        new(
            MrpConversionMetaCommands.Convert,
            RequiredPermission: "pom:manage",
            ExecutionMode: MetaCommandExecutionMode.HostRequiredAggregate,
            Effect: MetaCommandEffect.Mutating),
    ];

    public string? GetRequiredPermission(string commandId)
        => string.Equals(commandId, MrpConversionMetaCommands.Convert, StringComparison.OrdinalIgnoreCase)
            ? "pom:manage"
            : null;

    public MetaCommandAvailability CanExecute(
        string commandId,
        IReadOnlyDictionary<string, object?> parameters,
        MetaCommandExecutionContext context)
        => IsSupported(commandId)
            ? MetaCommandAvailability.Disabled(
                "MRP 전환은 전용 화면에서 선택 제안 전체와 생산 설비 배정을 함께 제출해야 합니다.")
            : MetaCommandAvailability.Disabled($"지원하지 않는 MRP 명령입니다: {commandId}");

    public Task<MetaCommandResult> ExecuteAsync(
        string commandId,
        IReadOnlyDictionary<string, object?> parameters,
        MetaCommandExecutionContext context,
        CancellationToken ct = default)
        => Task.FromResult(IsSupported(commandId)
            ? MetaCommandResult.Failed(
                "bridge:pom.mrp.convert requires the HostMrpPlanning aggregate contract: " +
                "one runId, all selected plannedOrderIds, and productionAssignments.",
                statusCode: 422)
            : MetaCommandResult.Failed($"지원하지 않는 MRP 명령입니다: {commandId}", statusCode: 404));

    private static bool IsSupported(string commandId)
        => string.Equals(commandId, MrpConversionMetaCommands.Convert, StringComparison.OrdinalIgnoreCase);
}
