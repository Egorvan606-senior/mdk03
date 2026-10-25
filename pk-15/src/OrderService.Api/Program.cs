// ============================================================================
// OrderService 2.0.0 — учебный сервис заказов (ПК № 15, вариант 10).
// Релиз: переход на новый платёжный шлюз с 3-D Secure 2.0 и включение WAF перед API.
// Эндпоинты, на которые опираются регламент, smoke-тесты и Runbook (папка docs/):
//   GET  /health                   — живость сервиса + проверка БД (DbContext.Database.CanConnect())
//   POST /api/v1/orders            — создание заказа (сложный JSON мобильного приложения)
//   POST /api/v1/orders/{id}/pay   — оплата через IPaymentService
//   GET  /api/v1/orders/{id}       — чтение заказа
// ============================================================================

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderService.Contracts;
using OrderService.Data;
using OrderService.Payments;
using OrderService.Waf;

var builder = WebApplication.CreateBuilder(args);

// ---- Хранилище заказов ------------------------------------------------------
builder.Services.AddDbContext<OrderDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Orders") ?? "Data Source=orders.db"));

// ---- Платёжный шлюз (ADR-001): реализация выбирается настройкой --------------
// Payments:Provider = NewGateway3DS2 (релиз 2.0.0) | Legacy (быстрый откат без пересборки)
if (string.Equals(builder.Configuration["Payments:Provider"], "Legacy", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddScoped<IPaymentService, LegacyGatewayPaymentService>();
}
else
{
    builder.Services.AddScoped<IPaymentService, NewGatewayPaymentService>();
}

var app = builder.Build();

// Схема БД создаётся при старте (для учебного стенда вместо миграций)
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<OrderDbContext>().Database.EnsureCreated();
}

// ---- WAF перед API (ADR-002): первым в конвейере, до обработки запроса ------
app.UseMiddleware<WafStandMiddleware>();

// ---- Smoke-тест № 1 и № 2: GET /health (ожидаем 200) + CanConnect к БД --------
app.MapGet("/health", (OrderDbContext db, IPaymentService payments, IConfiguration cfg) =>
{
    bool dbOk = db.Database.CanConnect();
    var body = new
    {
        status = dbOk ? "Healthy" : "Unhealthy",
        version = "2.0.0",
        database = dbOk ? "Connected" : "Unavailable",
        paymentProvider = payments.ProviderName,
        wafMode = cfg["Waf:Mode"] ?? "On",
        checkedAtUtc = DateTime.UtcNow
    };
    // 503 — сигнал Kubernetes readiness-probe не направлять трафик на этот под
    return dbOk ? Results.Ok(body) : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
});

var orders = app.MapGroup("/api/v1/orders");

orders.MapPost("", async (CreateOrderRequest request, OrderDbContext db, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.CustomerId) || request.Items is not { Count: > 0 })
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["items"] = ["Заказ должен содержать покупателя и хотя бы одну позицию"] });
    }

    if (request.Items.Any(i => i.Quantity <= 0 || i.Price < 0))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["items"] = ["Количество должно быть > 0, цена — неотрицательной"] });
    }

    var order = new OrderEntity
    {
        CustomerId = request.CustomerId,
        Channel = string.IsNullOrWhiteSpace(request.Channel) ? "web" : request.Channel,
        Total = request.Items.Sum(i => i.Price * i.Quantity),
        ItemsJson = JsonSerializer.Serialize(request.Items),
        Comment = request.Comment
    };

    db.Orders.Add(order);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/v1/orders/{order.Id}", ToResponse(order, null));
});

orders.MapGet("/{id:guid}", async (Guid id, OrderDbContext db, CancellationToken ct) =>
    await db.Orders.FindAsync([id], ct) is { } order ? Results.Ok(ToResponse(order, null)) : Results.NotFound());

orders.MapPost("/{id:guid}/pay", async (Guid id, PayOrderRequest request, OrderDbContext db, IPaymentService payments,
    ILogger<Program> logger, CancellationToken ct) =>
{
    var order = await db.Orders.FindAsync([id], ct);
    if (order is null)
    {
        return Results.NotFound();
    }

    try
    {
        var result = await payments.ChargeAsync(new PaymentRequest(order.Id, order.Total, "RUB", request.CardToken), ct);
        order.PaymentTransactionId = result.ProviderTransactionId;
        order.Status = result.Status switch
        {
            PaymentStatus.Approved => "Paid",
            PaymentStatus.ChallengeRequired => "AwaitingChallenge",
            _ => "Declined"
        };
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(order, result.ChallengeUrl));
    }
    catch (PaymentGatewayTimeoutException ex)
    {
        // Инцидент из Runbook RB-001: шлюз не отвечает -> 504 Gateway Timeout
        logger.LogError(ex, "504 Gateway Timeout при оплате заказа {OrderId} через {Provider}", order.Id, payments.ProviderName);
        return Results.Problem(title: "Payment gateway timeout", detail: ex.Message, statusCode: StatusCodes.Status504GatewayTimeout);
    }
});

app.Run();

static OrderResponse ToResponse(OrderEntity o, string? challengeUrl) =>
    new(o.Id, o.CustomerId, o.Channel, o.Total, o.Status, o.PaymentTransactionId, challengeUrl);

/// <summary>Открыт для WebApplicationFactory в smoke-тестах.</summary>
public partial class Program;
