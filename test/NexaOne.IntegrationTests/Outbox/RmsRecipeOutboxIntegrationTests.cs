using Microsoft.Extensions.DependencyInjection;
using NexaOne.RMS.Application.Rms;
using NexaOne.RMS.Domain;

namespace NexaOne.IntegrationTests.Outbox;

/// <summary>
/// ADR-002 — RMS 레시피 승인 워크플로 전이 시 도메인 이벤트가 레시피 행과 '동일 트랜잭션'에 EES_OUTBOX로
/// 기록되는지(opt-in)를 SQLite에서 검증한다. 활성이면 전이 1건, 비활성(기본)이면 0행. outbox 행은 디스패처
/// 발행 여부와 무관하게 DB를 직접 조회해 결정론적으로 확인한다. 영속→리포 getter로 재로드→전이 1회→저장
/// 순서로 검증해(reload-then-mutate) 읽기경로 재구성이 유령 이벤트를 발행하지 않음도 함께 확인한다.
/// (테스트 SQLite는 FK off라 미등록 설비클래스 레시피 INSERT가 가능 — 여기서는 outbox 트랜잭션 경로만 검증한다.)
/// </summary>
public sealed class RmsRecipeOutboxIntegrationTests : OutboxIntegrationTestBase
{
    public RmsRecipeOutboxIntegrationTests(TestApiFactory off, OutboxEnabledTestApiFactory on)
        : base(off, on)
    {
    }

    // Draft 레시피를 리포의 트랜잭션 경로로 기록한다(이벤트 없음 — Create는 이벤트를 발행하지 않는다).
    private static async Task SeedDraftAsync(IServiceProvider sp, string recipeId, string equipmentClassId)
    {
        var repo = sp.GetRequiredService<IRecipeRepository>();
        var recipe = Recipe.Create(recipeId, "Etch Recipe", "desc", equipmentClassId).Value;
        await repo.AddAsync(recipe);
    }

    [Fact]
    public async Task Recipe_request_approval_writes_outbox_in_same_transaction_when_enabled()
    {
        using var scope = On.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRecipeRepository>();
        await SeedDraftAsync(scope.ServiceProvider, "R-OB-ON", "EC-OB-ON");

        var recipe = await repo.GetByIdAsync("R-OB-ON");   // Restore — 이벤트 없음(유령 이벤트 회귀 탐지)
        recipe!.RequestApproval();                          // RecipeApprovalRequestedDomainEvent 발행
        await repo.UpdateAsync(recipe);

        OutboxCount(On.ConnectionString, "R-OB-ON", "RecipeApprovalRequested").Should().Be(1,
            "outbox 활성 시 승인요청과 같은 트랜잭션에 RecipeApprovalRequested 이벤트가 정확히 1건 기록돼야 한다");
    }

    [Fact]
    public async Task Recipe_request_approval_does_not_write_outbox_when_disabled()
    {
        using var scope = Off.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRecipeRepository>();
        await SeedDraftAsync(scope.ServiceProvider, "R-OB-OFF", "EC-OB-OFF");

        var recipe = await repo.GetByIdAsync("R-OB-OFF");
        recipe!.RequestApproval();
        await repo.UpdateAsync(recipe);

        var persisted = await repo.GetByIdAsync("R-OB-OFF");
        persisted.Should().NotBeNull("outbox 비활성이어도 레시피 전이는 정상 기록돼야 한다");
        persisted!.ApprovalState.Should().Be(RecipeApprovalState.WaitApproval, "전이 결과가 영속돼야 한다");

        OutboxCount(Off.ConnectionString, "R-OB-OFF", "RecipeApprovalRequested").Should().Be(0,
            "outbox 비활성(기본)에서는 outbox 행을 기록하지 않아야 한다(적체 없음)");
    }
}
