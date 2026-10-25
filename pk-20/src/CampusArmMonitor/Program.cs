// ============================================================================
// CampusArmMonitor — АРМ дежурного инженера ИС «Умный кампус» (.NET 8)
// Промежуточный контроль № 20, вариант 10: «Экспортер ведомости питания столовой».
// Автор: Иванов Е.М., группа И14-23-9.
//
// Программа построена на эталонном примере из задания и расширена по варианту:
//   1. Инициализирует Serilog с двумя приёмниками (Dual Sinks): консоль + локальный
//      файл C:\SmartCampus\Logs\arm_monitor_log<дата>.txt с ежедневной ротацией.
//   2. Проверяет доступность публичного API выгрузки транзакций (jsonplaceholder /posts)
//      и фиксирует время отклика (Latency) через Stopwatch.
//   3. Симулирует выгрузку транзакций питания, группирует их по категориям льгот
//      и сохраняет ведомость в CSV (C:\SmartCampus\Reports).
//   4. Имитирует отправку метрики времени отклика в систему мониторинга.
// Каждая проверка получает уникальный TraceId (W3C) — он есть в логе, в CSV и в метрике,
// поэтому все артефакты одной проверки связываются между собой (цифровой след).
//
// Коды завершения: 0 — API доступен, ведомость сформирована; 1 — сбой API/сети;
//                  2 — API доступен, но время отклика выше порога (деградация).
// ============================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Serilog;
using Serilog.Context;

namespace CampusArmMonitor
{
    internal class Program
    {
        private static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            // 0. Конфигурация: appsettings.json -> переменные окружения CAMPUS_* -> аргументы (--url)
            var settings = ArmSettings.Load(AppContext.BaseDirectory, args);

            // 1. Инициализация локального логирования через Serilog (Dual Sinks)
            const string template =
                "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] ({StationId}) {Message:lj} {TraceId}{NewLine}{Exception}";

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .Enrich.FromLogContext()                                   // подхватывает TraceId из LogContext
                .Enrich.WithProperty("StationId", settings.StationId)      // метка конкретного АРМ
                .WriteTo.Console(outputTemplate: template)                 // канал 1: экран дежурного
                .WriteTo.File(                                             // канал 2: локальный файл для офлайн-аудита
                    path: Path.Combine(settings.LogDirectory, "arm_monitor_log.txt"),
                    rollingInterval: RollingInterval.Day,                  // новый файл каждые сутки
                    outputTemplate: template,
                    encoding: Encoding.UTF8)
                .CreateLogger();

            // Activity = операция проверки; .NET генерирует для неё уникальный W3C TraceId
            using var activity = new Activity("CanteenStatementExport").Start();
            string traceId = activity.TraceId.ToString();
            using var _ = LogContext.PushProperty("TraceId", $"trace={traceId}");

            Log.Information("=== АРМ Мониторинг: Запуск проверки инфраструктуры (v2.0.0, ПК {Machine}) ===", Environment.MachineName);
            Log.Debug("Конфигурация: источник {Url}, таймаут {Timeout} с, порог задержки {Threshold} мс",
                settings.TargetUrl, settings.TimeoutSeconds, settings.LatencyWarningMs);

            string targetUrl = settings.TargetUrl;
            var stopwatch = Stopwatch.StartNew();

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds) };
            client.DefaultRequestHeaders.Add("User-Agent", "SmartCampus-ARM-Monitor");
            // Заголовок W3C traceparent: сервер (если поддерживает OpenTelemetry) продолжит ту же трассу
            client.DefaultRequestHeaders.Add("traceparent", activity.Id);

            int exitCode = 1; // по умолчанию — сбой; меняется только при успешной проверке
            try
            {
                // 2. Выполнение запроса к публичному API
                HttpResponseMessage response = await client.GetAsync(targetUrl);
                stopwatch.Stop();
                long latency = stopwatch.ElapsedMilliseconds;

                if (response.IsSuccessStatusCode)
                {
                    string jsonContent = await response.Content.ReadAsStringAsync();
                    var posts = MealStatement.Parse(jsonContent);

                    Log.Information("✔ API доступен. Код {StatusCode}, записей выгрузки: {Count}, время отклика: {ElapsedMs} мс",
                        (int)response.StatusCode, posts.Count, latency);

                    // 3. Экспорт ведомости питания по категориям льгот
                    var lines = MealStatement.Build(posts);
                    foreach (var line in lines)
                    {
                        // структурированный лог: значения — параметры события, а не склеенная строка
                        Log.Information("Категория {Code} «{Category}»: транзакций {Count}, сумма {Amount:N2} ₽, дотация {Subsidy:N2} ₽, к оплате {ToPay:N2} ₽",
                            line.Category.Code, line.Category.Name, line.Transactions, line.Amount, line.Subsidy, line.ToPay);
                    }

                    string csv = MealStatement.SaveCsv(settings.ReportDirectory, lines, settings.StationId, traceId);
                    Log.Information("Ведомость сохранена: {CsvPath}", csv);

                    if (latency > settings.LatencyWarningMs)
                    {
                        Log.Warning("⚠ Деградация: время отклика {ElapsedMs} мс превышает порог {Threshold} мс", latency, settings.LatencyWarningMs);
                        exitCode = 2;
                    }
                    else
                    {
                        exitCode = 0;
                    }

                    // 4. Имитация отправки метрики в систему мониторинга
                    SendMetric(settings, latency, up: true, traceId);
                }
                else
                {
                    Log.Warning("⚠ API вернул код ошибки: {StatusCode}, время отклика {ElapsedMs} мс", response.StatusCode, latency);
                    SendMetric(settings, latency, up: false, traceId);
                    exitCode = 1;
                }
            }
            catch (TaskCanceledException ex)
            {
                // HttpClient сообщает о таймауте через TaskCanceledException
                Log.Error(ex, "✖ Таймаут: сервер не ответил за {Timeout} с", settings.TimeoutSeconds);
                SendMetric(settings, stopwatch.ElapsedMilliseconds, up: false, traceId);
                exitCode = 1;
            }
            catch (JsonException ex)
            {
                Log.Error(ex, "✖ API ответил, но данные выгрузки повреждены (некорректный JSON)");
                exitCode = 1;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "✖ Критическая ошибка соединения с сервером мониторинга!");
                SendMetric(settings, stopwatch.ElapsedMilliseconds, up: false, traceId);
                exitCode = 1;
            }
            finally
            {
                Log.Information("=== Проверка завершена, код завершения {ExitCode} ===", exitCode);
                Log.CloseAndFlush(); // гарантированно дописывает буфер в файл лога
            }

            return exitCode;
        }

        /// <summary>
        /// Имитация отправки метрики. В промышленной эксплуатации метрика уходит по OTLP
        /// (порт 4317) в OpenTelemetry Collector; здесь она записывается в журнал метрик
        /// в формате Prometheus и фиксируется в логе Serilog.
        /// </summary>
        private static void SendMetric(ArmSettings settings, long latencyMs, bool up, string traceId)
        {
            string labels = $"station=\"{settings.StationId}\",endpoint=\"posts\"";
            string payload =
                $"arm_api_up{{{labels}}} {(up ? 1 : 0)}\n" +
                $"arm_api_latency_ms{{{labels}}} {latencyMs}\n";

            try
            {
                Directory.CreateDirectory(settings.LogDirectory);
                File.AppendAllText(Path.Combine(settings.LogDirectory, "arm_metrics.prom"),
                    $"# {DateTime.Now:O} trace={traceId}\n{payload}");
                Log.Information("Метрика отправлена (имитация OTLP -> otel.campus.internal:4317): up={Up}, latency={LatencyMs} мс", up ? 1 : 0, latencyMs);
            }
            catch (IOException ex)
            {
                // сбой отправки метрики не должен ронять проверку
                Log.Warning(ex, "Не удалось записать метрику");
            }
        }
    }

    /// <summary>Настройки АРМ: appsettings.json, переопределяемые переменными окружения и аргументами.</summary>
    internal sealed class ArmSettings
    {
        public string TargetUrl { get; set; } = "https://jsonplaceholder.typicode.com/posts";
        public string StationId { get; set; } = "CANTEEN-A1-ARM-01";
        public string LogDirectory { get; set; } = @"C:\SmartCampus\Logs";
        public string ReportDirectory { get; set; } = @"C:\SmartCampus\Reports";
        public int TimeoutSeconds { get; set; } = 10;
        public int LatencyWarningMs { get; set; } = 2000;

        public static ArmSettings Load(string baseDir, string[] args)
        {
            string path = Path.Combine(baseDir, "appsettings.json");
            var s = File.Exists(path)
                ? JsonSerializer.Deserialize<ArmSettings>(File.ReadAllText(path),
                      new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip })
                  ?? new ArmSettings()
                : new ArmSettings();

            // Переменные окружения АРМ (задаёт администратор при развертывании)
            s.StationId = Environment.GetEnvironmentVariable("CAMPUS_STATION_ID") ?? s.StationId;
            s.TargetUrl = Environment.GetEnvironmentVariable("CAMPUS_TARGET_URL") ?? s.TargetUrl;

            // Аргументы командной строки — наивысший приоритет
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "--url") s.TargetUrl = args[i + 1];
            }

            // Вне Windows (CI/Linux-стенд) путь C:\ недоступен — пишем рядом с программой
            if (!OperatingSystem.IsWindows())
            {
                s.LogDirectory = Path.Combine(baseDir, "SmartCampus", "Logs");
                s.ReportDirectory = Path.Combine(baseDir, "SmartCampus", "Reports");
            }

            return s;
        }
    }
}
