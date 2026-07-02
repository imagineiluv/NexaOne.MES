using NexaOne.Common;
using NexaOne.ServiceContracts.Sys;
using NexaOne.SYS.Domain;

namespace NexaOne.SYS.Application.Deploys;

/// <summary>ADR-008 얇은 브리지 어댑터 — DeployService(§20.11 업로드/latest 선정/활성 전환/다운로드)에
/// 위임하고 도메인 엔티티를 계약 DTO로 매핑한다. plugin ALC에서 생성되며 호스트(Default ALC)가
/// IDeployBridge로 캐스트해 DI에 등록한다. Result는 그대로 통과(컨트롤러가 409/400/404 매핑).</summary>
public sealed class DeployBridge : IDeployBridge
{
    private readonly DeployService _service;

    public DeployBridge(DeployService service) => _service = service;

    public async Task<Result<DeployFileDto>> UploadAsync(
        string version, string fileName, string? description, bool forceUpdate,
        Stream content, string uploadedBy, CancellationToken ct = default)
    {
        var r = await _service.UploadAsync(version, fileName, description, forceUpdate, content, uploadedBy, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<DeployFileDto>(r.Error);
    }

    public async Task<Result<IReadOnlyList<DeployFileDto>>> GetAllAsync(CancellationToken ct = default)
    {
        var r = await _service.GetAllAsync(ct);
        return r.IsSuccess
            ? Result.Success<IReadOnlyList<DeployFileDto>>(r.Value.Select(ToDto).ToList())
            : Result.Failure<IReadOnlyList<DeployFileDto>>(r.Error);
    }

    public async Task<Result<DeployFileDto>> GetLatestAsync(CancellationToken ct = default)
    {
        var r = await _service.GetLatestAsync(ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<DeployFileDto>(r.Error);
    }

    public Task<Result> SetActiveAsync(string fileId, bool isActive, CancellationToken ct = default)
        => _service.SetActiveAsync(fileId, isActive, ct);

    public async Task<Result<DeployDownloadDto>> OpenDownloadAsync(string fileId, CancellationToken ct = default)
    {
        var r = await _service.OpenDownloadAsync(fileId, ct);
        return r.IsSuccess
            ? Result.Success(new DeployDownloadDto(ToDto(r.Value.File), r.Value.Content))
            : Result.Failure<DeployDownloadDto>(r.Error);
    }

    private static DeployFileDto ToDto(DeployFile f)
        => new(f.FileId, f.Version, f.FileName, f.Hash, f.FileSize,
            f.Description, f.ForceUpdate, f.IsActive, f.UploadedBy, f.UploadedAt);
}
