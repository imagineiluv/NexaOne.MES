using Moq;
using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Domain;
using NexaOne.Common;

namespace NexaOne.UnitTests.Services;

public sealed class FdcDataServiceTests
{
    private static FdcParameter ActiveParam(string id = "PARAM01") =>
        FdcParameter.Create(id, "Test Param", "EQ001", "℃", 0m, 100m).Value;

    private FdcDataService BuildService(Mock<IFdcParameterRepository> paramRepo, Mock<IFdcCollectDataRepository> dataRepo)
        => new(paramRepo.Object, dataRepo.Object);

    // ── CreateParameterAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task CreateParameter_valid_data_succeeds()
    {
        var paramRepo = new Mock<IFdcParameterRepository>();
        var dataRepo  = new Mock<IFdcCollectDataRepository>();
        paramRepo.Setup(r => r.AddAsync(It.IsAny<FdcParameter>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(paramRepo, dataRepo)
            .CreateParameterAsync("PARAM01", "Test Param", "EQ001", "℃", 0m, 100m);

        result.IsSuccess.Should().BeTrue();
        result.Value.LowerLimit.Should().Be(0m);
        result.Value.UpperLimit.Should().Be(100m);
        result.Value.IsActive.Should().BeTrue();
        paramRepo.Verify(r => r.AddAsync(It.IsAny<FdcParameter>(), default), Times.Once);
    }

    [Fact]
    public async Task CreateParameter_lower_ge_upper_fails()
    {
        var paramRepo = new Mock<IFdcParameterRepository>();
        var dataRepo  = new Mock<IFdcCollectDataRepository>();

        var result = await BuildService(paramRepo, dataRepo)
            .CreateParameterAsync("PARAM01", "Test Param", "EQ001", "℃", 100m, 50m);

        result.IsFailure.Should().BeTrue();
        paramRepo.Verify(r => r.AddAsync(It.IsAny<FdcParameter>(), default), Times.Never);
    }

    [Fact]
    public async Task CreateParameter_missing_id_fails()
    {
        var paramRepo = new Mock<IFdcParameterRepository>();
        var dataRepo  = new Mock<IFdcCollectDataRepository>();

        var result = await BuildService(paramRepo, dataRepo)
            .CreateParameterAsync("", "Test Param", "EQ001", "℃", 0m, 100m);

        result.IsFailure.Should().BeTrue();
    }

    // ── GetParametersAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task GetParameters_returns_repository_list()
    {
        var parameters = new List<FdcParameter> { ActiveParam() };
        var paramRepo  = new Mock<IFdcParameterRepository>();
        var dataRepo   = new Mock<IFdcCollectDataRepository>();
        paramRepo.Setup(r => r.GetByEquipmentAsync("EQ001", default)).ReturnsAsync(parameters);

        var result = await BuildService(paramRepo, dataRepo).GetParametersAsync("EQ001");

        result.Should().HaveCount(1);
    }

    // ── RecordDataAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task RecordData_valid_data_succeeds()
    {
        var param     = ActiveParam("PARAM01");
        var paramRepo = new Mock<IFdcParameterRepository>();
        var dataRepo  = new Mock<IFdcCollectDataRepository>();
        paramRepo.Setup(r => r.GetByIdAsync("PARAM01", default)).ReturnsAsync(param);
        dataRepo.Setup(r => r.AddAsync(It.IsAny<FdcCollectData>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(paramRepo, dataRepo)
            .RecordDataAsync("CD001", "EQ001", "PARAM01", 55m, "Good");

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be(55m);
        result.Value.Quality.Should().Be("Good");
        result.Value.LowerLimit.Should().Be(0m);
        result.Value.UpperLimit.Should().Be(100m);
        result.Value.IsOutOfSpec.Should().BeFalse();
        dataRepo.Verify(r => r.AddAsync(It.IsAny<FdcCollectData>(), default), Times.Once);
    }

    [Fact]
    public async Task RecordData_out_of_spec_value_is_flagged()
    {
        var param     = ActiveParam("PARAM01");
        var paramRepo = new Mock<IFdcParameterRepository>();
        var dataRepo  = new Mock<IFdcCollectDataRepository>();
        paramRepo.Setup(r => r.GetByIdAsync("PARAM01", default)).ReturnsAsync(param);
        dataRepo.Setup(r => r.AddAsync(It.IsAny<FdcCollectData>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(paramRepo, dataRepo)
            .RecordDataAsync("CD002", "EQ001", "PARAM01", 150m, "Good");

        result.IsSuccess.Should().BeTrue();
        result.Value.IsOutOfSpec.Should().BeTrue();
    }

    [Fact]
    public async Task RecordData_invalid_quality_fails()
    {
        var param     = ActiveParam("PARAM01");
        var paramRepo = new Mock<IFdcParameterRepository>();
        var dataRepo  = new Mock<IFdcCollectDataRepository>();
        paramRepo.Setup(r => r.GetByIdAsync("PARAM01", default)).ReturnsAsync(param);

        var result = await BuildService(paramRepo, dataRepo)
            .RecordDataAsync("CD003", "EQ001", "PARAM01", 50m, "INVALID");

        result.IsFailure.Should().BeTrue();
        dataRepo.Verify(r => r.AddAsync(It.IsAny<FdcCollectData>(), default), Times.Never);
    }

    [Fact]
    public async Task RecordData_parameter_not_found_returns_failure()
    {
        var paramRepo = new Mock<IFdcParameterRepository>();
        var dataRepo  = new Mock<IFdcCollectDataRepository>();
        paramRepo.Setup(r => r.GetByIdAsync("PXXX", default)).ReturnsAsync((FdcParameter?)null);

        var result = await BuildService(paramRepo, dataRepo)
            .RecordDataAsync("CD004", "EQ001", "PXXX", 50m, "Good");

        result.IsFailure.Should().BeTrue();
        dataRepo.Verify(r => r.AddAsync(It.IsAny<FdcCollectData>(), default), Times.Never);
    }

    // ── GetCollectDataAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task GetCollectData_returns_repository_list()
    {
        var from = DateTime.UtcNow.AddHours(-1);
        var to   = DateTime.UtcNow;
        var data = new List<FdcCollectData>
        {
            FdcCollectData.Create("CD001", "EQ001", "PARAM01", 55m, from, "Good", 0m, 100m).Value
        };
        var paramRepo = new Mock<IFdcParameterRepository>();
        var dataRepo  = new Mock<IFdcCollectDataRepository>();
        dataRepo.Setup(r => r.GetByParameterAsync("PARAM01", from, to, It.IsAny<int>(), default)).ReturnsAsync(data);

        var result = await BuildService(paramRepo, dataRepo).GetCollectDataAsync("PARAM01", from, to, 1000);

        result.Should().HaveCount(1);
        result[0].Value.Should().Be(55m);
    }
}
