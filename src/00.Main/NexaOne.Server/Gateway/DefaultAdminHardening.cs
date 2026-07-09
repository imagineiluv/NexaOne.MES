using NexaOne.Application.Messaging;

namespace NexaOne.Server.Gateway;

/// <summary>운영 기본 자격 하드닝 — V001이 시드한 admin/admin(SHA-256)이 Production에서 그대로
/// 활성이면 PASSWORD_STATE='Create'로 강제해 기존 강제변경 흐름(PasswordChangeRequiredMiddleware +
/// change-password 자기해제)에 태운다. 마이그레이션이 아닌 기동 가드인 이유: 마이그레이션 UPDATE는
/// dev SQLite 전체 적용 경로에도 타서 로컬 admin/admin 개발 흐름·테스트를 깨뜨린다(환경 분기 불가).
/// 비밀번호를 이미 바꾼 운영 admin(해시 상이)은 건드리지 않는다 — 조건이 기본 해시 일치일 때만.</summary>
public static class DefaultAdminHardening
{
    /// <summary>SHA-256("admin") — V001 시드 원문 해시(공개 기본값이라 비밀 아님).</summary>
    internal const string DefaultAdminSha256 = "8c6976e5b5410415bde908bd4dee15dfb167a9c873fc4bb8a81f6f2ab448a918";

    /// <summary>기본 해시로 활성(Normal) 상태인 admin만 'Create'로 전이. 반환=영향 행 수(0=조치 불필요).</summary>
    public static async Task<int> HardenAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IRuleDispatcher>();
        return await dispatcher.ExecuteAsync(
            "UPDATE SYS_USER SET PASSWORD_STATE = 'Create', UPDATED_BY = 'SYSTEM', UPDATED_AT = @now " +
            "WHERE USER_ID = 'admin' AND PASSWORD_HASH = @hash AND PASSWORD_STATE = 'Normal'",
            new Dictionary<string, object> { ["now"] = DateTime.UtcNow, ["hash"] = DefaultAdminSha256 }, ct);
    }
}
