using Moq;
using NexaOne.MDM.Application.Equipments;
using NexaOne.MDM.Domain;
using NexaOne.Common;

namespace NexaOne.UnitTests.Services;

public sealed class MdmMasterServiceTests
{
    private MdmMasterService Build(
        Mock<IPlantRepository>?   plant   = null,
        Mock<IAreaRepository>?    area    = null,
        Mock<IProductRepository>? product = null,
        Mock<ICodeRepository>?    code    = null)
        => new(
            (plant   ?? new Mock<IPlantRepository>()).Object,
            (area    ?? new Mock<IAreaRepository>()).Object,
            (product ?? new Mock<IProductRepository>()).Object,
            (code    ?? new Mock<ICodeRepository>()).Object);

    // ── Plant ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreatePlant_valid_data_succeeds()
    {
        var repo = new Mock<IPlantRepository>();
        repo.Setup(r => r.AddAsync(It.IsAny<Plant>(), default)).Returns(Task.CompletedTask);

        var result = await Build(plant: repo).CreatePlantAsync("P01", "Plant One", "KR", "Asia/Seoul");

        result.IsSuccess.Should().BeTrue();
        result.Value.PlantName.Should().Be("Plant One");
        repo.Verify(r => r.AddAsync(It.IsAny<Plant>(), default), Times.Once);
    }

    [Fact]
    public async Task CreatePlant_missing_id_fails()
    {
        var repo = new Mock<IPlantRepository>();
        var result = await Build(plant: repo).CreatePlantAsync("", "Plant One", "KR", "Asia/Seoul");
        result.IsFailure.Should().BeTrue();
        repo.Verify(r => r.AddAsync(It.IsAny<Plant>(), default), Times.Never);
    }

    // ── Area ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateArea_valid_data_succeeds()
    {
        var repo = new Mock<IAreaRepository>();
        repo.Setup(r => r.AddAsync(It.IsAny<Area>(), default)).Returns(Task.CompletedTask);

        var result = await Build(area: repo).CreateAreaAsync("A01", "Line 1", "P01");

        result.IsSuccess.Should().BeTrue();
        result.Value.PlantId.Should().Be("P01");
        repo.Verify(r => r.AddAsync(It.IsAny<Area>(), default), Times.Once);
    }

    [Fact]
    public async Task CreateArea_missing_name_fails()
    {
        var repo = new Mock<IAreaRepository>();
        var result = await Build(area: repo).CreateAreaAsync("A01", "", "P01");
        result.IsFailure.Should().BeTrue();
    }

    // ── Product ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateProduct_valid_data_succeeds()
    {
        var repo = new Mock<IProductRepository>();
        repo.Setup(r => r.AddAsync(It.IsAny<Product>(), default)).Returns(Task.CompletedTask);

        var result = await Build(product: repo).CreateProductAsync("PROD01", "Widget A", "FG", "EA");

        result.IsSuccess.Should().BeTrue();
        result.Value.ProductType.Should().Be("FG");
        result.Value.ValidState.Should().Be("Valid");
        repo.Verify(r => r.AddAsync(It.IsAny<Product>(), default), Times.Once);
    }

    [Fact]
    public async Task CreateProduct_missing_id_fails()
    {
        var repo = new Mock<IProductRepository>();
        var result = await Build(product: repo).CreateProductAsync("", "Widget A", "FG", "EA");
        result.IsFailure.Should().BeTrue();
    }

    // ── CodeClass ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateCodeClass_valid_data_succeeds()
    {
        var repo = new Mock<ICodeRepository>();
        repo.Setup(r => r.AddClassAsync(It.IsAny<CodeClass>(), default)).Returns(Task.CompletedTask);

        var result = await Build(code: repo).CreateCodeClassAsync("ALARM_LEVEL", "알람 등급");

        result.IsSuccess.Should().BeTrue();
        result.Value.CodeClassName.Should().Be("알람 등급");
        repo.Verify(r => r.AddClassAsync(It.IsAny<CodeClass>(), default), Times.Once);
    }

    [Fact]
    public async Task CreateCodeClass_missing_id_fails()
    {
        var repo = new Mock<ICodeRepository>();
        var result = await Build(code: repo).CreateCodeClassAsync("", "알람 등급");
        result.IsFailure.Should().BeTrue();
    }

    // ── Code ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateCode_valid_data_succeeds()
    {
        var codeClass = CodeClass.Create("ALARM_LEVEL", "알람 등급").Value;
        var repo = new Mock<ICodeRepository>();
        repo.Setup(r => r.GetClassByIdAsync("ALARM_LEVEL", default)).ReturnsAsync(codeClass);
        repo.Setup(r => r.AddCodeAsync(It.IsAny<Code>(), default)).Returns(Task.CompletedTask);

        var result = await Build(code: repo).CreateCodeAsync("CRITICAL", "ALARM_LEVEL", "Critical", 1);

        result.IsSuccess.Should().BeTrue();
        result.Value.CodeClassId.Should().Be("ALARM_LEVEL");
        result.Value.SortOrder.Should().Be(1);
        repo.Verify(r => r.AddCodeAsync(It.IsAny<Code>(), default), Times.Once);
    }

    [Fact]
    public async Task CreateCode_class_not_found_returns_failure()
    {
        var repo = new Mock<ICodeRepository>();
        repo.Setup(r => r.GetClassByIdAsync("UNKNOWN", default)).ReturnsAsync((CodeClass?)null);

        var result = await Build(code: repo).CreateCodeAsync("C01", "UNKNOWN", "Code 1");

        result.IsFailure.Should().BeTrue();
        repo.Verify(r => r.AddCodeAsync(It.IsAny<Code>(), default), Times.Never);
    }

    [Fact]
    public async Task CreateCode_missing_name_fails()
    {
        var codeClass = CodeClass.Create("ALARM_LEVEL", "알람 등급").Value;
        var repo = new Mock<ICodeRepository>();
        repo.Setup(r => r.GetClassByIdAsync("ALARM_LEVEL", default)).ReturnsAsync(codeClass);

        var result = await Build(code: repo).CreateCodeAsync("C01", "ALARM_LEVEL", "");

        result.IsFailure.Should().BeTrue();
    }
}
