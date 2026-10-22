using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Campus.HealthCheck.Checks;
using Campus.HealthCheck.Infrastructure;

namespace Campus.HealthCheck.Reports;

/// <summary>Данные для акта готовности АРМ.</summary>
public sealed class ReadinessReport
{
    public string Number { get; init; } = $"HC-{DateTime.Now:yyyyMMdd-HHmmss}";
    public string Operation { get; init; } = "Проверка после обновления";
    public string ArmId { get; set; } = "не определён";
    public string Location { get; set; } = "—";
    public string? VersionBefore { get; set; }
    public string? VersionAfter { get; set; }
    public string ExpectedVersion { get; init; } = "";
    public DateTime StartedAt { get; init; } = DateTime.Now;
    public DateTime FinishedAt { get; set; }
    public string Machine { get; init; } = Environment.MachineName;
    public string User { get; init; } = Environment.UserName;
    public string Os { get; init; } = RuntimeInformation.OSDescription;
    public string Runtime { get; init; } = RuntimeInformation.FrameworkDescription;
    public string? ExternalIp { get; set; }
    public List<string> Emulators { get; init; } = [];
    public List<string> UpdateSteps { get; } = [];
    public IReadOnlyList<CheckResult> Results { get; set; } = [];
    public Readiness Verdict { get; set; }
    public bool RolledBack { get; set; }
    public string? BackupPath { get; set; }
    public string? LogPath { get; set; }

    public string VerdictText => Verdict switch
    {
        Readiness.Ready => "АРМ ГОТОВО к промышленной эксплуатации",
        Readiness.ReadyWithRemarks => "АРМ ГОТОВО С ЗАМЕЧАНИЯМИ (допуск при условии устранения)",
        _ => RolledBack
            ? "АРМ НЕ ГОТОВО — выполнен автоматический откат на предыдущую версию"
            : "АРМ НЕ ГОТОВО к промышленной эксплуатации"
    };
}

/// <summary>
/// Формирует акт готовности в двух видах: Markdown (для печати и подписи,
/// хранится в репозитории эксплуатационной документации) и JSON (для системы
/// мониторинга / CMDB). Плюс вывод итоговой таблицы в консоль.
/// </summary>
public static class ReadinessReportWriter
{
    public static (string Markdown, string Json) Save(ReadinessReport report, string reportsDir)
    {
        Directory.CreateDirectory(reportsDir);
        string baseName = $"readiness_act_{report.ArmId}_{report.StartedAt:yyyyMMdd_HHmmss}";
        string mdPath = Path.Combine(reportsDir, baseName + ".md");
        string jsonPath = Path.Combine(reportsDir, baseName + ".json");

        File.WriteAllText(mdPath, BuildMarkdown(report), new UTF8Encoding(true));
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, ToolOptions.JsonOptions), new UTF8Encoding(false));
        return (mdPath, jsonPath);
    }

    public static string BuildMarkdown(ReadinessReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Акт готовности АРМ к промышленной эксплуатации № {r.Number}");
        sb.AppendLine();
        sb.AppendLine("| Параметр | Значение |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Рабочее место | {r.ArmId} — {r.Location} |");
        sb.AppendLine($"| Операция | {r.Operation} |");
        sb.AppendLine($"| Версия до / после | {r.VersionBefore ?? "—"} → {r.VersionAfter ?? "—"} (ожидается {r.ExpectedVersion}) |");
        sb.AppendLine($"| Компьютер / пользователь | {r.Machine} / {r.User} |");
        sb.AppendLine($"| ОС / платформа | {r.Os} / {r.Runtime} |");
        sb.AppendLine($"| Внешний IP АРМ | {r.ExternalIp ?? "не определён"} |");
        sb.AppendLine($"| Начало / окончание | {r.StartedAt:dd.MM.yyyy HH:mm:ss} / {r.FinishedAt:HH:mm:ss} |");
        if (r.Emulators.Count > 0)
        {
            sb.AppendLine($"| Режим стенда (эмуляторы) | {string.Join("; ", r.Emulators)} |");
        }

        if (r.UpdateSteps.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Ход обновления");
            sb.AppendLine();
            for (int i = 0; i < r.UpdateSteps.Count; i++)
            {
                sb.AppendLine($"{i + 1}. {r.UpdateSteps[i]}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Результаты Health Check");
        sb.AppendLine();
        sb.AppendLine("| № | Проверка | Критичность | Статус | Задержка, мс | Попыток | Подробности |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        int n = 0;
        foreach (var c in r.Results)
        {
            n++;
            string sev = c.Severity == CheckSeverity.Critical ? "критичная" : "некритичная";
            sb.AppendLine($"| {n} | {c.Title} (`{c.Code}`) | {sev} | **{c.StatusText}** | {c.LatencyMs} | {c.Attempts} | {c.Details.Replace("|", "/")} |");
        }

        var network = r.Results.Where(c => c.Code is "CENTRAL_DB" or "API" or "READER" && c.Status != CheckStatus.Fail).ToList();
        if (network.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Суммарная задержка сетевых проверок: {network.Sum(c => c.LatencyMs)} мс, максимальная: {network.Max(c => c.LatencyMs)} мс.");
        }

        sb.AppendLine();
        sb.AppendLine("## Заключение");
        sb.AppendLine();
        sb.AppendLine($"**{r.VerdictText}.**");
        if (r.BackupPath is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"Резервная копия до обновления: `{r.BackupPath}`.");
        }

        if (r.LogPath is not null)
        {
            sb.AppendLine($"Журнал работы утилиты: `{r.LogPath}`.");
        }

        sb.AppendLine();
        sb.AppendLine("| Роль | ФИО | Подпись | Дата |");
        sb.AppendLine("|---|---|---|---|");
        sb.AppendLine("| Системный администратор | | | |");
        sb.AppendLine("| Ответственный за АРМ (оператор) | | | |");
        return sb.ToString();
    }

    /// <summary>Итоговая таблица в консоли.</summary>
    public static void PrintSummary(ReadinessReport r, FileLogger log)
    {
        log.Raw("");
        log.Raw("┌────┬────────────┬────────┬─────────┬─────────┐", ConsoleColor.DarkGray);
        log.Raw("│ №  │ Проверка   │ Статус │ Задержка│ Попыток │", ConsoleColor.DarkGray);
        log.Raw("├────┼────────────┼────────┼─────────┼─────────┤", ConsoleColor.DarkGray);
        int n = 0;
        foreach (var c in r.Results)
        {
            n++;
            var color = c.Status switch
            {
                CheckStatus.Pass => ConsoleColor.Green,
                CheckStatus.Warning => ConsoleColor.Yellow,
                _ => ConsoleColor.Red
            };
            log.Raw($"│ {n,-2} │ {c.Code,-10} │ {c.StatusText,-6} │ {c.LatencyMs,5} мс│ {c.Attempts,7} │", color);
        }

        log.Raw("└────┴────────────┴────────┴─────────┴─────────┘", ConsoleColor.DarkGray);
        log.Raw("");
        var verdictColor = r.Verdict switch
        {
            Readiness.Ready => ConsoleColor.Green,
            Readiness.ReadyWithRemarks => ConsoleColor.Yellow,
            _ => ConsoleColor.Red
        };
        log.Raw($"ИТОГ: {r.VerdictText}", verdictColor);
    }
}
