namespace OrderService.Payments;

/// <summary>
/// Абстракция платёжного шлюза (решение ADR-001).
/// Бизнес-логика заказов зависит только от этого интерфейса и не знает деталей SDK
/// конкретного вендора, поэтому:
///   * шлюз меняется регистрацией в DI (и откатывается настройкой Payments:Provider);
///   * в модульных тестах интерфейс подменяется mock-объектом (Moq).
/// </summary>
public interface IPaymentService
{
    /// <summary>Название провайдера — выводится в /health для контроля после деплоя.</summary>
    string ProviderName { get; }

    Task<PaymentResult> ChargeAsync(PaymentRequest request, CancellationToken ct);
}

public sealed record PaymentRequest(Guid OrderId, decimal Amount, string Currency, string CardToken);

public enum PaymentStatus
{
    Approved,           // оплата прошла (frictionless-сценарий 3-D Secure 2.0)
    ChallengeRequired,  // банк требует подтверждения покупателем (challenge 3-D Secure 2.0)
    Declined            // отказ банка
}

public sealed record PaymentResult(PaymentStatus Status, string ProviderTransactionId, string? ChallengeUrl = null);

/// <summary>Шлюз не ответил за отведённое время — API отдаёт 504 Gateway Timeout (см. Runbook RB-001).</summary>
public sealed class PaymentGatewayTimeoutException(string message) : Exception(message);
