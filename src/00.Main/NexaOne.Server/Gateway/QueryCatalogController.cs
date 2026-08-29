using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Application.Query;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.Web.Services.Meta;

namespace NexaOne.Server.Gateway;

/// <summary>Low-Code 디자이너용 쿼리/명령 카탈로그입니다.
/// 기존 id/isWrite/requiredPermission 계약을 유지하면서 출처, 변경 여부, 실행 방식을 함께 노출합니다.</summary>
[ApiController]
[Route("api/v1/sys/queries")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public sealed class QueryCatalogController : ControllerBase
{
    private readonly IQueryRegistry _registry;
    private readonly IMetaCommandDriverCatalog _commands;

    public QueryCatalogController(IQueryRegistry registry, IMetaCommandDriverCatalog commands)
    {
        _registry = registry;
        _commands = commands;
    }

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<QueryDescriptor>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.SysManage)]
    public IActionResult List()
    {
        var items = new List<QueryDescriptor>();
        foreach (var id in _registry.Ids)
            if (_registry.TryGet(id, out var def) && def is not null)
                items.Add(new QueryDescriptor(
                    def.Id,
                    def.IsWrite,
                    def.RequiredPermission,
                    QueryCatalogSource.NamedQuery,
                    def.IsWrite ? MetaCommandEffect.Mutating : MetaCommandEffect.NonMutating,
                    MetaCommandExecutionMode.PerRow));
        // SQL 명명 쿼리와 typed bridge 액션을 같은 Designer 드롭다운에 노출한다.
        // 액션은 ID/권한 힌트만 공개하며 구현 URL이나 SQL은 노출하지 않는다.
        foreach (var command in _commands.Commands)
            items.Add(new QueryDescriptor(
                command.Id,
                IsWrite: true,
                command.RequiredPermission,
                QueryCatalogSource.BridgeCommand,
                command.Effect,
                command.ExecutionMode));
        items.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return Ok(items);
    }

}

/// <summary>Designer 카탈로그 항목의 서버 실행 경계입니다.</summary>
public enum QueryCatalogSource
{
    NamedQuery,
    BridgeCommand,
}

/// <summary>
/// 기존 3개 필드 생성자와 JSON을 유지하며 descriptor 메타데이터를 확장한 카탈로그 DTO입니다.
/// BridgeCommand의 <see cref="IsWrite"/>는 기존 Designer의 action 그룹 호환을 위해 true를 유지하고,
/// 실제 변경 여부는 <see cref="Effect"/>가 권위 값입니다.
/// </summary>
public sealed record QueryDescriptor(
    string Id,
    bool IsWrite,
    string? RequiredPermission,
    QueryCatalogSource Source = QueryCatalogSource.NamedQuery,
    MetaCommandEffect Effect = MetaCommandEffect.Mutating,
    MetaCommandExecutionMode ExecutionMode = MetaCommandExecutionMode.PerRow);
