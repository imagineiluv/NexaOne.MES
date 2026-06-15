using NexaOne.MDM.Domain;

namespace NexaOne.UnitTests.Domain;

/// <summary>Product 애그리거트 — 읽기경로 Restore가 영속 상태(Description, ValidState)를 손실 없이 복원한다.
/// Create는 Description을 받지 않고 ValidState를 무조건 "Valid"로 재설정하므로 ToDomain에 쓰면 상태손실이 발생한다.</summary>
public sealed class ProductTests
{
    [Fact]
    public void Restore_preserves_description_and_validstate()
    {
        var product = Product.Restore("P-1", "Wafer", "8-inch test wafer", "RAW", "EA", "Invalid");

        product.Id.Should().Be("P-1");
        product.ProductName.Should().Be("Wafer");
        product.Description.Should().Be("8-inch test wafer", "Description은 영속 컬럼이라 읽기경로에서 보존돼야 한다");
        product.ProductType.Should().Be("RAW");
        product.Unit.Should().Be("EA");
        product.ValidState.Should().Be("Invalid", "비활성 제품의 ValidState가 'Valid'로 재설정되면 안 된다");
    }

    [Fact]
    public void Restore_raises_no_domain_events()
        => Product.Restore("P-2", "Wafer", "d", "RAW", "EA", "Valid")
            .DomainEvents.Should().BeEmpty("읽기경로 재구성(Restore)은 도메인 이벤트 발행 대상이 아니다");

    [Fact]
    public void Create_drops_description_and_forces_validstate_documenting_why_ToDomain_must_use_Restore()
    {
        // 회귀 가드: Create는 Description 파라미터가 없고 ValidState를 강제하므로 읽기경로 매핑에 쓰면 손실이 난다.
        var product = Product.Create("P-3", "Wafer", "RAW", "EA").Value;

        product.Description.Should().BeEmpty("Create는 Description을 받지 못한다");
        product.ValidState.Should().Be("Valid", "Create는 ValidState를 항상 'Valid'로 둔다");
    }
}
