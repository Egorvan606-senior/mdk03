// ============================================================================
// campus-meal-daily-report — генератор суточных отчётов по транзакциям питания
// ИС «Умный кампус», модуль «Столовая». Промежуточный контроль № 19, вариант 10.
// Автор: Иванов Е.М., группа И14-23-9.
//
// Порядок работы:
//   1. Сбор конфигурации: appsettings.json -> переменные CAMPUS_* -> аргументы.
//   2. Выгрузка «транзакций» с публичного мок-сервера jsonplaceholder.typicode.com/posts
//      через HttpClient (таймаут, повторы, кэш при недоступности сети).
//   3. Симуляция транзакций кассы за отчётные сутки.
//   4. Суммирование финансовых показателей по категориям (бюджет / коммерция).
//   5. Сохранение отчёта: CSV (реестр и итоги) + Markdown-сводка + SHA-256.
//
// Запуск:  dotnet run -- --date 2026-10-24
// Коды завершения: 0 — успех; 1 — ошибка; 2 — отчёт сформирован по кэшу (предварительный).
// ============================================================================

using System.Globalization;
using System.Text;
using Campus.MealDailyReport;

Console.OutputEncoding = Encoding.UTF8;
var ru = CultureInfo.GetCultureInfo("ru-RU");

AppConfig cfg;
try
{
    cfg = AppConfig.Build(AppContext.BaseDirectory, args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"Ошибка параметров: {ex.Message}");
    return 1;
}

// Каждые сутки — отдельная папка: reports\2026-10-24\...
string dayDir = Path.GetFullPath(Path.Combine(cfg.OutputDir, cfg.ReportDate.ToString("yyyy-MM-dd")));
using var log = new ReportLog(Path.GetFullPath(Path.Combine(cfg.OutputDir, "logs")));
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); }; // Ctrl+C — корректная остановка

log.Raw("=====================================================================", ConsoleColor.Cyan);
log.Raw("  ИС «Умный кампус» · Суточный отчёт по транзакциям питания · v1.0.0", ConsoleColor.Cyan);
log.Raw($"  Сутки: {cfg.ReportDate.ToString("dd MMMM yyyy (dddd)", ru)}", ConsoleColor.Cyan);
log.Raw($"  АРМ: {cfg.StationId} · оператор: {cfg.OperatorId} · ПК: {Environment.MachineName}", ConsoleColor.Cyan);
log.Raw("=====================================================================", ConsoleColor.Cyan);

try
{
    // ---- 1. Выгрузка с мок-сервера -----------------------------------------
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(cfg.TimeoutSeconds) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("SmartCampus-MealDailyReport/1.0");
    string cachePath = Path.GetFullPath(Path.Combine(cfg.OutputDir, "cache", "posts_cache.json"));
    var loaded = await new MockServerClient(http, log, cachePath, cfg.Retries).LoadAsync(cfg.SourceUrl, cts.Token);

    if (loaded.Posts.Count == 0)
    {
        log.Error("Выгрузка пуста — отчёт не формируется");
        return 1;
    }

    // ---- 2. Симуляция транзакций ---------------------------------------------
    var transactions = loaded.Posts
        .Select(p => TransactionSimulator.Create(p, cfg.ReportDate, cfg.StationId, cfg.OperatorId))
        .OrderBy(t => t.Timestamp)
        .ThenBy(t => t.TransactionId)
        .ToList();

    // Контроль полноты: номера транзакций уникальны (повтор = двойная выгрузка, суммы не сойдутся)
    var duplicates = transactions.GroupBy(t => t.TransactionId).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
    if (duplicates.Count > 0)
    {
        log.Error($"Дубликаты транзакций: {string.Join(", ", duplicates)} — отчёт не формируется");
        return 1;
    }

    log.Ok($"Сформировано транзакций за сутки: {transactions.Count}");

    // ---- 3. Суммирование по категориям ---------------------------------------
    var totals = TransactionSimulator.Summarize(transactions);

    // ---- 4. Сохранение отчёта (Docs-as-Code) ------------------------------------
    string stamp = cfg.ReportDate.ToString("yyyyMMdd");
    string header = $"Суточный отчёт по питанию за {cfg.ReportDate:dd.MM.yyyy}; АРМ {cfg.StationId}; оператор {cfg.OperatorId}; " +
                    $"сформирован {DateTime.Now:dd.MM.yyyy HH:mm}; " +
                    (loaded.FromCache ? $"ПРЕДВАРИТЕЛЬНЫЙ (кэш от {loaded.LoadedAt:dd.MM.yyyy HH:mm})" : $"источник {cfg.SourceUrl}");

    var writer = new ReportWriter(dayDir);
    writer.WriteTransactions($"meal_transactions_{stamp}.csv", transactions);
    writer.WriteCategoryReport($"meal_daily_report_{stamp}.csv", totals, header);
    writer.WriteMarkdownSummary($"meal_daily_report_{stamp}.md", cfg, loaded, totals, transactions.Count);
    string sums = writer.WriteChecksums($"meal_daily_report_{stamp}.sha256");

    // ---- 5. Итоги на экран оператора -------------------------------------------
    log.Raw("");
    log.Raw($"{"Категория",-11}{"Успешно",8}{"Возвр.",7}{"Отказ",7}{"Выручка",12}{"Дотация",11}{"Оплачено",12}{"Ср.чек",9}", ConsoleColor.White);
    log.Raw(new string('─', 77), ConsoleColor.DarkGray);
    foreach (var c in totals)
    {
        log.Raw($"{c.Category.ToRussian(),-11}{c.Success,8}{c.Refunds,7}{c.Declined,7}{c.NetRevenue.ToString("N2", ru),12}" +
                $"{c.Subsidy.ToString("N2", ru),11}{c.Paid.ToString("N2", ru),12}{c.AverageCheck.ToString("N2", ru),9}");
    }

    log.Raw(new string('─', 77), ConsoleColor.DarkGray);
    log.Raw($"{"ИТОГО",-11}{totals.Sum(c => c.Success),8}{totals.Sum(c => c.Refunds),7}{totals.Sum(c => c.Declined),7}" +
            $"{totals.Sum(c => c.NetRevenue).ToString("N2", ru),12}{totals.Sum(c => c.Subsidy).ToString("N2", ru),11}" +
            $"{totals.Sum(c => c.Paid).ToString("N2", ru),12}", ConsoleColor.White);
    log.Raw("");
    log.Ok($"Отчёт сохранён: {dayDir}");
    log.Info($"Контрольные суммы: {Path.GetFileName(sums)} · журнал: {log.FilePath}");

    if (loaded.FromCache)
    {
        log.Warn("Отчёт ПРЕДВАРИТЕЛЬНЫЙ (по кэшу). После восстановления связи сформируйте его повторно.");
        return 2;
    }

    return 0;
}
catch (OperationCanceledException)
{
    log.Error("Формирование отчёта прервано оператором (Ctrl+C)");
    return 1;
}
catch (InvalidOperationException ex)
{
    log.Error(ex.Message);
    return 1;
}
catch (UnauthorizedAccessException ex)
{
    log.Error($"Нет прав на запись в папку отчётов: {ex.Message}");
    return 1;
}
catch (IOException ex)
{
    log.Error($"Ошибка записи файла (файл открыт в Excel?): {ex.Message}");
    return 1;
}
