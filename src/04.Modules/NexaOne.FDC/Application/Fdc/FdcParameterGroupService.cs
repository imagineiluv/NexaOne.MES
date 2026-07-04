using NexaOne.Common;
using NexaOne.FDC.Domain;

namespace NexaOne.FDC.Application.Fdc;

/// <summary>FDC 파라미터 그룹 관리 서비스 (design 10.4.1 — FDC 파라미터 그룹 화면 백엔드).</summary>
public sealed class FdcParameterGroupService
{
    private readonly IFdcParameterGroupRepository _groupRepository;

    public FdcParameterGroupService(IFdcParameterGroupRepository groupRepository)
    {
        _groupRepository = groupRepository;
    }

    public async Task<IReadOnlyList<FdcParameterGroup>> GetGroupsAsync(string equipmentId, CancellationToken ct = default)
        => await _groupRepository.GetByEquipmentAsync(equipmentId, ct);

    public async Task<Result<FdcParameterGroup>> CreateGroupAsync(
        string groupId,
        string groupName,
        string equipmentId,
        string? description = null,
        int displayOrder = 0,
        CancellationToken ct = default)
    {
        var result = FdcParameterGroup.Create(groupId, groupName, equipmentId, description, displayOrder);
        if (result.IsFailure) return result;

        await _groupRepository.AddAsync(result.Value, ct);
        return result;
    }

    public async Task<Result<FdcParameterGroup>> RenameGroupAsync(
        string groupId, string groupName, CancellationToken ct = default)
    {
        var group = await _groupRepository.GetByIdAsync(groupId, ct);
        if (group is null)
            return Result.Failure<FdcParameterGroup>(Error.NotFound(nameof(FdcParameterGroup), $"FdcParameterGroup '{groupId}'을(를) 찾을 수 없습니다."));

        group.Rename(groupName);
        await _groupRepository.UpdateAsync(group, ct);
        return group;
    }

    public async Task<Result<FdcParameterGroup>> DeactivateGroupAsync(string groupId, CancellationToken ct = default)
    {
        var group = await _groupRepository.GetByIdAsync(groupId, ct);
        if (group is null)
            return Result.Failure<FdcParameterGroup>(Error.NotFound(nameof(FdcParameterGroup), $"FdcParameterGroup '{groupId}'을(를) 찾을 수 없습니다."));

        group.Deactivate();
        await _groupRepository.UpdateAsync(group, ct);
        return group;
    }
}
