using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.SYS.Application.Screens;

namespace NexaOne.API.Controllers;

/// <summary>
/// Phase 4 후속 — Low-Code 화면 정의(메타) 저장소 API. 정의 JSON은 불투명 저장(구조는 프론트 소유).
/// 조회는 인증, 저장은 관리 권한(perm:sys:manage, ADR-003).
/// </summary>
[ApiController]
[Route("api/v1/sys/screen-definitions")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public class ScreenDefinitionController(IScreenDefinitionStore store) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ScreenDefinitionRecord>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await store.GetAllAsync(ct));

    [HttpGet("{uiId}")]
    [ProducesResponseType<ScreenDefinitionRecord>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Get(string uiId, CancellationToken ct)
    {
        var def = await store.GetAsync(uiId, ct);
        return def is null ? NotFound() : Ok(def);
    }

    [HttpPut("{uiId}")]
    [Authorize(Policy = "perm:sys:manage")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Upsert(string uiId, [FromBody] SaveScreenDefinitionRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Title) || string.IsNullOrWhiteSpace(req.DefinitionJson))
            return BadRequest(new Error("Screen.ValidationFailed", "Title and DefinitionJson are required.", ErrorType.Validation));
        await store.UpsertAsync(new ScreenDefinitionRecord(uiId, req.Title, req.DefinitionJson), ct);
        return Ok();
    }
}

public record SaveScreenDefinitionRequest(string Title, string DefinitionJson);
