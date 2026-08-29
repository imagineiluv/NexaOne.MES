using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;

namespace NexaOne.Server.Gateway;

/// <summary>
/// Designer가 코드 화면 시드를 발견하고, 검토한 시드를 DB 정의로 한 번만 가져오는 API입니다.
/// 가져온 뒤의 편집은 일반 화면 정의 저장 경로를 사용하며 이 API는 기존 DB 정의를 절대 갱신하지 않습니다.
/// </summary>
[ApiController]
[Route("api/v1/sys/screen-seeds")]
[Authorize]
[RequirePermission(Permissions.SysManage)]
[ProducesErrorResponseType(typeof(Error))]
public sealed class ScreenDefinitionSeedController : ControllerBase
{
    private readonly IScreenDefinitionSeedService _seeds;

    public ScreenDefinitionSeedController(IScreenDefinitionSeedService seeds) => _seeds = seeds;

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ScreenSeedSummary>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(CancellationToken ct)
        => Ok(await _seeds.ListAsync(ct));

    [HttpGet("{uiId}")]
    [ProducesResponseType<ScreenSeedPreview>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get([FromRoute] string uiId, CancellationToken ct)
    {
        var seed = await _seeds.GetAsync(uiId, ct);
        return seed is null
            ? NotFound(Error.NotFound("SCREEN_SEED_NOT_FOUND", $"Screen seed '{uiId}' was not found."))
            : Ok(seed);
    }

    [HttpPost("{uiId}/import")]
    [ProducesResponseType<ScreenSeedPreview>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Import([FromRoute] string uiId, CancellationToken ct)
    {
        var result = await _seeds.ImportAsync(uiId, User.CurrentUserId() ?? "SYSTEM", ct);
        return result.Status switch
        {
            ScreenSeedImportStatus.Imported => Ok(result.Preview),
            ScreenSeedImportStatus.NotFound => NotFound(Error.NotFound(
                "SCREEN_SEED_NOT_FOUND", $"Screen seed '{uiId}' was not found.")),
            ScreenSeedImportStatus.AlreadyExists => Conflict(Error.Conflict(
                "SCREEN_DEFINITION_ALREADY_EXISTS",
                $"Database screen definition '{result.Preview?.UiId ?? uiId}' already exists.")),
            ScreenSeedImportStatus.CapabilityInvalid => UnprocessableEntity(Error.Validation(
                "SCREEN_SEED_CAPABILITY_INVALID",
                $"Screen seed '{result.Preview?.UiId ?? uiId}' has capability errors and cannot be imported.")),
            _ => throw new InvalidOperationException($"Unsupported import status '{result.Status}'."),
        };
    }
}
