using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OrderService.Contracts;
using OrderService.Payments;
using Xunit;

namespace OrderService.Tests;

/// <summary>
/// Проверки плана отката и платёжной части. IPaymentService подменяется Moq —
/// это прямое следствие ADR-001 (бизнес-логика изолирована от SDK вендора).
/// </summary>
public sealed class RollbackAndPaymentTests
{
    private static readonly object SimpleOrder = new
    {
        customerId = "student-10",
        channel = "mobile",
        items = new[] { new { sku = "LUNCH", name = "Комплексный обед", quantity = 1, price = 250m } }
    };

    [Fact(DisplayName = "Откат: в режиме WAF DetectionOnly запрос не блокируется, а только журналируется")]
    [Trait("Category", "Rollback")]
    public async Task DetectionOnly_DoesNotBlock()
    {
        using var factory = new OrderServiceFactory(wafMode: "DetectionOnly");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/orders/00000000-0000-0000-0000-000000000001?search=1%27%20OR%20%271%27%3D%271");

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);   // запрос дошёл до API
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);       // такого заказа нет — штатный ответ API
        Assert.Equal("DetectionOnly", response.Headers.GetValues("X-Waf-Mode").Single());
    }

    [Fact(DisplayName = "Оплата: новый шлюз одобрил платёж — заказ Paid, шлюз вызван один раз на сумму заказа")]
    [Trait("Category", "Unit")]
    public async Task Pay_Approved_SetsPaid()
    {
        var gateway = new Mock<IPaymentService>();
        gateway.SetupGet(g => g.ProviderName).Returns("NewGateway3DS2");
        gateway.Setup(g => g.ChargeAsync(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new PaymentResult(PaymentStatus.Approved, "3DS2-TEST-001"));

        using var factory = new OrderServiceFactory(payments: gateway.Object);
        using var client = factory.CreateClient();
        var order = await CreateOrder(client);

        using var paid = await client.PostAsJsonAsync($"/api/v1/orders/{order.Id}/pay", new { cardToken = "tok_visa" });
        var result = await paid.Content.ReadFromJsonAsync<OrderResponse>();

        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);
        Assert.Equal("Paid", result!.Status);
        Assert.Equal("3DS2-TEST-001", result.PaymentTransactionId);
        gateway.Verify(g => g.ChargeAsync(It.Is<PaymentRequest>(r => r.Amount == 250m && r.Currency == "RUB"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact(DisplayName = "Runbook RB-001: шлюз не отвечает — API возвращает 504 Gateway Timeout")]
    [Trait("Category", "Unit")]
    public async Task Pay_GatewayTimeout_Returns504()
    {
        var gateway = new Mock<IPaymentService>();
        gateway.SetupGet(g => g.ProviderName).Returns("NewGateway3DS2");
        gateway.Setup(g => g.ChargeAsync(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()))
               .ThrowsAsync(new PaymentGatewayTimeoutException("timeout"));

        using var factory = new OrderServiceFactory(payments: gateway.Object);
        using var client = factory.CreateClient();
        var order = await CreateOrder(client);

        using var response = await client.PostAsJsonAsync($"/api/v1/orders/{order.Id}/pay", new { cardToken = "tok_visa" });

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }

    [Theory(DisplayName = "3-D Secure 2.0: крупная сумма требует challenge, малая проходит без подтверждения")]
    [Trait("Category", "Unit")]
    [InlineData(250, PaymentStatus.Approved)]
    [InlineData(4999.99, PaymentStatus.Approved)]
    [InlineData(5000, PaymentStatus.ChallengeRequired)]
    public async Task NewGateway_3ds2Flow(decimal amount, PaymentStatus expected)
    {
        var service = new NewGatewayPaymentService(new ConfigurationBuilder().Build(), NullLogger<NewGatewayPaymentService>.Instance);

        var result = await service.ChargeAsync(new PaymentRequest(Guid.NewGuid(), amount, "RUB", "tok_visa"), CancellationToken.None);

        Assert.Equal(expected, result.Status);
    }

    private static async Task<OrderResponse> CreateOrder(HttpClient client)
    {
        using var created = await client.PostAsJsonAsync("/api/v1/orders", SimpleOrder);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<OrderResponse>())!;
    }
}
