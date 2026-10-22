// ==========================================================================
// campus-healthcheck — тестер Health Check после обновления АРМ оператора
// ИС «Умный кампус». Практическая работа № 6 (CALS-технологии), вариант 10.
//
// Команды:
//   check                       — цепочка проверок и акт готовности АРМ
//   update  --package <папка>   — имитация установки обновления: проверка пакета,
//                                 бэкап, установка, контроль целостности данных,
//                                 Health Check, при сбое — автоматический откат
//   manifest --package <папка> --version <x.y.z>
//                               — подготовка manifest.json (SHA-256 файлов пакета)
// Общие параметры:
//   --config <файл>    настройки утилиты (по умолчанию healthcheck.json рядом с exe)
//   --api-url <url>    переопределить адрес проверки внешнего API
//   --emulators none|reader|db|all  режим эмуляторов стенда (иначе — из настроек)
//
// Коды завершения (используются скриптом развертывания / планировщиком):
//   0 — АРМ готово, 2 — готово с замечаниями, 1 — не готово,
//   3 — не готово, выполнен откат, 4 — ошибка запуска (параметры, файлы)
// ==========================================================================

using System.Text;
using System.Text.Json;
using Campus.HealthCheck.Checks;
using Campus.HealthCheck.Infrastructure;
using Campus.HealthCheck.Reports;
using Campus.HealthCheck.Update;

Console.OutputEncoding = Encoding.UTF8;

var cli = CommandLine.Parse(args);
if (cli.Command is "help" or "-h" or "--help")
{
    PrintHelp();
    return 0;
}

ToolOptions options;
try
{
    options = ToolOptions.Load(cli.Get("config") ?? Path.Combine(AppContext.BaseDirectory, "healthcheck.json"));
}
catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Ошибка настроек: {ex.Message}");
    return 4;
}

// --emulators none|reader|db|all — переопределить режим стенда без правки healthcheck.json
switch (cli.Get("emulators")?.ToLowerInvariant())
{
    case "none": options.Emulators = new EmulatorOptions(); break;
    case "reader": options.Emulators = new EmulatorOptions { CardReader = true }; break;
    case "db": options.Emulators = new EmulatorOptions { CentralDb = true }; break;
    case "all": options.Emulators = new EmulatorOptions { CardReader = true, CentralDb = true }; break;
}

if (cli.Command == "manifest")
{
    return BuildManifest(cli);
}

using var log = new FileLogger(options.LogsDir);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); }; // Ctrl+C — корректная остановка

PrintBanner(cli.Command == "update" ? "Обновление АРМ + Health Check" : "Health Check после обновления");

try
{
    return cli.Command switch
    {
        "check" => await RunCheckAsync(options, cli, log, cts.Token),
        "update" => await RunUpdateAsync(options, cli, log, cts.Token),
        _ => Usage($"неизвестная команда «{cli.Command}»")
    };
}
catch (OperationCanceledException)
{
    log.Error("Работа прервана оператором (Ctrl+C)");
    return 1;
}

// --------------------------------------------------------------------------
// check: только проверки и акт
// --------------------------------------------------------------------------
static async Task<int> RunCheckAsync(ToolOptions options, CommandLine cli, FileLogger log, CancellationToken ct)
{
    var report = new ReadinessReport { Operation = "Проверка АРМ после обновления", ExpectedVersion = options.ExpectedVersion };
    await RunHealthCheckAsync(options, cli, log, report, ct);
    report.VersionAfter = report.VersionBefore;
    return Finish(report, options, log);
}

// --------------------------------------------------------------------------
// update: регламент установки обновления с контролем и откатом
// --------------------------------------------------------------------------
static async Task<int> RunUpdateAsync(ToolOptions options, CommandLine cli, FileLogger log, CancellationToken ct)
{
    string? packageDir = cli.Get("package");
    if (packageDir is null || !Directory.Exists(packageDir))
    {
        return Usage("укажите существующую папку пакета: --package <папка>");
    }

    var installer = new UpdateInstaller(options, log);
    var report = new ReadinessReport { Operation = "Установка обновления", ExpectedVersion = options.ExpectedVersion };
    string currentVersion = ReadVersion(options.InstallDir) ?? "unknown";
    report.VersionBefore = currentVersion;
    report.ArmId = ReadArmId(options) ?? report.ArmId;

    // 1. Пакет
    log.Step($"1/6 Проверка пакета обновления: {Path.GetFullPath(packageDir)}");
    UpdateManifest manifest;
    try
    {
        manifest = installer.VerifyPackage(packageDir);
    }
    catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException)
    {
        log.Error($"Пакет отклонён: {ex.Message}");
        report.UpdateSteps.Add($"Пакет отклонён до установки: {ex.Message}. Изменения на АРМ не вносились.");
        report.VersionAfter = currentVersion;
        report.Verdict = Readiness.NotReady;
        return Finish(report, options, log, skipChecks: true);
    }

    log.Ok($"       пакет {manifest.Version} ({manifest.Channel}): {manifest.Files.Count} файлов, SHA-256 совпадают");
    report.UpdateSteps.Add($"Пакет {manifest.Version} ({manifest.Channel}) проверен: {manifest.Files.Count} файлов, контрольные суммы SHA-256 совпадают с манифестом.");

    // 2. Резервная копия
    log.Step($"2/6 Резервное копирование АРМ (версия {currentVersion})");
    string backup = installer.Backup(currentVersion, report.ArmId);
    report.BackupPath = backup;
    log.Ok($"       {backup} ({new FileInfo(backup).Length / 1024.0:F1} КБ)");
    report.UpdateSteps.Add($"Создана резервная копия `{Path.GetFileName(backup)}` (кэш, appsettings.json, локальная БД SQLite).");

    // 3. Снимок данных
    log.Step("3/6 Снимок контрольных сумм локальных данных");
    var before = installer.SnapshotProtectedData();
    log.Ok($"       защищённых файлов данных: {before.Count}");

    // 4. Установка
    log.Step($"4/6 Установка версии {manifest.Version}");
    var (copied, newKeys) = installer.Install(packageDir, manifest);
    log.Ok($"       скопировано файлов: {copied}, добавлено ключей конфигурации: {newKeys}");
    report.UpdateSteps.Add($"Установлены файлы версии {manifest.Version}: {copied} шт.; в appsettings.json добавлено новых ключей: {newKeys} (существующие значения сохранены).");

    // 5. Целостность данных
    log.Step("5/6 Контроль целостности локальных данных");
    var after = installer.SnapshotProtectedData();
    var changed = before.Where(kv => !after.TryGetValue(kv.Key, out var h) || h != kv.Value).Select(kv => kv.Key).ToList();
    bool dataIntact = changed.Count == 0;
    if (dataIntact)
    {
        log.Ok($"       файлов данных проверено: {before.Count}, изменений нет (SHA-256 до = после)");
        report.UpdateSteps.Add($"Целостность данных подтверждена: {before.Count} файлов в data/ не изменились.");
    }
    else
    {
        log.Error("       изменены данные: " + string.Join(", ", changed));
        report.UpdateSteps.Add("Нарушена целостность данных: " + string.Join(", ", changed));
    }

    // 6. Health Check
    log.Step("6/6 Health Check после установки");
    await RunHealthCheckAsync(options, cli, log, report, ct);
    report.VersionAfter = ReadVersion(options.InstallDir);

    if (report.Verdict == Readiness.NotReady || !dataIntact)
    {
        // Регламент: критичный сбой после обновления -> немедленный откат
        log.Error($"Критичный сбой после обновления — откат на {currentVersion}");
        installer.Rollback(backup);
        string? restored = ReadVersion(options.InstallDir);
        var afterRollback = installer.SnapshotProtectedData();
        bool same = before.All(kv => afterRollback.TryGetValue(kv.Key, out var h) && h == kv.Value);
        log.Warn($"       откат выполнен: версия {restored}, данные {(same ? "совпадают с исходными" : "ОТЛИЧАЮТСЯ")}");
        report.UpdateSteps.Add($"Автоматический откат из `{Path.GetFileName(backup)}`: восстановлена версия {restored}, данные совпадают с исходными: {(same ? "да" : "нет")}.");
        report.RolledBack = true;
        report.Verdict = Readiness.NotReady;
        report.VersionAfter = restored;
    }

    int code = Finish(report, options, log);
    return report.RolledBack ? 3 : code;
}

// --------------------------------------------------------------------------
// Общая часть: цепочка проверок
// --------------------------------------------------------------------------
static async Task RunHealthCheckAsync(ToolOptions options, CommandLine cli, FileLogger log, ReadinessReport report, CancellationToken ct)
{
    var ctx = new HealthCheckContext(options);
    using var http = new HttpClient();
    http.DefaultRequestHeaders.UserAgent.ParseAdd("campus-healthcheck/1.0");

    await using var emulators = new StandEmulators();

    // CFG выполняется первым: из него берутся адреса для остальных проверок
    var config = new ConfigCheck(ctx, cli.Get("api-url"));
    var checks = new List<IHealthCheck>
    {
        config,
        new VersionCheck(ctx),
        new LocalDatabaseCheck(ctx),
        new CentralDatabaseCheck(ctx),
        new ExternalApiCheck(ctx, http),
        new CardReaderCheck(ctx),
        new DiskSpaceCheck(ctx)
    };

    // Эмуляторы нужно поднять ДО сетевых проверок, но после чтения конфигурации
    var runner = new HealthCheckRunner(log);
    var results = new List<CheckResult>(await runner.RunAsync([config], ct, 0, checks.Count));
    if (ctx.Arm is not null && options.Emulators.Any)
    {
        try
        {
            if (options.Emulators.CardReader)
            {
                emulators.StartCardReader(ctx.Arm.ReaderPort, ctx.Arm.ReaderModel);
            }
        }
        catch (System.Net.Sockets.SocketException)
        {
            log.Info($"       порт {ctx.Arm.ReaderPort} занят — проверяется реальный считыватель");
        }

        try
        {
            if (options.Emulators.CentralDb)
            {
                emulators.StartCentralDb(ctx.Arm.CentralDbPort);
            }
        }
        catch (System.Net.Sockets.SocketException)
        {
            log.Info($"       порт {ctx.Arm.CentralDbPort} занят — проверяется реальный PostgreSQL");
        }

        if (emulators.Started.Count > 0)
        {
            log.Warn("       режим стенда, эмуляторы: " + string.Join(", ", emulators.Started));
        }
    }

    results.AddRange(await runner.RunAsync(checks.Skip(1).ToList(), ct, 1, checks.Count));

    report.ArmId = ctx.Arm?.ArmId ?? report.ArmId;
    report.Location = ctx.Arm?.Location ?? report.Location;
    report.VersionBefore ??= ctx.InstalledVersion;
    report.ExternalIp = ctx.ExternalIp;
    report.Emulators.AddRange(emulators.Started);
    report.Results = results;
    report.Verdict = HealthCheckRunner.Evaluate(results);
}

static int Finish(ReadinessReport report, ToolOptions options, FileLogger log, bool skipChecks = false)
{
    report.FinishedAt = DateTime.Now;
    report.LogPath = log.FilePath;
    if (!skipChecks)
    {
        ReadinessReportWriter.PrintSummary(report, log);
    }
    else
    {
        log.Raw($"ИТОГ: {report.VerdictText}", ConsoleColor.Red);
    }

    var (md, json) = ReadinessReportWriter.Save(report, options.ReportsDir);
    log.Info($"Акт готовности: {md}");
    log.Info($"Отчёт для мониторинга: {json}");

    return report.Verdict switch
    {
        Readiness.Ready => 0,
        Readiness.ReadyWithRemarks => 2,
        _ => 1
    };
}

static int BuildManifest(CommandLine cli)
{
    string? dir = cli.Get("package");
    string? version = cli.Get("version");
    if (dir is null || version is null || !Directory.Exists(Path.Combine(dir, "files")))
    {
        return Usage("manifest --package <папка с подпапкой files> --version <x.y.z> [--channel Stable|Beta]");
    }

    string filesDir = Path.Combine(dir, "files");
    var files = Directory.EnumerateFiles(filesDir, "*", SearchOption.AllDirectories)
        .OrderBy(f => f, StringComparer.Ordinal)
        .Select(f => new ManifestFile(Path.GetRelativePath(filesDir, f).Replace('\\', '/'), UpdateInstaller.Sha256(f)))
        .ToList();

    var manifest = new UpdateManifest(version, cli.Get("channel") ?? "Stable", DateTime.Now, files);
    File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(manifest, ToolOptions.JsonOptions));
    Console.WriteLine($"manifest.json: версия {version}, файлов {files.Count}");
    return 0;
}

static string? ReadVersion(string installDir)
{
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(installDir, "version.json")));
        return ArmConfig.TryGet(doc.RootElement, "version");
    }
    catch (Exception ex) when (ex is IOException or JsonException)
    {
        return null;
    }
}

static string? ReadArmId(ToolOptions options)
{
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(options.InstallDir, options.ArmConfigFile)));
        return ArmConfig.TryGet(doc.RootElement, "Arm:Id");
    }
    catch (Exception ex) when (ex is IOException or JsonException)
    {
        return null;
    }
}

static void PrintBanner(string title)
{
    var c = Console.ForegroundColor;
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("==========================================================");
    Console.WriteLine($"  ИС «Умный кампус» · campus-healthcheck 1.0");
    Console.WriteLine($"  {title}");
    Console.WriteLine($"  {DateTime.Now:dd.MM.yyyy HH:mm:ss} · {Environment.MachineName}");
    Console.WriteLine("==========================================================");
    Console.ForegroundColor = c;
}

static int Usage(string error)
{
    Console.Error.WriteLine($"Ошибка: {error}");
    PrintHelp();
    return 4;
}

static void PrintHelp() => Console.WriteLine("""
    Использование:
      campus-healthcheck check  [--config healthcheck.json] [--api-url <url>] [--emulators none|reader|db|all]
      campus-healthcheck update --package <папка пакета> [--config healthcheck.json]
      campus-healthcheck manifest --package <папка> --version <x.y.z> [--channel Stable]
    Коды завершения: 0 — готово, 2 — с замечаниями, 1 — не готово, 3 — откат, 4 — ошибка запуска.
    """);

/// <summary>Разбор аргументов: первая позиция — команда, далее пары --ключ значение.</summary>
internal sealed record CommandLine(string Command, Dictionary<string, string> Values)
{
    public string? Get(string key) => Values.TryGetValue(key, out var v) ? v : null;

    public static CommandLine Parse(string[] args)
    {
        string command = args.Length > 0 && !args[0].StartsWith("--") ? args[0].ToLowerInvariant() : "check";
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--") && i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            {
                values[args[i][2..]] = args[i + 1];
                i++;
            }
        }

        return new CommandLine(command, values);
    }
}
