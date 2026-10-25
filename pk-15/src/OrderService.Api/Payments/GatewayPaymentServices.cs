namespace OrderService.Payments;

/// <summary>
/// Новый безопасный шлюз с поддержкой 3-D Secure 2.0 (учебная реализация-адаптер).
/// В production здесь вызывается официальный SDK платёжной системы; адаптер
/// переводит его ответы в доменный <see cref="PaymentResult"/>.
/// </summary>
public sealed class NewGatewayPaymentService(IConfiguration configuration, ILogger<NewGatewayPaymentService> logger) : IPaymentService
{
    /// <summary>Сумма, начиная с которой банк-эмитент запрашивает challenge 3-D Secure.</summary>
    public const decimal ChallengeThreshold = 5000m;

    public string ProviderName => "NewGateway3DS2";

    public async Task<PaymentResult> ChargeAsync(PaymentRequest request, CancellationToken ct)
    {
        // Имитация инцидента для отработки Runbook RB-001 на стенде
        if (configuration.GetValue<bool>("Payments:SimulateTimeout"))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
            throw new PaymentGatewayTimeoutException("Платёжный шлюз не ответил за 30 с (api.payment-gateway.com/v1)");
        }

        await Task.Delay(TimeSpan.FromMilliseconds(30), ct); // сетевой вызов SDK
        string txId = "3DS2-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

        if (request.CardToken == "tok_declined")
        {
            logger.LogWarning("Шлюз отклонил оплату заказа {OrderId}", request.OrderId);
            return new PaymentResult(PaymentStatus.Declined, txId);
        }

        if (request.Amount >= ChallengeThreshold)
        {
            // Крупная сумма — банк требует подтверждение (SMS/push) — challenge flow 3DS2
            return new PaymentResult(PaymentStatus.ChallengeRequired, txId, $"https://acs.bank.example/challenge/{txId}");
        }

        return new PaymentResult(PaymentStatus.Approved, txId); // frictionless flow
    }
}

/// <summary>
/// Старый шлюз (без 3-D Secure 2.0). Оставлен в сборке на время релизного окна:
/// переключение Payments:Provider=Legacy — самый быстрый откат платёжной части без пересборки.
/// </summary>
public sealed class LegacyGatewayPaymentService : IPaymentService
{
    public string ProviderName => "Legacy";

    public async Task<PaymentResult> ChargeAsync(PaymentRequest request, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(30), ct);
        return new PaymentResult(PaymentStatus.Approved, "LEG-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant());
    }
}
