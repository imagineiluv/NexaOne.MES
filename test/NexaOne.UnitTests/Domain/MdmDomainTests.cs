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

    [Fact]
    public void Restore_plant_preserves_description()
    {
        // 읽기경로 상태손실 회귀 방지: Create는 Description을 받지 않아 읽기 시 공백으로 초기화되었다.
        var plant = Plant.Restore("PLANT01", "서울 공장", "본사 공장", "Korea", "Asia/Seoul");

        plant.Description.Should().Be("본사 공장");
        plant.PlantName.Should().Be("서울 공장");
        plant.Country.Should().Be("Korea");
        plant.TimeZone.Should().Be("Asia/Seoul");
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

    [Fact]
    public void Restore_area_preserves_persisted_description()
    {
        // 읽기경로 상태손실 회귀: Create는 Description을 받지 않아 ToDomain이 저장된 설명을 빈 문자열로 유실했다.
        var area = Area.Restore("AREA01", "조립 구역", "차체 조립 라인", "PLANT01");
        area.Description.Should().Be("차체 조립 라인", "Restore는 저장된 Description을 그대로 복원해야 한다");
        area.AreaName.Should().Be("조립 구역");
        area.PlantId.Should().Be("PLANT01");
    }

    [Fact]
    public void Restore_area_raises_no_domain_events()
        => Area.Restore("AREA01", "조립 구역", "차체 조립 라인", "PLANT01")
            .DomainEvents.Should().BeEmpty("읽기경로 재구성(Restore)은 도메인 이벤트 발행 대상이 아니다");

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

    [Fact]
    public void Restore_code_class_preserves_persisted_description()
    {
        // 읽기경로 상태손실 회귀: Create는 Description을 받지 않아 ToDomain이 저장된 설명을 빈 문자열로 유실했다.
        var codeClass = CodeClass.Restore("CC001", "설비 유형", "설비 분류 코드군");
        codeClass.Description.Should().Be("설비 분류 코드군", "Restore는 저장된 Description을 그대로 복원해야 한다");
        codeClass.CodeClassName.Should().Be("설비 유형");
    }

    [Fact]
    public void Restore_code_class_raises_no_domain_events()
        => CodeClass.Restore("CC001", "설비 유형", "설비 분류 코드군")
            .DomainEvents.Should().BeEmpty("읽기경로 재구성(Restore)은 도메인 이벤트 발행 대상이 아니다");

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

    [Fact]
    public void Restore_code_preserves_non_valid_state()
    {
        // 읽기경로 상태손실 회귀: Create는 ValidState를 항상 "Valid"로 고정해, 무효화된 코드가 읽기 시 되살아났다.
        var code = Code.Restore("CODE001", "CC001", "프레스", 2, "Invalid");
        code.ValidState.Should().Be("Invalid", "Restore는 저장된 ValidState를 그대로 복원해야 한다");
        code.SortOrder.Should().Be(2, "Restore는 저장된 SortOrder를 그대로 복원해야 한다");
        code.CodeName.Should().Be("프레스");
        code.CodeClassId.Should().Be("CC001");
    }

    [Fact]
    public void Restore_code_raises_no_domain_events()
        => Code.Restore("CODE001", "CC001", "프레스", 2, "Invalid")
            .DomainEvents.Should().BeEmpty("읽기경로 재구성(Restore)은 도메인 이벤트 발행 대상이 아니다");
}
