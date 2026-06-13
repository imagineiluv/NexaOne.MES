using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Domain;

namespace NexaOne.UnitTests.Fdc;

/// <summary>FDC 파라미터 그룹(FDC_PARAMETER_GROUP) 도메인 검증과 그룹 관리 서비스를 검증한다 (§10.4.1).</summary>
public sealed class FdcParameterGroupTests
{
    // ── 도메인 ──

    [Fact]
    public void Create_succeeds_with_valid_fields()
    {
        var result = FdcParameterGroup.Create("G1", "Temperature Group", "EQ-001", "temp params", 2);

        result.IsFailure.Should().BeFalse();
        var g = result.Value;
        g.GroupName.Should().Be("Temperature Group");
        g.EquipmentId.Should().Be("EQ-001");
        g.Description.Should().Be("temp params");
        g.DisplayOrder.Should().Be(2);
        g.IsActive.Should().BeTrue();
    }

    [Theory]
    [InlineData("", "name", "EQ-001")]
    [InlineData("G1", "", "EQ-001")]
    [InlineData("G1", "name", "")]
    public void Create_fails_when_required_field_missing(string id, string name, string eq)
        => FdcParameterGroup.Create(id, name, eq).IsFailure.Should().BeTrue();

    [Fact]
    public void Create_fails_on_negative_display_order()
        => FdcParameterGroup.Create("G1", "name", "EQ-001", displayOrder: -1).IsFailure.Should().BeTrue();

    [Fact]
    public void Mutators_apply_only_valid_values()
    {
        var g = FdcParameterGroup.Create("G1", "old", "EQ-001", displayOrder: 1).Value;

        g.Rename("new");
        g.SetDisplayOrder(5);
        g.SetDescription("desc");
        g.GroupName.Should().Be("new");
        g.DisplayOrder.Should().Be(5);
        g.Description.Should().Be("desc");

        g.Rename("   ");          // 무효 무시
        g.SetDisplayOrder(-3);    // 무효 무시
        g.GroupName.Should().Be("new");
        g.DisplayOrder.Should().Be(5);

        g.Deactivate();
        g.IsActive.Should().BeFalse();
    }

    // ── 서비스 ──

    [Fact]
    public async Task CreateGroupAsync_persists_valid_group()
    {
        FdcParameterGroup? saved = null;
        var repo = new Mock<IFdcParameterGroupRepository>();
        repo.Setup(r => r.AddAsync(It.IsAny<FdcParameterGroup>(), It.IsAny<CancellationToken>()))
            .Callback<FdcParameterGroup, CancellationToken>((g, _) => saved = g)
            .Returns(Task.CompletedTask);
        var svc = new FdcParameterGroupService(repo.Object);

        var result = await svc.CreateGroupAsync("G1", "Temp", "EQ-001");

        result.IsFailure.Should().BeFalse();
        saved.Should().NotBeNull();
        saved!.Id.Should().Be("G1");
    }

    [Fact]
    public async Task CreateGroupAsync_does_not_persist_invalid_group()
    {
        var repo = new Mock<IFdcParameterGroupRepository>();
        var svc = new FdcParameterGroupService(repo.Object);

        var result = await svc.CreateGroupAsync("G1", "", "EQ-001");   // 이름 누락

        result.IsFailure.Should().BeTrue();
        repo.Verify(r => r.AddAsync(It.IsAny<FdcParameterGroup>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RenameGroupAsync_updates_existing_group()
    {
        var group = FdcParameterGroup.Create("G1", "old", "EQ-001").Value;
        var repo = new Mock<IFdcParameterGroupRepository>();
        repo.Setup(r => r.GetByIdAsync("G1", It.IsAny<CancellationToken>())).ReturnsAsync(group);
        var svc = new FdcParameterGroupService(repo.Object);

        var result = await svc.RenameGroupAsync("G1", "renamed");

        result.IsFailure.Should().BeFalse();
        group.GroupName.Should().Be("renamed");
        repo.Verify(r => r.UpdateAsync(group, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RenameGroupAsync_fails_for_missing_group()
    {
        var repo = new Mock<IFdcParameterGroupRepository>();
        repo.Setup(r => r.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FdcParameterGroup?)null);
        var svc = new FdcParameterGroupService(repo.Object);

        (await svc.RenameGroupAsync("missing", "x")).IsFailure.Should().BeTrue();
    }
}
