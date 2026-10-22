using System.Text.Json;
using System.Text.Json.Serialization;

namespace Campus.HealthCheck.Infrastructure;

/// <summary>
/// Настройки самой утилиты (файл healthcheck.json рядом с exe).
/// Адреса БД, API и считывателя утилита НЕ хранит у себя — она берёт их
/// из конфигурации АРМ (appsettings.json), то есть проверяет именно то,
/// с чем АРМ будет работать после обновления.
/// </summary>
public sealed class ToolOptions
{
    /// <summary>Каталог установки клиента АРМ (C:\Program Files\SmartCampus\Operator).</summary>
    public string InstallDir { get; set; } = @"C:\Program Files\SmartCampus\Operator";

    /// <summary>Версия, которая должна стоять после обновления.</summary>
    public string ExpectedVersion { get; set; } = "2.4.0";

    public string ArmConfigFile { get; set; } = "appsettings.json";

    /// <summary>Ключи конфигурации АРМ, без которых он не запустится.</summary>
    public string[] RequiredConfigKeys { get; set; } = [];

    public ThresholdOptions Thresholds { get; set; } = new();
    public TimeoutOptions Timeouts { get; set; } = new();

    /// <summary>Повторы для сетевых проверок (кратковременные сбои сети не должны валить акт).</summary>
    public int NetworkRetries { get; set; } = 3;

    /// <summary>
    /// Эмуляторы для испытательного стенда, где нет оборудования и серверов кампуса.
    /// На боевом АРМ оба флага должны быть false (это отражается в акте).
    /// </summary>
    public EmulatorOptions Emulators { get; set; } = new();

    public string ReportsDir { get; set; } = "reports";
    public string LogsDir { get; set; } = "logs";
    public string BackupsDir { get; set; } = "backups";

    /// <summary>Каталог данных АРМ, которые обновление не должно изменить.</summary>
    public string[] ProtectedData { get; set; } = ["data"];

    public static ToolOptions Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Не найден файл настроек утилиты: {path}");
        }

        var options = JsonSerializer.Deserialize<ToolOptions>(File.ReadAllText(path), JsonOptions)
                      ?? throw new InvalidDataException($"Файл {path} пуст.");

        // относительные пути считаем от каталога, где лежит healthcheck.json
        string baseDir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        options.InstallDir = Resolve(baseDir, options.InstallDir);
        options.ReportsDir = Resolve(baseDir, options.ReportsDir);
        options.LogsDir = Resolve(baseDir, options.LogsDir);
        options.BackupsDir = Resolve(baseDir, options.BackupsDir);
        return options;
    }

    public static string Resolve(string baseDir, string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDir, path));

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}

public sealed class EmulatorOptions
{
    /// <summary>Эмулятор контроллера считывателя пропусков (отвечает на STATUS).</summary>
    public bool CardReader { get; set; }

    /// <summary>Эмулятор ответа PostgreSQL на SSLRequest (стенд без центральной БД).</summary>
    public bool CentralDb { get; set; }

    public bool Any => CardReader || CentralDb;
}

public sealed class ThresholdOptions
{
    public int CentralDbWarnMs { get; set; } = 200;
    public int ApiWarnMs { get; set; } = 800;
    public int ReaderWarnMs { get; set; } = 150;
    public long MinFreeDiskMb { get; set; } = 500;
}

public sealed class TimeoutOptions
{
    public int CentralDbMs { get; set; } = 3000;
    public int ApiMs { get; set; } = 5000;
    public int ReaderMs { get; set; } = 2000;
}
