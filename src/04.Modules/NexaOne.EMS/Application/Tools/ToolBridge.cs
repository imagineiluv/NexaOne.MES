using NexaOne.Common;
using NexaOne.ServiceContracts.Ems;

namespace NexaOne.EMS.Application.Tools;

public sealed class ToolBridge : IToolBridge
{
    private readonly ToolService _service;
    public ToolBridge(ToolService service) => _service = service;

    public async Task<Result<ToolDto>> SaveAsync(ToolCommand c, CancellationToken ct = default)
    { var r = await _service.SaveAsync(c, ct); return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<ToolDto>(r.Error); }
    public async Task<Result<ToolMountDto>> MountAsync(ToolMountCommand c, CancellationToken ct = default)
    { var r = await _service.MountAsync(c, ct); return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<ToolMountDto>(r.Error); }
    public async Task<Result<ToolMountDto>> UnmountAsync(ToolUnmountCommand c, CancellationToken ct = default)
    { var r = await _service.UnmountAsync(c, ct); return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<ToolMountDto>(r.Error); }
    public async Task<Result<ToolUsageDto>> RecordUsageAsync(ToolUsageCommand c, CancellationToken ct = default)
    { var r = await _service.RecordUsageAsync(c, ct); return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<ToolUsageDto>(r.Error); }
    public async Task<Result<ToolInspectionDto>> RecordInspectionAsync(ToolInspectionCommand c, CancellationToken ct = default)
    { var r = await _service.RecordInspectionAsync(c, ct); return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<ToolInspectionDto>(r.Error); }

    private static ToolDto ToDto(ToolRecord r) => new(r.ToolId, r.ToolName, r.ToolType, r.Status,
        r.CurrentUseCount, r.CurrentUseMinutes, r.MaxUseCount, r.MaxUseMinutes,
        r.NextInspectionDueAt, r.NextCalibrationDueAt, r.IsActive, r.Version);
    private static ToolMountDto ToDto(ToolMountRecord r) => new(r.MountId, r.ToolId, r.EquipmentId,
        r.PositionCode, r.MountedAt, r.MountedBy, r.UnmountedAt, r.UnmountedBy, r.UnmountReason);
    private static ToolUsageDto ToDto(ToolUsageRecord r) => new(r.UsageId, r.ToolId, r.EquipmentId,
        r.UseCount, r.UseMinutes, r.UsedAt, r.UsedBy, r.ProcessLotId, r.WorkOrderId, r.TraceId,
        r.WorkScopeId, r.CarrierId, r.ActivityType, r.CleaningProgramId, r.CleaningResult);
    private static ToolInspectionDto ToDto(ToolInspectionRecord r) => new(r.InspectionId, r.ToolId,
        r.InspectionType, r.Result, r.InspectedAt, r.InspectedBy, r.NextDueAt, r.CertificateNumber);
}
