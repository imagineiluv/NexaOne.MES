using NexaOne.MDM.Domain;
using NexaOne.Common;

namespace NexaOne.UnitTests.Domain;

public sealed class EquipmentTests
{
    [Fact]
    public void Create_equipment_with_valid_data_succeeds()
    {
        var result = Equipment.Create("EQ001", "Furnace #1", "P01", "A01", "Furnace");
        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be("EQ001");
        result.Value.EquipmentName.Should().Be("Furnace #1");
        result.Value.ValidState.Should().Be("Valid");
    }

    [Fact]
    public void Create_equipment_with_empty_id_fails()
    {
        var result = Equipment.Create("", "Furnace #1", "P01", "A01", "Furnace");
        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().Contain("required");
    }

    [Fact]
    public void Create_equipment_with_empty_name_fails()
    {
        var result = Equipment.Create("EQ001", "", "P01", "A01", "Furnace");
        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().Contain("required");
    }

    [Fact]
    public void Deactivate_sets_ValidState_to_Invalid()
    {
        var eq = Equipment.Create("EQ002", "CVD", "P01", "A01", "CVD").Value;
        eq.Deactivate();
        eq.ValidState.Should().Be("Invalid");
    }

    [Fact]
    public void Activate_sets_ValidState_to_Valid()
    {
        var eq = Equipment.Create("EQ003", "ALD", "P01", "A01", "ALD").Value;
        eq.Deactivate();
        eq.Activate();
        eq.ValidState.Should().Be("Valid");
    }

    [Fact]
    public void UpdateInfo_changes_name_and_type()
    {
        var eq = Equipment.Create("EQ004", "Old Name", "P01", "A01", "OldType").Value;
        eq.UpdateInfo("New Name", "desc", "NewType", "Vendor", "Model");
        eq.EquipmentName.Should().Be("New Name");
        eq.EquipmentType.Should().Be("NewType");
    }
}
