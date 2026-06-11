using NexaOne.MDM.Domain;

namespace NexaOne.UnitTests.Domain;

public sealed class MdmDomainTests
{
    // ── Plant ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Create_plant_valid_succeeds()
    {
        var result = Plant.Create("PLANT01", "서울 공장", "Korea", "Asia/Seoul");
        result.IsSuccess.Should().BeTrue();
        result.Value.PlantName.Should().Be("서울 공장");
    }

    [Fact]
    public void Create_plant_missing_id_fails()
    {
        var result = Plant.Create("", "서울 공장", "Korea", "Asia/Seoul");
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_plant_missing_name_fails()
    {
        var result = Plant.Create("PLANT01", "", "Korea", "Asia/Seoul");
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Update_plant_changes_fields()
    {
        var plant = Plant.Create("PLANT01", "서울 공장", "Korea", "Asia/Seoul").Value;
        plant.Update("부산 공장", "부산 소재", "Korea", "Asia/Seoul");
        plant.PlantName.Should().Be("부산 공장");
        plant.Description.Should().Be("부산 소재");
    }

    // ── Area ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Create_area_valid_succeeds()
    {
        var result = Area.Create("AREA01", "조립 구역", "PLANT01");
        result.IsSuccess.Should().BeTrue();
        result.Value.PlantId.Should().Be("PLANT01");
    }

    [Fact]
    public void Create_area_missing_id_fails()
    {
        var result = Area.Create("", "조립 구역", "PLANT01");
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Update_area_changes_name_and_description()
    {
        var area = Area.Create("AREA01", "조립 구역", "PLANT01").Value;
        area.Update("도장 구역", "차체 도장 라인");
        area.AreaName.Should().Be("도장 구역");
        area.Description.Should().Be("차체 도장 라인");
    }

    // ── Product ───────────────────────────────────────────────────────────────

    [Fact]
    public void Create_product_valid_succeeds()
    {
        var result = Product.Create("PROD001", "완성차 A", "Finished", "EA");
        result.IsSuccess.Should().BeTrue();
        result.Value.ValidState.Should().Be("Valid");
    }

    [Fact]
    public void Create_product_missing_name_fails()
    {
        var result = Product.Create("PROD001", "", "Finished", "EA");
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Update_product_changes_fields()
    {
        var product = Product.Create("PROD001", "완성차 A", "Finished", "EA").Value;
        product.Update("완성차 B", "개선 모델", "Finished", "EA");
        product.ProductName.Should().Be("완성차 B");
        product.Description.Should().Be("개선 모델");
    }

    // ── CodeClass ─────────────────────────────────────────────────────────────

    [Fact]
    public void Create_code_class_valid_succeeds()
    {
        var result = CodeClass.Create("CC001", "설비 유형");
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Create_code_class_missing_id_fails()
    {
        var result = CodeClass.Create("", "설비 유형");
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_code_class_missing_name_fails()
    {
        var result = CodeClass.Create("CC001", "");
        result.IsFailure.Should().BeTrue();
    }

    // ── Code ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Create_code_valid_succeeds()
    {
        var result = Code.Create("CODE001", "CC001", "프레스", 1);
        result.IsSuccess.Should().BeTrue();
        result.Value.ValidState.Should().Be("Valid");
        result.Value.SortOrder.Should().Be(1);
    }

    [Fact]
    public void Create_code_missing_id_fails()
    {
        var result = Code.Create("", "CC001", "프레스", 0);
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_code_missing_name_fails()
    {
        var result = Code.Create("CODE001", "CC001", "", 0);
        result.IsFailure.Should().BeTrue();
    }
}
