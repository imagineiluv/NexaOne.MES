using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Application.Query;
using NexaOne.Common;

namespace NexaOne.API.Controllers;

/// <summary>
/// Low-Code 디자이너용 쿼리 카탈로그. 파일 기반 레지스트리의 등록 쿼리를 {id, isWrite, requiredPermission}로
/// 노출한다(SQL 본문은 노출하지 않음 — 주입/정보유출 방지). 디자이너가 그리드/명령 바인딩 드롭다운을 채우고,
/// 위젯의 UX 권한 비활성을 쿼리의 실제 requiredPermission에서 유도하는 단일 출처. 관리 권한 전용(ADR-003).
/// </summary>
[ApiController]
[Route("api/v1/sys/queries")]
[Authorize(Policy = "perm:sys:manage")]
[ProducesErrorResponseType(typeof(Error))]
public class QueryCatalogController(IQueryRegistry registry) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<QueryDescriptor>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult List()
    {
        var items = new List<QueryDescriptor>();
        foreach (var id in registry.Ids)
            if (registry.TryGet(id, out var def) && def is not null)
                items.Add(new QueryDescriptor(def.Id, def.IsWrite, def.RequiredPermission));
        items.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return Ok(items);
    }
}

/// <summary>디자이너에 노출하는 안전한 쿼리 서술자(SQL 제외).</summary>
public sealed record QueryDescriptor(string Id, bool IsWrite, string? RequiredPermission);
