using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.Server.Gateway;

/// <summary>배포 파일 업로드/클라이언트 자동 업데이트(§20.11, ADR-008 얇은 브리지). plugin-ALC DeployService에
/// IDeployBridge로 위임한다 — 버전 형식/파일명 검증·System.Version latest 선정·비활성 회수는 모듈이 소유.
/// 관리(업로드/목록/활성 전환)는 sys:manage, 클라이언트 소비(latest/download)는 인증만 요구한다
/// (데스크톱 클라이언트가 로그인 토큰으로 기동 시 버전 확인). (modules ON에서만 동작.)
/// 주의: 배포 메타는 dev 시드 금지 — 오염 시 클라이언트 latest 선정이 깨진다.</summary>
[ApiController]
[Route("api/v1/deploy")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public sealed class SysDeployController : ControllerBase
{
    private readonly IDeployBridge _bridge;
    public SysDeployController(IDeployBridge bridge) => _bridge = bridge;

    [HttpGet("files")]
    [ProducesResponseType<IReadOnlyList<DeployFileDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.SysManage)]
    public async Task<IActionResult> GetFiles(CancellationToken ct)
        => (await _bridge.GetAllAsync(ct)).ToActionResult();

    /// <summary>활성 최신 버전 — 클라이언트가 기동 시 버전 비교에 사용(인증만, 권한 불요).</summary>
    [HttpGet("latest")]
    [ProducesResponseType<DeployFileDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetLatest(CancellationToken ct)
        => (await _bridge.GetLatestAsync(ct)).ToActionResult();

    /// <summary>업로드(multipart) — 버전 중복 409, 형식 오류 400. 스트림은 되감기 없이 1회 저장(SHA-256 동시 계산).</summary>
    [HttpPost("files")]
    [RequestSizeLimit(512L * 1024 * 1024)]   // 클라이언트 패키지 상한 512MB — 기본 30MB 제한을 배포 파일에 맞게 완화
    [ProducesResponseType<DeployFileDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.SysManage)]
    public async Task<IActionResult> Upload(
        [FromForm] IFormFile? file, [FromForm] string version, [FromForm] string? description,
        [FromForm] bool forceUpdate, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new Error("FILE_REQUIRED", "업로드할 파일이 없습니다.", ErrorType.Validation));

        await using var content = file.OpenReadStream();
        var uploadedBy = User.CurrentUserId() ?? "SYSTEM";
        return (await _bridge.UploadAsync(version, file.FileName, description, forceUpdate, content, uploadedBy, ct))
            .ToActionResult();
    }

    [HttpPost("files/{fileId}/activate")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.SysManage)]
    public async Task<IActionResult> Activate(string fileId, CancellationToken ct)
        => (await _bridge.SetActiveAsync(fileId, isActive: true, ct)).ToActionResult();

    [HttpPost("files/{fileId}/deactivate")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.SysManage)]
    public async Task<IActionResult> Deactivate(string fileId, CancellationToken ct)
        => (await _bridge.SetActiveAsync(fileId, isActive: false, ct)).ToActionResult();

    /// <summary>다운로드 — 비활성/미존재는 404. 파일명은 업로드 시 경로/제어문자 검증을 통과한 값이다.</summary>
    [HttpGet("files/{fileId}/download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(string fileId, CancellationToken ct)
    {
        var r = await _bridge.OpenDownloadAsync(fileId, ct);
        if (r.IsFailure)
            return r.ToActionResult();
        // FileStreamResult가 스트림 폐기를 소유한다. Content-Disposition 파일명은 업로드 검증 통과값.
        return File(r.Value.Content, "application/octet-stream", r.Value.File.FileName);
    }
}
