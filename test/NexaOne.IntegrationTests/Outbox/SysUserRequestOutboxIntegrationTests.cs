using Microsoft.Extensions.DependencyInjection;
using NexaOne.Common;
using NexaOne.SYS.Application.Users;
using NexaOne.SYS.Domain;

namespace NexaOne.IntegrationTests.Outbox;

/// <summary>
/// ADR-002 — 사용자 신청(UserRequest) 애그리거트의 트랜잭션 outbox. 승인/반려 시 도메인 이벤트가 신청 행과
/// '동일 트랜잭션'에 EES_OUTBOX로 기록되는지(opt-in)를 SQLite에서 검증한다. 활성이면 전이 각 1행, 비활성(기본)이면
/// 0행. outbox 행은 디스패처 발행 여부와 무관하게 DB를 직접 조회해 결정론적으로 확인한다. 영속 후 GetById로 다시
/// 로드해 전이→저장하므로(reload-then-mutate), 읽기경로가 Restore 아닌 Create+재생이면 팬텀 이벤트가 COUNT>1로 드러난다.
/// (테스트 SQLite는 FK off라 미등록 부모 없이 신청 INSERT가 가능 — 여기서는 outbox 트랜잭션 경로만 검증한다.)
/// </summary>
public sealed class SysUserRequestOutboxIntegrationTests : OutboxIntegrationTestBase
{
    public SysUserRequestOutboxIntegrationTests(TestApiFactory off, OutboxEnabledTestApiFactory on)
        : base(off, on)
    {
    }

    // 신청을 Request로 영속한다(생성은 이벤트를 발행하지 않음 — 전이만 발행).
    private static async Task SeedRequestAsync(IServiceProvider sp, string requestId, string userId)
    {
        var repo = sp.GetRequiredService<IUserRequestRepository>();
        var request = UserRequest.Create(requestId, userId, "신청자-OB", "ob@test.com", "생산팀", "사원",
            "P-OB", LanguageType.KoKr, termsAccepted: true, DateTime.UtcNow, "10.0.0.9").Value;
        await repo.AddAsync(request);
    }

    [Fact]
    public async Task Approve_writes_outbox_in_same_transaction_when_enabled()
    {
        using var scope = On.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUserRequestRepository>();
        await SeedRequestAsync(scope.ServiceProvider, "UR-OB-APPR", "ob-appr");

        var request = await repo.GetByIdAsync("UR-OB-APPR");   // Restore — 이벤트 없음
        request!.Approve("admin", DateTime.UtcNow);            // UserRequestApprovedDomainEvent 발행
        await repo.UpdateAsync(request);

        OutboxCount(On.ConnectionString, "UR-OB-APPR", "UserRequestApproved").Should().Be(1,
            "outbox 활성 시 승인과 같은 트랜잭션에 UserRequestApproved 이벤트가 1건 기록돼야 한다(재로드 후 전이라 팬텀이면 >1)");
    }

    [Fact]
    public async Task Reject_writes_outbox_in_same_transaction_when_enabled()
    {
        using var scope = On.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUserRequestRepository>();
        await SeedRequestAsync(scope.ServiceProvider, "UR-OB-REJ", "ob-rej");

        var request = await repo.GetByIdAsync("UR-OB-REJ");    // Restore — 이벤트 없음
        request!.Reject("admin", "부서 확인 불가", DateTime.UtcNow);  // UserRequestRejectedDomainEvent 발행
        await repo.UpdateAsync(request);

        OutboxCount(On.ConnectionString, "UR-OB-REJ", "UserRequestRejected").Should().Be(1,
            "outbox 활성 시 반려와 같은 트랜잭션에 UserRequestRejected 이벤트가 1건 기록돼야 한다");
    }

    [Fact]
    public async Task Approve_does_not_write_outbox_when_disabled()
    {
        using var scope = Off.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUserRequestRepository>();
        await SeedRequestAsync(scope.ServiceProvider, "UR-OB-OFF", "ob-off");

        var request = await repo.GetByIdAsync("UR-OB-OFF");
        request!.Approve("admin", DateTime.UtcNow);
        await repo.UpdateAsync(request);

        var persisted = await repo.GetByIdAsync("UR-OB-OFF");
        persisted.Should().NotBeNull("outbox 비활성이어도 신청은 정상 기록돼야 한다");
        persisted!.Status.Should().Be(UserRequestStatus.Approved, "비활성 경로에서도 상태 전이는 영속돼야 한다");

        OutboxCount(Off.ConnectionString, "UR-OB-OFF", "UserRequestApproved").Should().Be(0,
            "outbox 비활성(기본)에서는 outbox 행을 기록하지 않아야 한다(적체 없음)");
    }
}
