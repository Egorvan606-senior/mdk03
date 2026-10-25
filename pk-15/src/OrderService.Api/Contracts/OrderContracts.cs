namespace OrderService.Contracts;

/// <summary>
/// Заказ от клиента. Мобильное приложение присылает «сложный» JSON: вложенные объекты,
/// массивы позиций с опциями, произвольный комментарий покупателя (кавычки, апострофы,
/// слова вроде «select», «order by»). Именно такие запросы WAF не должен блокировать
/// (специфика кейса 10 — риск ложных срабатываний правил).
/// </summary>
public sealed record CreateOrderRequest(
    string CustomerId,
    string Channel,
    List<OrderItemDto> Items,
    DeliveryDto? Delivery,
    string? Comment);

public sealed record OrderItemDto(string Sku, string Name, int Quantity, decimal Price, Dictionary<string, string>? Options);

public sealed record DeliveryDto(string Address, string? Entrance, string? Floor, string? TimeSlot);

public sealed record PayOrderRequest(string CardToken);

public sealed record OrderResponse(Guid Id, string CustomerId, string Channel, decimal Total, string Status, string? PaymentTransactionId, string? ChallengeUrl);
