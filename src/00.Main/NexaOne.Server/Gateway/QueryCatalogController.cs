using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Application.Query;
using NexaOne.Common;
using NexaOne.Common.Security;

namespace NexaOne.Server.Gateway;

/// <summary>Low-Code 디자이너용 쿼리 카탈로그(Phase 5a) — 호스트 IQueryRegistry의 등록 쿼리를
/// {id, isWrite, requiredPermission}로 노출(SQL 비노출). 디자이너 드롭다운 단일 출처. sys:manage 수동 검사.</summary>
[ApiController]
[Route("api/v1/sys/queries")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public sealed class QueryCatalogController : ControllerBase
{
    private readonly IQueryRegistry _registry;

    public QueryCatalogController(IQueryRegistry registry) => _registry = registry;

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<QueryDescriptor>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult List()
    {
        if (!HasPermission(Permissions.SysManage)) return Forbid();
        var items = new List<QueryDescriptor>();
        foreach (var id in _registry.Ids)
            if (_registry.TryGet(id, out var def) && def is not null)
                items.Add(new QueryDescriptor(def.Id, def.IsWrite, def.RequiredPermission));
        items.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return Ok(items);
    }

    private bool HasPermission(string permission) =>
        User.FindAll(Permissions.ClaimType)
            .Any(c => c.Value == Permissions.All || string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));
}

public sealed record QueryDescriptor(string Id, bool IsWrite, string? RequiredPermission);
