using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Campus.MealStatement;

/// <summary>Строка сводной ведомости по категории питания.</summary>
public sealed record CategoryTotal(string Category, int Transactions, int Portions, decimal Amount, decimal Subsidy, decimal ToPay)
{
    public decimal AverageCheck => Transactions == 0 ? 0 : Math.Round(ToPay / Transactions, 2);
}

/// <summary>
/// Экспорт в CSV для Excel (русская локаль): разделитель «;», десятичная запятая,
/// кодировка UTF-8 с BOM (иначе Excel покажет кракозябры), экранирование кавычек.
/// Для защиты от изменений рядом создаётся файл контрольных сумм SHA-256.
/// </summary>
public sealed class StatementExporter(string outputDir, Logger log)
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly Encoding Utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public List<string> Written { get; } = [];

    /// <summary>Группировка транзакций по категориям + итоговая строка.</summary>
    public static List<CategoryTotal> GroupByCategory(IEnumerable<MealTransaction> transactions) =>
        transactions
            .GroupBy(t => t.Category)
            .OrderBy(g => Array.IndexOf(TransactionSimulator.Categories, g.Key))
            .Select(g => new CategoryTotal(
                g.Key.Name,
                g.Count(),
                g.Sum(t => t.Portions),
                g.Sum(t => t.Amount),
                g.Sum(t => t.Subsidy),
                g.Sum(t => t.ToPay)))
            .ToList();

    /// <summary>Файл 1 — детальный финансовый лог (каждая транзакция).</summary>
    public void WriteTransactions(string fileName, IReadOnlyList<MealTransaction> items)
    {
        var sb = new StringBuilder();
        Row(sb, "№", "ID транзакции", "Дата", "Время", "Карта", "Студент", "Категория", "Порций",
            "Цена, руб.", "Сумма, руб.", "Дотация, руб.", "К оплате, руб.", "Способ оплаты", "Касса");
        int n = 0;
        foreach (var t in items)
        {
            n++;
            Row(sb, n.ToString(Ru), t.TransactionId, t.Date.ToString("dd.MM.yyyy", Ru), t.Time.ToString("HH:mm", Ru),
                t.CardNumber, t.StudentId, t.Category.Name, t.Portions.ToString(Ru),
                Money(t.Price), Money(t.Amount), Money(t.Subsidy), Money(t.ToPay), t.PaymentMethod, t.CashDesk);
        }

        Save(fileName, sb);
    }

    /// <summary>Файл 2 — сводная ведомость по категориям питания.</summary>
    public void WriteCategoryStatement(string fileName, IReadOnlyList<CategoryTotal> totals, string header)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Escape(header));
        Row(sb, "Категория", "Транзакций", "Порций", "Сумма, руб.", "Дотация, руб.", "К оплате, руб.", "Средний чек, руб.", "Доля выручки, %");
        decimal total = totals.Sum(t => t.ToPay);
        foreach (var c in totals)
        {
            Row(sb, c.Category, c.Transactions.ToString(Ru), c.Portions.ToString(Ru), Money(c.Amount), Money(c.Subsidy),
                Money(c.ToPay), Money(c.AverageCheck), (total == 0 ? 0 : c.ToPay / total * 100).ToString("F1", Ru));
        }

        Row(sb, "ИТОГО", totals.Sum(c => c.Transactions).ToString(Ru), totals.Sum(c => c.Portions).ToString(Ru),
            Money(totals.Sum(c => c.Amount)), Money(totals.Sum(c => c.Subsidy)), Money(total),
            Money(totals.Sum(c => c.Transactions) == 0 ? 0 : Math.Round(total / totals.Sum(c => c.Transactions), 2)), "100,0");
        Save(fileName, sb);
    }

    /// <summary>Файл 3 — сводная по студентам × категориям (для бухгалтерии и соцотдела).</summary>
    public void WriteStudentStatement(string fileName, IReadOnlyList<MealTransaction> items)
    {
        var cats = TransactionSimulator.Categories;
        var sb = new StringBuilder();
        Row(sb, [.. new[] { "Студент", "Карта" }, .. cats.Select(c => c.Name + ", руб."), "Дотация, руб.", "Итого к оплате, руб."]);
        foreach (var g in items.GroupBy(t => (t.StudentId, t.CardNumber)).OrderBy(g => g.Key.StudentId))
        {
            Row(sb, [g.Key.StudentId, g.Key.CardNumber,
                .. cats.Select(c => Money(g.Where(t => t.Category == c).Sum(t => t.ToPay))),
                Money(g.Sum(t => t.Subsidy)), Money(g.Sum(t => t.ToPay))]);
        }

        Row(sb, ["ИТОГО", "",
            .. cats.Select(c => Money(items.Where(t => t.Category == c).Sum(t => t.ToPay))),
            Money(items.Sum(t => t.Subsidy)), Money(items.Sum(t => t.ToPay))]);
        Save(fileName, sb);
    }

    /// <summary>
    /// Контрольные суммы всех выгруженных файлов в формате sha256sum.
    /// Проверка: <c>sha256sum -c</c> или <c>Get-FileHash</c> в PowerShell.
    /// </summary>
    public string WriteChecksums(string fileName)
    {
        var sb = new StringBuilder();
        foreach (string path in Written)
        {
            using var stream = File.OpenRead(path);
            sb.Append(Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant())
              .Append("  ").AppendLine(Path.GetFileName(path));
        }

        string target = Path.Combine(outputDir, fileName);
        File.WriteAllText(target, sb.ToString(), new UTF8Encoding(false));
        return target;
    }

    private void Save(string fileName, StringBuilder content)
    {
        Directory.CreateDirectory(outputDir);
        string path = Path.Combine(outputDir, fileName);
        try
        {
            File.WriteAllText(path, content.ToString(), Utf8Bom);
        }
        catch (IOException) when (File.Exists(path))
        {
            // типичная ситуация: прошлая ведомость открыта в Excel и файл заблокирован
            string alternative = Path.Combine(outputDir,
                Path.GetFileNameWithoutExtension(fileName) + $"_{DateTime.Now:HHmmss}" + Path.GetExtension(fileName));
            log.Warn($"Файл {fileName} занят (открыт в Excel?) — сохраняю как {Path.GetFileName(alternative)}");
            File.WriteAllText(alternative, content.ToString(), Utf8Bom);
            path = alternative;
        }

        Written.Add(path);
        log.Ok($"Сохранён {Path.GetFileName(path)} ({new FileInfo(path).Length / 1024.0:F1} КБ)");
    }

    private static string Money(decimal value) => value.ToString("F2", Ru);

    private static void Row(StringBuilder sb, params string[] cells) =>
        sb.AppendLine(string.Join(';', cells.Select(Escape)));

    /// <summary>Экранирование по RFC 4180: поле с ; " или переводом строки берётся в кавычки.</summary>
    private static string Escape(string value) =>
        value.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
}
