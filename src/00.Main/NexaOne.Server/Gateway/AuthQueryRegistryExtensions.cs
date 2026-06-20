using NexaOne.Application.Query;

namespace NexaOne.Server.Gateway;

/// <summary>격리 인증 레지스트리에서 명명 쿼리 id→SQL 해석(미등록은 기동/요청 시 fail-fast).
/// SysRefreshTokenStore·GatewayLoginService가 공유해 중복을 제거한다.</summary>
internal static class AuthQueryRegistryExtensions
{
    public static string Sql(this IQueryRegistry registry, string id)
        => registry.TryGet(id, out var def) && def is not null
            ? def.Sql
            : throw new InvalidOperationException(
                $"인증 명명 쿼리 '{id}'가 격리 레지스트리에 없습니다(db/queries-auth/{registry.Dialect}).");
}
