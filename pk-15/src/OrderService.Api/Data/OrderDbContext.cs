using Microsoft.EntityFrameworkCore;

namespace OrderService.Data;

/// <summary>Заказ в БД. Позиции хранятся JSON-строкой — для учебного сервиса этого достаточно.</summary>
public sealed class OrderEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CustomerId { get; set; } = "";
    public string Channel { get; set; } = "web";          // mobile / web — нужно для анализа ложных срабатываний WAF
    public decimal Total { get; set; }
    public string Status { get; set; } = "Created";       // Created -> Paid / AwaitingChallenge / Declined
    public string ItemsJson { get; set; } = "[]";
    public string? Comment { get; set; }
    public string? PaymentTransactionId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class OrderDbContext(DbContextOptions<OrderDbContext> options) : DbContext(options)
{
    public DbSet<OrderEntity> Orders => Set<OrderEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var order = modelBuilder.Entity<OrderEntity>();
        order.ToTable("Orders");
        order.HasKey(o => o.Id);
        order.Property(o => o.CustomerId).HasMaxLength(64).IsRequired();
        order.Property(o => o.Channel).HasMaxLength(16);
        order.Property(o => o.Status).HasMaxLength(32);
        order.Property(o => o.Total).HasConversion<double>(); // SQLite не имеет decimal
    }
}
