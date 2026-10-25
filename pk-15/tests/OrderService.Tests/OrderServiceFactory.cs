using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderService.Data;
using OrderService.Payments;

namespace OrderService.Tests;

/// <summary>
/// Поднимает OrderService в памяти теста (как после деплоя на стенд):
/// отдельная SQLite-база на каждый запуск, режим WAF и платёжный шлюз задаются тестом.
/// </summary>
public sealed class OrderServiceFactory(string wafMode = "On", IPaymentService? payments = null) : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"orders_test_{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Waf:Mode"] = wafMode }));

        builder.ConfigureTestServices(services =>
        {
            // отдельная тестовая БД вместо orders.db
            services.RemoveAll<DbContextOptions<OrderDbContext>>();
            services.AddDbContext<OrderDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));

            if (payments is not null)
            {
                services.RemoveAll<IPaymentService>();
                services.AddSingleton(payments);
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { /* файл ещё занят — удалит ОС */ }
    }
}
