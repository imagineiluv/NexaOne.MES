using NexaOne.Web.Services;

namespace NexaOne.UnitTests.Web;

public sealed class UiTextServiceTests
{
    [Fact]
    public void Field_prefers_resource_then_humanizes_stable_key_only_in_english_mode()
    {
        var service = new UiTextService();
        service.Field("STOCK_QTY", "현재고").Should().Be("현재고");

        service.Load("EnUs", new Dictionary<string, string>
        {
            ["field.STOCK_QTY"] = "On-hand Quantity",
        });

        service.Field("STOCK_QTY", "현재고").Should().Be("On-hand Quantity");
        service.Field("LOT_NO", "LOT 번호").Should().Be("LOT No.");
        service.Field("RECEIVED_AT", "입고일시").Should().Be("Received At");
        service.Field("AI_INSPECTION_QTY", "AI 검사수량").Should().Be("AI Inspection Quantity");
    }
}
