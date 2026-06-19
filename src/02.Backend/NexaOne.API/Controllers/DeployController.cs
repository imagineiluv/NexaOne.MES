using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.API.Extensions;
using NexaOne.Common;
using NexaOne.SYS.Application.Deploys;
using NexaOne.SYS.Domain;

namespace NexaOne.API.Controllers;

/// <summary>§20.11 — 배포 파일 업로드 / 클라이언트 자동 업데이트.</summary>
[ApiController]
[Route("api/v1/deploy")]
[Authorize]
public class DeployController(DeployService deployService) : ControllerBase
{
    /// <summary>업로드 파일 상한(500MB) — 배포 패키지 크기 여유분 포함. Kestrel 기본 30MB 제한을 대체한다.</summary>
    public const long MaxUploadBytes = 500L * 1024 * 1024;

    /// <summary>요청 본문 한도 — multipart 경계/폼 필드 오버헤드 여유분을 더해
    /// 정확히 500MB인 파일이 413으로 거부되지 않게 한다 (파일 자체는 Upload에서 명시 검사).</summary>
    public const long MaxRequestBytes = MaxUploadBytes + 1024 * 1024;

    private string CurrentUserId =>
        User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value ?? string.Empty;

    /// <summary>
    /// 최신 버전 확인 — 클라이언트가 로그인 전에 호출하므로 익명 허용.
    /// 배포 메타데이터는 비밀이 아니며 사내망 전용 서비스라는 전제 (설계 20.11 웹 적응).
    /// </summary>
    [HttpGet("latest")]
    [AllowAnonymous]
    [ProducesResponseType<DeployLatestDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<Error>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetLatest(CancellationToken ct)
    {
        var result = await deployService.GetLatestAsync(ct);
        if (result.IsFailure) return NotFound(result.Error);

        var f = result.Value;
        return Ok(new DeployLatestDto(
            f.Version, $"/api/v1/deploy/files/{f.FileId}/download", f.Hash, f.ForceUpdate, f.Description));
    }

    [HttpGet("files")]
    [Authorize(Policy = "perm:deploy:manage")]   // ADR-003 — 역할 하드코딩 → 권한 정책(ADMIN=* 보유)
    [ProducesResponseType<IEnumerable<DeployFileDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetFiles(CancellationToken ct)
    {
        var result = await deployService.GetAllAsync(ct);
        return result.ToActionResult(files => Ok(files.Select(ToDto)));
    }

    [HttpPost("files")]
    [Authorize(Policy = "perm:deploy:manage")]   // ADR-003 — 역할 하드코딩 → 권한 정책(ADMIN=* 보유)
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    [ProducesResponseType<DeployFileDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<Error>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<Error>(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Upload(
        IFormFile? file,
        [FromForm] string? version,
        [FromForm] string? description,
        [FromForm] bool forceUpdate,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(Error.Validation("업로드할 파일을 선택해 주세요."));
        // 파일 상한은 스트림을 열기 전에 검사한다 — Kestrel 413 대신 사유 있는 400을 돌려준다
        if (file.Length > MaxUploadBytes)
            return BadRequest(Error.Validation($"파일은 {MaxUploadBytes / (1024 * 1024)}MB 이하여야 합니다."));

        await using var stream = file.OpenReadStream();
        var result = await deployService.UploadAsync(
            version ?? string.Empty, file.FileName, description, forceUpdate, stream, CurrentUserId, ct);
        return result.ToActionResult(file => Ok(ToDto(file)));
    }

    /// <summary>
    /// 파일 다운로드 — enableRangeProcessing으로 이어받기(HTTP Range)를 지원한다 (설계 20.11).
    /// latest와 동일하게 로그인 전 클라이언트 업데이트 경로라 익명 허용.
    /// </summary>
    [HttpGet("files/{fileId}/download")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(FileResult))]
    [ProducesResponseType<Error>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(string fileId, CancellationToken ct)
    {
        var result = await deployService.OpenDownloadAsync(fileId, ct);
        if (result.IsFailure) return NotFound(result.Error);

        return File(result.Value.Content, "application/octet-stream",
            result.Value.File.FileName, enableRangeProcessing: true);
    }

    /// <summary>문제 버전 회수 — latest 선정/다운로드에서 제외한다.</summary>
    [HttpPut("files/{fileId}/deactivate")]
    [Authorize(Policy = "perm:deploy:manage")]   // ADR-003 — 역할 하드코딩 → 권한 정책(ADMIN=* 보유)
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<Error>(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Deactivate(string fileId, CancellationToken ct)
    {
        var result = await deployService.SetActiveAsync(fileId, isActive: false, ct: ct);
        return result.ToActionResult();
    }

    [HttpPut("files/{fileId}/activate")]
    [Authorize(Policy = "perm:deploy:manage")]   // ADR-003 — 역할 하드코딩 → 권한 정책(ADMIN=* 보유)
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<Error>(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Activate(string fileId, CancellationToken ct)
    {
        var result = await deployService.SetActiveAsync(fileId, isActive: true, ct: ct);
        return result.ToActionResult();
    }

    private static DeployFileDto ToDto(DeployFile f) => new(
        f.FileId, f.Version, f.FileName, f.Hash, f.FileSize,
        f.Description, f.ForceUpdate, f.IsActive, f.UploadedBy, f.UploadedAt);
}

public record DeployLatestDto(string Version, string DownloadUrl, string Hash, bool ForceUpdate, string ReleaseNote);
public record DeployFileDto(
    string FileId, string Version, string FileName, string Hash, long FileSize,
    string Description, bool ForceUpdate, bool IsActive, string UploadedBy, DateTime UploadedAt);
