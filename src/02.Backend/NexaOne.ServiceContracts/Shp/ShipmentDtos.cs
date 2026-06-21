namespace NexaOne.ServiceContracts.Shp;

// 도메인 엔티티를 직렬화 계약으로 노출하지 않는 경량 DTO(ALC/버전 결합 차단). Status는 enum→string.
public record DeliveryOrderDto(
    string OrderId, string CustomerName, string PlantId, DateTime RequestedDate,
    DateTime? ShippedDate, string Status, decimal TotalQty);
