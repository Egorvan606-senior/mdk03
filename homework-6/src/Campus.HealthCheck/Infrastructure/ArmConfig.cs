using System.Text.Json;

namespace Campus.HealthCheck.Infrastructure;

/// <summary>
/// Параметры АРМ, прочитанные из его appsettings.json.
/// Заполняется проверкой CFG и используется остальными проверками цепочки.
/// </summary>
public sealed record ArmConfig(
    string ArmId,
    string Location,
    string ApiCheckUrl,
    string CentralDbHost,
    int CentralDbPort,
    string LocalDbPath,
    string ReaderHost,
    int ReaderPort,
    string ReaderModel)
{
    /// <summary>
    /// Разбор конфигурации. Ключи задаются в формате "Секция:Ключ",
    /// как в Microsoft.Extensions.Configuration.
    /// </summary>
    public static ArmConfig Parse(JsonElement root, string installDir) => new(
        ArmId: Get(root, "Arm:Id"),
        Location: TryGet(root, "Arm:Location") ?? "—",
        ApiCheckUrl: Get(root, "Api:ExternalCheckUrl"),
        CentralDbHost: Get(root, "CentralDb:Host"),
        CentralDbPort: int.Parse(Get(root, "CentralDb:Port")),
        LocalDbPath: ToolOptions.Resolve(installDir, Get(root, "LocalDb:Path")),
        ReaderHost: Get(root, "CardReader:Host"),
        ReaderPort: int.Parse(Get(root, "CardReader:Port")),
        ReaderModel: TryGet(root, "CardReader:Model") ?? "не указана");

    public static string Get(JsonElement root, string key) =>
        TryGet(root, key) ?? throw new KeyNotFoundException($"нет ключа {key}");

    /// <summary>Значение по пути "A:B:C" или null, если ключа нет / он пустой.</summary>
    public static string? TryGet(JsonElement root, string key)
    {
        JsonElement current = root;
        foreach (string part in key.Split(':'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
            {
                return null;
            }
        }

        string? value = current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => current.GetRawText(),
            _ => null
        };

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

/// <summary>Общий контекст одного прогона: результаты ранних проверок нужны поздним.</summary>
public sealed class HealthCheckContext(ToolOptions options)
{
    public ToolOptions Options { get; } = options;

    /// <summary>Конфигурация АРМ; null, если проверка CFG не прошла.</summary>
    public ArmConfig? Arm { get; set; }

    /// <summary>Версия, найденная в каталоге установки.</summary>
    public string? InstalledVersion { get; set; }

    /// <summary>Внешний IP АРМ (из ответа API) — попадает в акт для настройки межсетевого экрана.</summary>
    public string? ExternalIp { get; set; }
}
