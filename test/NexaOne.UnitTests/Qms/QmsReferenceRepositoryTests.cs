using NexaOne.QMS.Infrastructure;
using NexaOne.ServiceContracts.Ivt;
using NexaOne.ServiceContracts.Mdm;
using NexaOne.ServiceContracts.Pom;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.UnitTests.Qms;

public sealed class QmsReferenceRepositoryTests
{
    [Fact]
    public async Task Production_lot_short_circuits_material_lot_lookup()
    {
        var dependencies = Dependencies();
        dependencies.Production
            .Setup(directory => directory.GetLotAsync("LOT-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProductionLotDirectoryEntry("LOT-1", "PRODUCT-1"));
        var sut = Create(dependencies);

        var exists = await sut.LotExistsAsync("LOT-1");

        exists.Should().BeTrue();
        dependencies.Material.Verify(
            directory => directory.GetLotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Material_lot_is_used_when_production_lot_does_not_exist()
    {
        var dependencies = Dependencies();
        dependencies.Material
            .Setup(directory => directory.GetLotAsync("MAT-LOT-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MaterialLotDirectoryEntry("MAT-LOT-1", "MATERIAL-1"));
        var sut = Create(dependencies);

        (await sut.LotExistsAsync("MAT-LOT-1")).Should().BeTrue();

        dependencies.Production.Verify(
            directory => directory.GetLotAsync("MAT-LOT-1", It.IsAny<CancellationToken>()),
            Times.Once);
        dependencies.Material.Verify(
            directory => directory.GetLotAsync("MAT-LOT-1", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Equipment_must_exist_and_be_valid(bool isValid, bool expected)
    {
        var dependencies = Dependencies();
        dependencies.Equipment
            .Setup(directory => directory.GetEquipmentAsync("EQ-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EquipmentDirectoryEntry("EQ-1", "PLANT-1", "CLASS-1", isValid));
        var sut = Create(dependencies);

        (await sut.EquipmentExistsAsync("EQ-1")).Should().Be(expected);
    }

    [Fact]
    public async Task Missing_equipment_is_not_a_valid_reference()
    {
        var sut = Create(Dependencies());
        (await sut.EquipmentExistsAsync("UNKNOWN")).Should().BeFalse();
    }

    [Fact]
    public async Task Process_and_user_checks_delegate_to_their_owner_directories()
    {
        var dependencies = Dependencies();
        dependencies.Process
            .Setup(directory => directory.ProcessExistsAsync("PROC-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        dependencies.User
            .Setup(directory => directory.IsActiveAsync("USER-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var sut = Create(dependencies);

        (await sut.ProcessExistsAsync("PROC-1")).Should().BeTrue();
        (await sut.UserExistsAsync("USER-1")).Should().BeTrue();
    }

    private static DependenciesSet Dependencies() => new(
        new Mock<IProductionLotDirectory>(),
        new Mock<IMaterialLotDirectory>(),
        new Mock<IEquipmentDirectory>(),
        new Mock<IProcessDirectory>(),
        new Mock<IUserDirectory>());

    private static QmsReferenceRepository Create(DependenciesSet dependencies) => new(
        dependencies.Production.Object,
        dependencies.Material.Object,
        dependencies.Equipment.Object,
        dependencies.Process.Object,
        dependencies.User.Object);

    private sealed record DependenciesSet(
        Mock<IProductionLotDirectory> Production,
        Mock<IMaterialLotDirectory> Material,
        Mock<IEquipmentDirectory> Equipment,
        Mock<IProcessDirectory> Process,
        Mock<IUserDirectory> User);
}
