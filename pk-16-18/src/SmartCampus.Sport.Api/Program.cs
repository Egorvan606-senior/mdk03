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
        .AddService(serviceName: "smart-campus-sport", serviceVersion: "1.6.0")
        .AddAttributes(new Dictionary<string, object>
        {
            ["deployment.environment"] = builder.Environment.EnvironmentName,
            ["campus.release.jira"] = CampusTelemetry.JiraIssue
        }))
    .WithTracing(tracing =>
    {
        tracing
            .AddAspNetCoreInstrumentation(o => o.RecordException = true)
            .AddSource(CampusTelemetry.SourceName)
            .AddOtlpExporter();

        // Локальная отладка в Visual Studio без Jaeger: span'ы со всеми тегами
        // (jira.issue, campus.entity_id ...) печатаются в окно консоли сервиса
        if (builder.Configuration.GetValue<bool>("Telemetry:ConsoleExporter"))
        {
            tracing.AddConsoleExporter();
        }
    });

// В каждой строке лога — TraceId/SpanId текущего запроса (scope логирования).
// По этому TraceId строка лога контейнера находится в Jaeger (см. docs/DIGITAL_TRACE.md).
builder.Logging.AddSimpleConsole(o =>
{
    o.IncludeScopes = true;
    o.TimestampFormat = "HH:mm:ss ";
});

var app = builder.Build();

app.MapControllers();

// Liveness/readiness для Kubernetes (используются при kubectl rollout)
app.MapGet("/health", () => Results.Ok(new { status = "Healthy", version = "1.6.0" }));

app.Run();

/// <summary>Нужен для WebApplicationFactory в интеграционных тестах.</summary>
public partial class Program;
