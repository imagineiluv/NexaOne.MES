using NexaOne.Application.Query;
using NexaOne.Common.Security;
using NexaOne.Web.Services.Meta;

namespace NexaOne.Server.Gateway;

/// <summary>
/// 메타 런타임의 권한 힌트를 실제 서버 실행 카탈로그와 연결합니다.
/// QueryGateway와 같은 <see cref="IQueryRegistry"/> 및 typed bridge 카탈로그를 사용하므로
/// Designer JSON에 권한이 빠져 있어도 권한 없는 호출을 UI 단계에서 차단합니다.
/// </summary>
public sealed class MetaPermissionCatalog : IMetaPermissionCatalog, IMetaPermissionEvaluator
{
    private readonly IQueryRegistry _queries;
    private readonly IMetaCommandDriverCatalog _commands;

    public MetaPermissionCatalog(IQueryRegistry queries, IMetaCommandDriverCatalog commands)
    {
        _queries = queries;
        _commands = commands;
    }

    public MetaBindingPermission ResolveRead(string queryId)
    {
        if (string.IsNullOrWhiteSpace(queryId)
            || !_queries.TryGet(queryId.Trim(), out var query)
            || query is null
            || query.IsWrite)
            return MetaBindingPermission.Unknown;

        // QueryGateway.CanExecute와 같은 우선순위: public read는 별도 권한이 없고,
        // 비공개 read는 requiredPermission이 있어야 등록된 binding으로 취급합니다.
        if (query.IsPublic) return MetaBindingPermission.Known(null);
        return !string.IsNullOrWhiteSpace(query.RequiredPermission)
            ? MetaBindingPermission.Known(query.RequiredPermission)
            : MetaBindingPermission.Unknown;
    }

    public MetaBindingPermission ResolveWrite(string commandId)
    {
        if (string.IsNullOrWhiteSpace(commandId)) return MetaBindingPermission.Unknown;
        var id = commandId.Trim();

        if (_queries.TryGet(id, out var query) && query is not null)
        {
            return query is { IsWrite: true, IsPublic: false }
                && !string.IsNullOrWhiteSpace(query.RequiredPermission)
                    ? MetaBindingPermission.Known(query.RequiredPermission)
                    : MetaBindingPermission.Unknown;
        }

        return _commands.TryGetDescriptor(id, out var command)
            && command is not null
            && !string.IsNullOrWhiteSpace(command.RequiredPermission)
            ? MetaBindingPermission.Known(command.RequiredPermission)
            : MetaBindingPermission.Unknown;
    }

    public bool HasPermission(System.Security.Claims.ClaimsPrincipal user, string requiredPermission)
        => user.HasPermission(requiredPermission);
}
