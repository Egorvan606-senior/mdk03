// ==========================================================================
// campus-meal-statement — генератор ведомости питания (финансовый лог столовой)
// ИС «Умный кампус». Практическая работа № 7, вариант 10.
//
// Что делает:
//   1. Загружает записи из публичного API (по умолчанию jsonplaceholder /posts),
//      при недоступности API работает по кэшу последней выгрузки.
//   2. Симулирует по ним транзакции питания за месяц (правила — MealTransaction.cs).
//   3. Группирует данные по категориям и по студентам.
//   4. Экспортирует три CSV-файла для Excel и файл контрольных сумм SHA-256.
//
// Параметры:
//   --month 2026-10        отчётный месяц (по умолчанию — текущий)
//   --source <url|файл>    источник данных (по умолчанию из mealstatement.json)
//   --out <папка>          куда сохранить ведомости
// Коды завершения: 0 — успех, 1 — ошибка данных/источника, 2 — ведомость по кэшу.
// ==========================================================================

using System.Globalization;
using System.Text;
using System.Text.Json;
using Campus.MealStatement;

Console.OutputEncoding = Encoding.UTF8;
var ru = CultureInfo.GetCultureInfo("ru-RU");

// ---- настройки: mealstatement.json + параметры командной строки ----------
var settings = Settings.Load(Path.Combine(AppContext.BaseDirectory, "mealstatement.json"));
var argsMap = ParseArgs(args);
string source = argsMap.GetValueOrDefault("source", settings.SourceUrl);
string outDir = Path.GetFullPath(argsMap.GetValueOrDefault("out", settings.OutputDir));
if (!TryParseMonth(argsMap.GetValueOrDefault("month", DateTime.Today.ToString("yyyy-MM")), out int year, out int month))
{
    Console.Error.WriteLine("Ошибка: --month задаётся в формате ГГГГ-ММ, например 2026-10");
    return 1;
}

using var log = new Logger(Path.Combine(outDir, "logs"));
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

log.Raw("==========================================================", ConsoleColor.Cyan);
log.Raw("  ИС «Умный кампус» · Генератор ведомости питания 1.0", ConsoleColor.Cyan);
log.Raw($"  Отчётный период: {new DateTime(year, month, 1).ToString("MMMM yyyy", ru)} · АРМ {Environment.MachineName}", ConsoleColor.Cyan);
log.Raw("==========================================================", ConsoleColor.Cyan);

try
{
    // ---- 1. загрузка --------------------------------------------------------
    LoadResult loaded;
    if (File.Exists(source))
    {
        log.Info($"Источник — файл выгрузки: {source}");
        var posts = JsonSerializer.Deserialize<List<SourcePost>>(File.ReadAllText(source),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        loaded = new LoadResult(posts, false, File.GetLastWriteTime(source), 0);
    }
    else
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("campus-meal-statement/1.0");
        loaded = await new PostsSource(http, log, Path.Combine(outDir, "cache", "posts_cache.json"), settings.Retries)
            .LoadAsync(source, cts.Token);
    }

    if (loaded.Posts.Count == 0)
    {
        log.Error("Источник вернул 0 записей — ведомость не формируется");
        return 1;
    }

    // ---- 2. симуляция транзакций --------------------------------------------
    var transactions = loaded.Posts
        .Select(p => TransactionSimulator.Create(p, year, month))
        .OrderBy(t => t.Timestamp)
        .ToList();

    var duplicates = transactions.GroupBy(t => t.TransactionId).Where(g => g.Count() > 1).ToList();
    if (duplicates.Count > 0)
    {
        // повтор номера транзакции — признак повторной выгрузки, финансовые данные не сходятся
        log.Error($"Найдены дубликаты транзакций: {string.Join(", ", duplicates.Select(d => d.Key))}");
        return 1;
    }

    log.Ok($"Сформировано транзакций: {transactions.Count} (отклонено записей источника: {loaded.Rejected})");

    // ---- 3–4. группировка и экспорт -----------------------------------------
    string period = $"{year}-{month:00}";
    var exporter = new StatementExporter(outDir, log);
    var totals = StatementExporter.GroupByCategory(transactions);
    string header = $"Ведомость учёта питания за {new DateTime(year, month, 1).ToString("MMMM yyyy", ru)}; " +
                    $"сформирована {DateTime.Now:dd.MM.yyyy HH:mm}; " +
                    (loaded.FromCache ? $"ПО ДАННЫМ КЭША от {loaded.LoadedAt:dd.MM.yyyy HH:mm}" : $"источник {source}");

    exporter.WriteTransactions($"meal_transactions_{period}.csv", transactions);
    exporter.WriteCategoryStatement($"meal_statement_{period}.csv", totals, header);
    exporter.WriteStudentStatement($"meal_by_student_{period}.csv", transactions);
    string sums = exporter.WriteChecksums($"meal_statement_{period}.sha256");
    log.Ok($"Контрольные суммы SHA-256: {Path.GetFileName(sums)}");

    // ---- итог в консоль ----------------------------------------------------
    log.Raw("");
    log.Raw($"{"Категория",-18}{"Транз.",7}{"Порций",8}{"Сумма",12}{"Дотация",11}{"К оплате",12}{"Ср. чек",10}", ConsoleColor.White);
    log.Raw(new string('─', 78), ConsoleColor.DarkGray);
    foreach (var c in totals)
    {
        log.Raw($"{c.Category,-18}{c.Transactions,7}{c.Portions,8}{c.Amount.ToString("N2", ru),12}{c.Subsidy.ToString("N2", ru),11}{c.ToPay.ToString("N2", ru),12}{c.AverageCheck.ToString("N2", ru),10}");
    }

    log.Raw(new string('─', 78), ConsoleColor.DarkGray);
    log.Raw($"{"ИТОГО",-18}{totals.Sum(c => c.Transactions),7}{totals.Sum(c => c.Portions),8}{totals.Sum(c => c.Amount).ToString("N2", ru),12}" +
            $"{totals.Sum(c => c.Subsidy).ToString("N2", ru),11}{totals.Sum(c => c.ToPay).ToString("N2", ru),12}", ConsoleColor.White);
    log.Raw("");
    log.Info($"Папка ведомостей: {outDir}");

    if (loaded.FromCache)
    {
        log.Warn("Ведомость сформирована по кэшу — после восстановления связи выгрузите повторно");
        return 2;
    }

    return 0;
}
catch (InvalidOperationException ex)
{
    log.Error(ex.Message);
    return 1;
}
catch (JsonException ex)
{
    log.Error($"Файл выгрузки повреждён: {ex.Message}");
    return 1;
}
catch (UnauthorizedAccessException ex)
{
    log.Error($"Нет прав на запись в папку ведомостей: {ex.Message}");
    return 1;
}
catch (OperationCanceledException)
{
    log.Error("Выгрузка прервана оператором");
    return 1;
}

static Dictionary<string, string> ParseArgs(string[] args)
{
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i + 1 < args.Length; i += 2)
    {
        map[args[i].TrimStart('-')] = args[i + 1];
    }

    return map;
}

static bool TryParseMonth(string value, out int year, out int month)
{
    year = month = 0;
    if (DateTime.TryParseExact(value + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
    {
        (year, month) = (d.Year, d.Month);
        return true;
    }

    return false;
}

/// <summary>Настройки из mealstatement.json.</summary>
internal sealed class Settings
{
    public string SourceUrl { get; set; } = "https://jsonplaceholder.typicode.com/posts";
    public string OutputDir { get; set; } = "statements";
    public int TimeoutSeconds { get; set; } = 10;
    public int Retries { get; set; } = 3;

    public static Settings Load(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            }) ?? new Settings()
            : new Settings();
}
