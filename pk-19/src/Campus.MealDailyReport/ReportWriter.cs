using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Campus.MealDailyReport;

/// <summary>
/// Формирование файлов суточного отчёта (подход Docs-as-Code: результат — текстовые файлы,
/// которые можно хранить в Git, сравнивать и открывать в Excel):
///   * meal_transactions_ГГГГММДД.csv  — реестр транзакций за сутки;
///   * meal_daily_report_ГГГГММДД.csv  — финансовые итоги по категориям;
///   * meal_daily_report_ГГГГММДД.md   — сводка для руководства/бухгалтерии;
///   * meal_daily_report_ГГГГММДД.sha256 — контрольные суммы (защита от изменений).
/// CSV — с разделителем «;», UTF-8 с BOM и запятой в дробных числах: так файл
/// корректно открывается двойным щелчком в русской версии Excel.
/// </summary>
public sealed class ReportWriter(string directory)
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly Encoding Utf8Bom = new UTF8Encoding(true);
    private readonly List<string> _written = [];

    public string Directory { get; } = directory;

    public string WriteTransactions(string fileName, IEnumerable<MealTransaction> transactions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Date;Time;TransactionId;StationId;OperatorId;Category;MealType;Amount;Subsidy;ToPay;Transaction_Status");
        foreach (var t in transactions)
        {
            sb.AppendLine(string.Join(';',
                t.Timestamp.ToString("dd.MM.yyyy", Ru),
                t.Timestamp.ToString("HH:mm", Ru),
                t.TransactionId,
                Csv(t.StationId),
                Csv(t.OperatorId),
                t.Category.ToRussian(),
                t.MealType,
                Money(t.Amount),
                Money(t.Subsidy),
                Money(t.ToPay),
                t.Status.ToRussian()));
        }

        return Save(fileName, sb.ToString());
    }

    public string WriteCategoryReport(string fileName, IReadOnlyList<CategoryTotals> totals, string header)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Csv(header));
        sb.AppendLine("Category;Success;Refunds;Declined;Revenue;RefundAmount;NetRevenue;Subsidy;Paid;AverageCheck");
        foreach (var c in totals)
        {
            sb.AppendLine(string.Join(';', c.Category.ToRussian(), c.Success, c.Refunds, c.Declined,
                Money(c.Revenue), Money(c.RefundAmount), Money(c.NetRevenue), Money(c.Subsidy), Money(c.Paid), Money(c.AverageCheck)));
        }

        // итоговая строка — для сверки с Z-отчётом кассы
        sb.AppendLine(string.Join(';', "ИТОГО",
            totals.Sum(c => c.Success), totals.Sum(c => c.Refunds), totals.Sum(c => c.Declined),
            Money(totals.Sum(c => c.Revenue)), Money(totals.Sum(c => c.RefundAmount)), Money(totals.Sum(c => c.NetRevenue)),
            Money(totals.Sum(c => c.Subsidy)), Money(totals.Sum(c => c.Paid)), ""));

        return Save(fileName, sb.ToString());
    }

    public string WriteMarkdownSummary(string fileName, AppConfig cfg, LoadResult loaded, IReadOnlyList<CategoryTotals> totals, int count)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Суточный отчёт по транзакциям питания — {cfg.ReportDate:dd.MM.yyyy}");
        sb.AppendLine();
        sb.AppendLine("| Параметр | Значение |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| ИС | «Умный кампус», модуль «Столовая» |");
        sb.AppendLine($"| АРМ (StationId) | `{cfg.StationId}` |");
        sb.AppendLine($"| Оператор | `{cfg.OperatorId}` |");
        sb.AppendLine($"| Сформирован | {DateTime.Now:dd.MM.yyyy HH:mm:ss} |");
        sb.AppendLine($"| Источник | {(loaded.FromCache ? $"**КЭШ** выгрузки от {loaded.LoadedAt:dd.MM.yyyy HH:mm}" : cfg.SourceUrl)} |");
        sb.AppendLine($"| Транзакций | {count} |");
        sb.AppendLine();
        sb.AppendLine("## Итоги по категориям");
        sb.AppendLine();
        sb.AppendLine("| Категория | Успешно | Возвраты | Отказы | Чистая выручка, ₽ | Дотация, ₽ | Оплачено, ₽ | Средний чек, ₽ |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var c in totals)
        {
            sb.AppendLine($"| {c.Category.ToRussian()} | {c.Success} | {c.Refunds} | {c.Declined} | {Money(c.NetRevenue)} | {Money(c.Subsidy)} | {Money(c.Paid)} | {Money(c.AverageCheck)} |");
        }

        sb.AppendLine($"| **ИТОГО** | **{totals.Sum(c => c.Success)}** | **{totals.Sum(c => c.Refunds)}** | **{totals.Sum(c => c.Declined)}** | " +
                      $"**{Money(totals.Sum(c => c.NetRevenue))}** | **{Money(totals.Sum(c => c.Subsidy))}** | **{Money(totals.Sum(c => c.Paid))}** | |");
        sb.AppendLine();
        sb.AppendLine("*Отчёт сформирован автоматически утилитой campus-meal-daily-report 1.0.0. " +
                      "Целостность файлов проверяется по контрольным суммам SHA-256.*");
        return Save(fileName, sb.ToString());
    }

    /// <summary>Контрольные суммы всех файлов отчёта (формат утилиты sha256sum).</summary>
    public string WriteChecksums(string fileName)
    {
        var sb = new StringBuilder();
        foreach (string path in _written)
        {
            using var stream = File.OpenRead(path);
            string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            sb.AppendLine($"{hash}  {Path.GetFileName(path)}");
        }

        string target = Path.Combine(Directory, fileName);
        File.WriteAllText(target, sb.ToString(), new UTF8Encoding(false));
        return target;
    }

    private string Save(string fileName, string content)
    {
        System.IO.Directory.CreateDirectory(Directory);
        string path = Path.Combine(Directory, fileName);
        File.WriteAllText(path, content, Utf8Bom);
        _written.Add(path);
        return path;
    }

    private static string Money(decimal value) => value.ToString("0.00", Ru);

    /// <summary>Экранирование значения CSV (кавычки, «;» и переводы строк).</summary>
    private static string Csv(string value) =>
        value.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
}
