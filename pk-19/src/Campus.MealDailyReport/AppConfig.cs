using System.Globalization;
using System.Text.Json;

namespace Campus.MealDailyReport;

/// <summary>
/// Параметры запуска утилиты. Собираются по иерархии конфигурации .NET:
///   1) appsettings.json        — базовые статические настройки (URL, таймауты);
///   2) переменные окружения    — CAMPUS_STATION_ID, CAMPUS_OPERATOR_ID, CAMPUS_SOURCE_URL
///                                (настройки конкретного АРМ, задаются администратором);
///   3) аргументы командной строки — наивысший приоритет (--date, --source, --out, --station, --operator).
/// Каждый следующий уровень переопределяет предыдущий.
/// </summary>
public sealed class AppConfig
{
    public string SourceUrl { get; set; } = "https://jsonplaceholder.typicode.com/posts";
    public string OutputDir { get; set; } = "reports";
    public int TimeoutSeconds { get; set; } = 10;
    public int Retries { get; set; } = 3;
    public string StationId { get; set; } = "CANTEEN-A1-POS-01";

    /// <summary>Оператор, сформировавший отчёт (полнота данных: кто и где сформировал).</summary>
    public string OperatorId { get; set; } = Environment.UserName;

    /// <summary>Отчётные сутки.</summary>
    public DateOnly ReportDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);

    public static AppConfig Build(string baseDirectory, string[] args)
    {
        // ---- 1. appsettings.json ---------------------------------------------
        string path = Path.Combine(baseDirectory, "appsettings.json");
        var config = File.Exists(path)
            ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            }) ?? new AppConfig()
            : new AppConfig();

        // ---- 2. переменные окружения с префиксом CAMPUS_ ---------------------
        config.StationId = Env("CAMPUS_STATION_ID") ?? config.StationId;
        config.OperatorId = Env("CAMPUS_OPERATOR_ID") ?? config.OperatorId;
        config.SourceUrl = Env("CAMPUS_SOURCE_URL") ?? config.SourceUrl;

        // ---- 3. аргументы командной строки ------------------------------------
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            string key = args[i].TrimStart('-').ToLowerInvariant();
            string value = args[i + 1];
            switch (key)
            {
                case "date":
                    if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    {
                        throw new ArgumentException($"Параметр --date задаётся в формате ГГГГ-ММ-ДД, получено: «{value}»");
                    }
                    config.ReportDate = date;
                    break;
                case "source": config.SourceUrl = value; break;
                case "out": config.OutputDir = value; break;
                case "station": config.StationId = value; break;
                case "operator": config.OperatorId = value; break;
                default: throw new ArgumentException($"Неизвестный параметр: {args[i]}");
            }
        }

        if (config.TimeoutSeconds <= 0 || config.Retries <= 0)
        {
            throw new ArgumentException("TimeoutSeconds и Retries в appsettings.json должны быть больше нуля");
        }

        return config;
    }

    private static string? Env(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
