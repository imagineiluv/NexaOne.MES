using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NexaOne.API.Controllers;
using NexaOne.SYS.Application.Conditions;
using NexaOne.SYS.Application.Menus;
using NexaOne.SYS.Application.Users;
using NexaOne.SYS.Domain;

namespace NexaOne.UnitTests.Controllers;

/// <summary>SysController 조건 저장/불러오기 엔드포인트 (설계서 20.8) — DTO 변환·키 정규화 검증.</summary>
public sealed class SysControllerConditionTests
{
    private static ConditionSetting Saved(string name, string valuesJson = "{}") =>
        ConditionSetting.Create("user1", "/fdc/monitor", name, valuesJson, DateTime.UtcNow);

    private static SysController BuildController(Mock<IConditionSettingRepository> condRepo)
    {
        var userService = new UserService(
            new Mock<IUserRepository>().Object,
            new Mock<IRoleRepository>().Object,
            new Mock<IMultiLanguageResourceRepository>().Object,
            new Mock<ILoginFailureHistoryRepository>().Object);
        var menuService = new MenuService(
            new Mock<IMenuRepository>().Object,
            NullLogger<MenuService>.Instance);

        var controller = new SysController(
            userService, menuService, new ConditionSettingService(condRepo.Object),
            new UserMenuService(
                new Mock<IFavoriteMenuRepository>().Object,
                new Mock<IRecentMenuRepository>().Object,
                new Mock<IMenuRepository>().Object))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.NameIdentifier, "user1") }, "test"))
                }
            }
        };
        return controller;
    }

    // ── GetConditions ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetConditions_splits_latest_and_items()
    {
        var condRepo = new Mock<IConditionSettingRepository>();
        condRepo.Setup(r => r.GetByMenuAsync("user1", "/fdc/monitor", default))
            .ReturnsAsync(new List<ConditionSetting>
            {
                Saved(ConditionSetting.LatestName, """{"parameterid":"P1"}"""),
                Saved("일별 조회"),
            });

        var result = await BuildController(condRepo).GetConditions("/fdc/monitor", default);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<ConditionSettingDto>().Subject;
        dto.Latest.Should().NotBeNull();
        dto.Latest!.Values.Should().ContainKey("parameterid").WhoseValue.Should().Be("P1");
        dto.Items.Should().ContainSingle(i => i.Name == "일별 조회");
    }

    [Fact]
    public async Task GetConditions_uppercase_latest_name_treated_as_latest()
    {
        // 콜레이션 무시 DB에 대문자 변형으로 저장된 기존 행도 latest로 분류돼야 한다
        var condRepo = new Mock<IConditionSettingRepository>();
        condRepo.Setup(r => r.GetByMenuAsync("user1", "/fdc/monitor", default))
            .ReturnsAsync(new List<ConditionSetting> { Saved("$LATEST") });

        var result = await BuildController(condRepo).GetConditions("/fdc/monitor", default);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<ConditionSettingDto>().Subject;
        dto.Latest.Should().NotBeNull();
        dto.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task GetConditions_corrupt_values_json_returns_empty_values()
    {
        var condRepo = new Mock<IConditionSettingRepository>();
        condRepo.Setup(r => r.GetByMenuAsync("user1", "/fdc/monitor", default))
            .ReturnsAsync(new List<ConditionSetting> { Saved("깨진 조건", "{invalid json") });

        var result = await BuildController(condRepo).GetConditions("/fdc/monitor", default);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<ConditionSettingDto>().Subject;
        dto.Items.Should().ContainSingle().Which.Values.Should().BeEmpty();
    }

    [Fact]
    public async Task GetConditions_mixed_case_duplicate_keys_folded()
    {
        // 정규화 이전에 저장된 대소문자 중복 키가 복원 시 예외를 내지 않도록 소문자로 접는다
        var condRepo = new Mock<IConditionSettingRepository>();
        condRepo.Setup(r => r.GetByMenuAsync("user1", "/fdc/monitor", default))
            .ReturnsAsync(new List<ConditionSetting>
            {
                Saved("조건1", """{"ParameterId":"P1","parameterid":"P2"}"""),
            });

        var result = await BuildController(condRepo).GetConditions("/fdc/monitor", default);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<ConditionSettingDto>().Subject;
        var values = dto.Items.Should().ContainSingle().Which.Values;
        values.Should().HaveCount(1);
        values["parameterid"].Should().Be("P2");   // 마지막 값 우선
    }

    // ── SaveCondition / SaveLatestCondition ───────────────────────────────────

    [Fact]
    public async Task SaveCondition_normalizes_value_keys_to_lowercase()
    {
        var condRepo = new Mock<IConditionSettingRepository>();
        condRepo.Setup(r => r.GetByMenuAsync(It.IsAny<string>(), It.IsAny<string>(), default))
            .ReturnsAsync(new List<ConditionSetting>());
        ConditionSetting? upserted = null;
        condRepo.Setup(r => r.UpsertAsync(It.IsAny<ConditionSetting>(), default))
            .Callback<ConditionSetting, CancellationToken>((s, _) => upserted = s)
            .Returns(Task.CompletedTask);

        var req = new SaveConditionRequest("/fdc/monitor", "조건1",
            new Dictionary<string, string?> { ["ParameterId"] = "P1", ["Limit"] = "50" });
        var result = await BuildController(condRepo).SaveCondition(req, default);

        result.Should().BeOfType<OkObjectResult>();
        upserted.Should().NotBeNull();
        upserted!.ValuesJson.Should().Contain("\"parameterid\"").And.Contain("\"limit\"");
        upserted.ValuesJson.Should().NotContain("ParameterId");
    }

    [Fact]
    public async Task SaveLatestCondition_null_values_serializes_empty_object()
    {
        var condRepo = new Mock<IConditionSettingRepository>();
        ConditionSetting? upserted = null;
        condRepo.Setup(r => r.UpsertAsync(It.IsAny<ConditionSetting>(), default))
            .Callback<ConditionSetting, CancellationToken>((s, _) => upserted = s)
            .Returns(Task.CompletedTask);

        var req = new SaveLatestConditionRequest("/fdc/monitor", null);
        var result = await BuildController(condRepo).SaveLatestCondition(req, default);

        result.Should().BeOfType<OkObjectResult>();
        upserted.Should().NotBeNull();
        upserted!.ValuesJson.Should().Be("{}");
        upserted.IsLatest.Should().BeTrue();
    }

    [Fact]
    public async Task SaveCondition_reserved_name_returns_bad_request()
    {
        var condRepo = new Mock<IConditionSettingRepository>();

        var req = new SaveConditionRequest("/fdc/monitor", "$Latest", null);
        var result = await BuildController(condRepo).SaveCondition(req, default);

        result.Should().BeOfType<BadRequestObjectResult>();
        condRepo.Verify(r => r.UpsertAsync(It.IsAny<ConditionSetting>(), default), Times.Never);
    }

    // ── DeleteCondition / ClearLatestCondition ────────────────────────────────

    [Fact]
    public async Task DeleteCondition_latest_variant_returns_bad_request()
    {
        var condRepo = new Mock<IConditionSettingRepository>();

        var result = await BuildController(condRepo).DeleteCondition("/fdc/monitor", "$LATEST", default);

        result.Should().BeOfType<BadRequestObjectResult>();
        condRepo.Verify(r => r.DeleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
    }

    [Fact]
    public async Task ClearLatestCondition_deletes_latest_and_returns_no_content()
    {
        var condRepo = new Mock<IConditionSettingRepository>();

        var result = await BuildController(condRepo).ClearLatestCondition("/FDC/Monitor/", default);

        result.Should().BeOfType<NoContentResult>();
        condRepo.Verify(r => r.DeleteAsync("user1", "/fdc/monitor", ConditionSetting.LatestName, default), Times.Once);
    }
}
