using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaOne.SYS.Application.Screens;

namespace NexaOne.API.Controllers;

/// <summary>
/// Phase 4 후속 — Low-Code 화면 정의(메타) 저장소 API. 정의 JSON은 불투명 저장(구조는 프론트 소유).
/// 조회는 인증, 저장은 관리 권한(perm:sys:manage, ADR-003).
/// </summary>
[ApiController]
[Route("api/v1/sys/screen-definitions")]
[Authorize]
public class ScreenDefinitionController(IScreenDefinitionStore store) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await store.GetAllAsync(ct));

    [HttpGet("{uiId}")]
    public async Task<IActionResult> Get(string uiId, CancellationToken ct)
    {
        var def = await store.GetAsync(uiId, ct);
        return def is null ? NotFound() : Ok(def);
    }

    [HttpPut("{uiId}")]
    [Authorize(Policy = "perm:sys:manage")]
    public async Task<IActionResult> Upsert(string uiId, [FromBody] SaveScreenDefinitionRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Title) || string.IsNullOrWhiteSpace(req.DefinitionJson))
            return BadRequest("Title and DefinitionJson are required.");
        await store.UpsertAsync(new ScreenDefinitionRecord(uiId, req.Title, req.DefinitionJson), ct);
        return Ok();
    }
}

public record SaveScreenDefinitionRequest(string Title, string DefinitionJson);
