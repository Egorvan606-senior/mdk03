using System.Data.Common;
using Npgsql;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SmartCampus.Sport.Data;
using SmartCampus.Sport.Telemetry;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<BookingSchemaOptions>(builder.Configuration.GetSection(BookingSchemaOptions.Section));

// Хранилище: PostgreSQL на стендах и в проде, память — для локального запуска
string? connectionString = builder.Configuration.GetConnectionString("Campus");
if (string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddSingleton<IBookingRepository, InMemoryBookingRepository>();
}
else
{
    builder.Services.AddSingleton<DbDataSource>(_ => NpgsqlDataSource.Create(connectionString));
    builder.Services.AddScoped<IBookingRepository, PostgresBookingRepository>();
}

// OpenTelemetry: трассы входящих HTTP-запросов + собственный ActivitySource модуля.
// Экспорт по OTLP (gRPC, порт 4317) в коллектор; адрес задаётся переменной
// окружения OTEL_EXPORTER_OTLP_ENDPOINT, по умолчанию http://localhost:4317.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource
        .AddService(serviceName: "smart-campus-sport", serviceVersion: "1.9.0")
        .AddAttributes(new Dictionary<string, object>
        {
            ["deployment.environment"] = builder.Environment.EnvironmentName,
            ["campus.release.jira"] = CampusTelemetry.JiraIssue
        }))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation(o => o.RecordException = true)
        .AddSource(CampusTelemetry.SourceName)
        .AddOtlpExporter());

var app = builder.Build();

app.MapControllers();

// Liveness/readiness для Kubernetes (используются при kubectl rollout)
app.MapGet("/health", () => Results.Ok(new { status = "Healthy", version = "1.9.0" }));

app.Run();

/// <summary>Нужен для WebApplicationFactory в интеграционных тестах.</summary>
public partial class Program;
