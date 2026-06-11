using NexaOne.SYS.Application.Conditions;
using NexaOne.SYS.Domain;

namespace NexaOne.UnitTests.Services;

public sealed class ConditionSettingServiceTests
{
    private static ConditionSetting Saved(string name, DateTime savedAt, string menuId = "/fdc/monitor") =>
        ConditionSetting.Create("user1", menuId, name, "{}", savedAt);

    private static ConditionSettingService BuildService(Mock<IConditionSettingRepository> repo, int max = 10)
        => new(repo.Object, max);

    // ── SaveConditionAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task SaveCondition_valid_upserts_and_succeeds()
    {
        var repo = new Mock<IConditionSettingRepository>();
        repo.Setup(r => r.GetByMenuAsync("user1", "/fdc/monitor", default))
            .ReturnsAsync(new List<ConditionSetting> { Saved("일별 조회", DateTime.UtcNow) });

        var result = await BuildService(repo)
            .SaveConditionAsync("user1", "/FDC/Monitor", "일별 조회", """{"parameterid":"P1"}""");

        result.IsSuccess.Should().BeTrue();
        result.Value.MenuId.Should().Be("/fdc/monitor");   // menuId 소문자 정규화
        result.Value.Name.Should().Be("일별 조회");
        repo.Verify(r => r.UpsertAsync(It.Is<ConditionSetting>(s =>
            s.MenuId == "/fdc/monitor" && s.Name == "일별 조회"), default), Times.Once);
        repo.Verify(r => r.DeleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
    }

    [Fact]
    public async Task SaveCondition_empty_name_fails()
    {
        var repo = new Mock<IConditionSettingRepository>();

        var result = await BuildService(repo)
            .SaveConditionAsync("user1", "/fdc/monitor", "  ", "{}");

        result.IsFailure.Should().BeTrue();
        repo.Verify(r => r.UpsertAsync(It.IsAny<ConditionSetting>(), default), Times.Never);
    }

    [Fact]
    public async Task SaveCondition_reserved_latest_name_fails()
    {
        var repo = new Mock<IConditionSettingRepository>();

        var result = await BuildService(repo)
            .SaveConditionAsync("user1", "/fdc/monitor", ConditionSetting.LatestName, "{}");

        result.IsFailure.Should().BeTrue();
        repo.Verify(r => r.UpsertAsync(It.IsAny<ConditionSetting>(), default), Times.Never);
    }

    [Fact]
    public async Task SaveCondition_over_limit_deletes_oldest_user_conditions()
    {
        // 한도 3, 저장 후 사용자 조건 5개 + latest 1개 → 가장 오래된 2개만 삭제, latest는 제외
        var now = DateTime.UtcNow;
        var existing = new List<ConditionSetting>
        {
            Saved(ConditionSetting.LatestName, now.AddDays(-30)),   // latest — 한도 미포함
            Saved("oldest", now.AddDays(-5)),
            Saved("older",  now.AddDays(-4)),
            Saved("mid",    now.AddDays(-3)),
            Saved("recent", now.AddDays(-2)),
            Saved("new",    now),
        };
        var repo = new Mock<IConditionSettingRepository>();
        repo.Setup(r => r.GetByMenuAsync("user1", "/fdc/monitor", default)).ReturnsAsync(existing);

        var result = await BuildService(repo, max: 3)
            .SaveConditionAsync("user1", "/fdc/monitor", "new", "{}");

        result.IsSuccess.Should().BeTrue();
        repo.Verify(r => r.DeleteAsync("user1", "/fdc/monitor", "oldest", default), Times.Once);
        repo.Verify(r => r.DeleteAsync("user1", "/fdc/monitor", "older", default), Times.Once);
        repo.Verify(r => r.DeleteAsync("user1", "/fdc/monitor", "mid", default), Times.Never);
        repo.Verify(r => r.DeleteAsync("user1", "/fdc/monitor", ConditionSetting.LatestName, default), Times.Never);
    }

    [Fact]
    public async Task SaveCondition_at_limit_overwrite_does_not_delete()
    {
        // 한도 3에 정확히 3개(덮어쓰기 포함) → 삭제 없음
        var now = DateTime.UtcNow;
        var existing = new List<ConditionSetting>
        {
            Saved("a", now.AddDays(-2)),
            Saved("b", now.AddDays(-1)),
            Saved("c", now),
        };
        var repo = new Mock<IConditionSettingRepository>();
        repo.Setup(r => r.GetByMenuAsync("user1", "/fdc/monitor", default)).ReturnsAsync(existing);

        var result = await BuildService(repo, max: 3)
            .SaveConditionAsync("user1", "/fdc/monitor", "c", "{}");

        result.IsSuccess.Should().BeTrue();
        repo.Verify(r => r.DeleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
    }

    // ── SaveLatestAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task SaveLatest_upserts_reserved_name()
    {
        var repo = new Mock<IConditionSettingRepository>();

        var result = await BuildService(repo)
            .SaveLatestAsync("user1", "/fdc/monitor", """{"parameterid":"P1"}""");

        result.IsSuccess.Should().BeTrue();
        result.Value.IsLatest.Should().BeTrue();
        repo.Verify(r => r.UpsertAsync(It.Is<ConditionSetting>(s =>
            s.Name == ConditionSetting.LatestName), default), Times.Once);
        // latest 저장은 한도 검사를 하지 않는다
        repo.Verify(r => r.GetByMenuAsync(It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
    }

    // ── DeleteConditionAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task DeleteCondition_existing_succeeds()
    {
        var repo = new Mock<IConditionSettingRepository>();
        repo.Setup(r => r.GetAsync("user1", "/fdc/monitor", "일별 조회", default))
            .ReturnsAsync(Saved("일별 조회", DateTime.UtcNow));

        var result = await BuildService(repo)
            .DeleteConditionAsync("user1", "/fdc/monitor", "일별 조회");

        result.IsSuccess.Should().BeTrue();
        repo.Verify(r => r.DeleteAsync("user1", "/fdc/monitor", "일별 조회", default), Times.Once);
    }

    [Fact]
    public async Task DeleteCondition_missing_fails()
    {
        var repo = new Mock<IConditionSettingRepository>();
        repo.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), default))
            .ReturnsAsync((ConditionSetting?)null);

        var result = await BuildService(repo)
            .DeleteConditionAsync("user1", "/fdc/monitor", "없는 조건");

        result.IsFailure.Should().BeTrue();
        repo.Verify(r => r.DeleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
    }

    [Fact]
    public async Task DeleteCondition_latest_is_protected()
    {
        // $latest는 수동 삭제 불가 — '최근 조건 초기화'로만 삭제 (설계 20.8)
        var repo = new Mock<IConditionSettingRepository>();

        var result = await BuildService(repo)
            .DeleteConditionAsync("user1", "/fdc/monitor", ConditionSetting.LatestName);

        result.IsFailure.Should().BeTrue();
        repo.Verify(r => r.DeleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
    }

    // ── ClearLatestAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task ClearLatest_deletes_latest_row()
    {
        var repo = new Mock<IConditionSettingRepository>();

        var result = await BuildService(repo).ClearLatestAsync("user1", "/FDC/Monitor");

        result.IsSuccess.Should().BeTrue();
        repo.Verify(r => r.DeleteAsync("user1", "/fdc/monitor", ConditionSetting.LatestName, default), Times.Once);
    }

    // ── GetConditionsAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task GetConditions_normalizes_menu_id()
    {
        var repo = new Mock<IConditionSettingRepository>();
        repo.Setup(r => r.GetByMenuAsync("user1", "/fdc/monitor", default))
            .ReturnsAsync(new List<ConditionSetting> { Saved("a", DateTime.UtcNow) });

        var result = await BuildService(repo).GetConditionsAsync("user1", "/FDC/Monitor ");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetConditions_missing_user_fails()
    {
        var repo = new Mock<IConditionSettingRepository>();

        var result = await BuildService(repo).GetConditionsAsync("", "/fdc/monitor");

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task GetConditions_missing_menu_fails()
    {
        var repo = new Mock<IConditionSettingRepository>();

        var result = await BuildService(repo).GetConditionsAsync("user1", "");

        result.IsFailure.Should().BeTrue();
    }

    // ── 생성자 한도 폴백 ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Ctor_non_positive_max_falls_back_to_default(int max)
    {
        var svc = new ConditionSettingService(new Mock<IConditionSettingRepository>().Object, max);
        svc.MaxSavedConditions.Should().Be(ConditionSettingService.DefaultMaxSavedConditions);
    }

    // ── 예약명 가드 ($ 접두 — DB 콜레이션 대소문자 무시 우회 차단) ────────────

    [Theory]
    [InlineData("$latest")]
    [InlineData("$Latest")]
    [InlineData("$LATEST")]
    [InlineData("$기타예약")]
    public async Task SaveCondition_dollar_prefixed_name_fails(string name)
    {
        var repo = new Mock<IConditionSettingRepository>();

        var result = await BuildService(repo).SaveConditionAsync("user1", "/fdc/monitor", name, "{}");

        result.IsFailure.Should().BeTrue();
        repo.Verify(r => r.UpsertAsync(It.IsAny<ConditionSetting>(), default), Times.Never);
    }

    [Theory]
    [InlineData("$Latest")]
    [InlineData("$LATEST")]
    [InlineData("$latest ")]   // 후행 공백 — ANSI 패딩으로 DB에서는 $latest와 동일하게 매칭된다
    public async Task DeleteCondition_latest_variants_are_protected(string name)
    {
        var repo = new Mock<IConditionSettingRepository>();

        var result = await BuildService(repo).DeleteConditionAsync("user1", "/fdc/monitor", name);

        result.IsFailure.Should().BeTrue();
        repo.Verify(r => r.DeleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
    }

    [Fact]
    public async Task DeleteCondition_whitespace_menu_id_fails()
    {
        // ?menuId=%20 같은 공백 menuId가 "/" 버킷으로 정규화되어 엉뚱한 조건을 지우지 않도록 차단
        var repo = new Mock<IConditionSettingRepository>();

        var result = await BuildService(repo).DeleteConditionAsync("user1", " ", "일별 조회");

        result.IsFailure.Should().BeTrue();
        repo.Verify(r => r.DeleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
    }

    [Fact]
    public async Task ClearLatest_whitespace_menu_id_fails()
    {
        var repo = new Mock<IConditionSettingRepository>();

        var result = await BuildService(repo).ClearLatestAsync("user1", " ");

        result.IsFailure.Should().BeTrue();
        repo.Verify(r => r.DeleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
    }

    // ── 조건명/메뉴/값 크기 경계 ──────────────────────────────────────────────

    [Fact]
    public async Task SaveCondition_name_at_100_chars_succeeds()
    {
        var repo = new Mock<IConditionSettingRepository>();
        repo.Setup(r => r.GetByMenuAsync(It.IsAny<string>(), It.IsAny<string>(), default))
            .ReturnsAsync(new List<ConditionSetting>());

        var result = await BuildService(repo)
            .SaveConditionAsync("user1", "/fdc/monitor", new string('가', 100), "{}");

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task SaveCondition_name_over_100_chars_fails()
    {
        var repo = new Mock<IConditionSettingRepository>();

        var result = await BuildService(repo)
            .SaveConditionAsync("user1", "/fdc/monitor", new string('가', 101), "{}");

        result.IsFailure.Should().BeTrue();
        repo.Verify(r => r.UpsertAsync(It.IsAny<ConditionSetting>(), default), Times.Never);
    }

    [Fact]
    public async Task SaveCondition_menu_id_over_limit_fails()
    {
        var repo = new Mock<IConditionSettingRepository>();
        var longMenuId = "/" + new string('a', ConditionSettingService.MaxMenuIdLength);

        var result = await BuildService(repo)
            .SaveConditionAsync("user1", longMenuId, "조건1", "{}");

        result.IsFailure.Should().BeTrue();
        repo.Verify(r => r.UpsertAsync(It.IsAny<ConditionSetting>(), default), Times.Never);
    }

    [Fact]
    public async Task SaveCondition_values_json_at_cap_succeeds()
    {
        var repo = new Mock<IConditionSettingRepository>();
        repo.Setup(r => r.GetByMenuAsync(It.IsAny<string>(), It.IsAny<string>(), default))
            .ReturnsAsync(new List<ConditionSetting>());

        var result = await BuildService(repo).SaveConditionAsync(
            "user1", "/fdc/monitor", "조건1", new string('x', ConditionSettingService.MaxValuesJsonLength));

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task SaveCondition_oversized_values_json_fails()
    {
        var repo = new Mock<IConditionSettingRepository>();

        var result = await BuildService(repo).SaveConditionAsync(
            "user1", "/fdc/monitor", "조건1", new string('x', ConditionSettingService.MaxValuesJsonLength + 1));

        result.IsFailure.Should().BeTrue();
        repo.Verify(r => r.UpsertAsync(It.IsAny<ConditionSetting>(), default), Times.Never);
    }

    [Fact]
    public async Task SaveLatest_oversized_values_json_fails()
    {
        var repo = new Mock<IConditionSettingRepository>();

        var result = await BuildService(repo).SaveLatestAsync(
            "user1", "/fdc/monitor", new string('x', ConditionSettingService.MaxValuesJsonLength + 1));

        result.IsFailure.Should().BeTrue();
        repo.Verify(r => r.UpsertAsync(It.IsAny<ConditionSetting>(), default), Times.Never);
    }

    [Fact]
    public async Task SaveLatest_missing_user_fails()
    {
        var repo = new Mock<IConditionSettingRepository>();

        var result = await BuildService(repo).SaveLatestAsync("", "/fdc/monitor", "{}");

        result.IsFailure.Should().BeTrue();
        repo.Verify(r => r.UpsertAsync(It.IsAny<ConditionSetting>(), default), Times.Never);
    }

    // ── menuId 후행 슬래시 정규화 ─────────────────────────────────────────────

    [Fact]
    public async Task SaveCondition_trailing_slash_menu_id_normalized()
    {
        var repo = new Mock<IConditionSettingRepository>();
        repo.Setup(r => r.GetByMenuAsync(It.IsAny<string>(), It.IsAny<string>(), default))
            .ReturnsAsync(new List<ConditionSetting>());

        var result = await BuildService(repo)
            .SaveConditionAsync("user1", "/FDC/Monitor/", "조건1", "{}");

        result.IsSuccess.Should().BeTrue();
        result.Value.MenuId.Should().Be("/fdc/monitor");
        repo.Verify(r => r.UpsertAsync(It.Is<ConditionSetting>(s => s.MenuId == "/fdc/monitor"), default), Times.Once);
    }

    [Fact]
    public async Task ClearLatest_root_path_stays_root()
    {
        var repo = new Mock<IConditionSettingRepository>();

        var result = await BuildService(repo).ClearLatestAsync("user1", "/");

        result.IsSuccess.Should().BeTrue();
        repo.Verify(r => r.DeleteAsync("user1", "/", ConditionSetting.LatestName, default), Times.Once);
    }

    // ── 도메인: IsLatest 대소문자 무시 ────────────────────────────────────────

    [Theory]
    [InlineData("$latest")]
    [InlineData("$Latest")]
    [InlineData("$LATEST")]
    public void IsLatest_is_case_insensitive(string name)
    {
        Saved(name, DateTime.UtcNow).IsLatest.Should().BeTrue();
    }
}
