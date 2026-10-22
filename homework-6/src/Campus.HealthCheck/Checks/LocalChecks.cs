using System.Text;
using System.Text.Json;
using Campus.HealthCheck.Infrastructure;

namespace Campus.HealthCheck.Checks;

/// <summary>
/// APP_VER — установленная версия клиента АРМ совпадает с ожидаемой.
/// Версия берётся из файла version.json, который пишет установщик.
/// </summary>
public sealed class VersionCheck(HealthCheckContext ctx) : HealthCheckBase
{
    public override string Code => "APP_VER";
    public override string Title => "Версия клиента АРМ";

    public override async Task<(CheckStatus Status, string Details)> ExecuteAsync(CancellationToken ct)
    {
        string path = Path.Combine(ctx.Options.InstallDir, "version.json");
        if (!File.Exists(path))
        {
            return Fail($"не найден {path} — клиент не установлен или установка повреждена");
        }

        await using var stream = File.OpenRead(path);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        string? version = ArmConfig.TryGet(doc.RootElement, "version");
        ctx.InstalledVersion = version;

        if (version is null)
        {
            return Fail("в version.json нет поля version");
        }

        return version == ctx.Options.ExpectedVersion
            ? Pass($"установлена {version}")
            : Fail($"установлена {version}, ожидается {ctx.Options.ExpectedVersion}");
    }
}

/// <summary>
/// CFG — конфигурация АРМ (appsettings.json) читается и содержит все обязательные ключи.
/// Результат разбора сохраняется в контекст: по нему работают сетевые проверки.
/// </summary>
public sealed class ConfigCheck(HealthCheckContext ctx, string? apiUrlOverride) : HealthCheckBase
{
    public override string Code => "CFG";
    public override string Title => "Конфигурация appsettings.json";

    public override async Task<(CheckStatus Status, string Details)> ExecuteAsync(CancellationToken ct)
    {
        string path = Path.Combine(ctx.Options.InstallDir, ctx.Options.ArmConfigFile);
        if (!File.Exists(path))
        {
            return Fail($"файл не найден: {path}");
        }

        await using var stream = File.OpenRead(path);
        JsonDocument doc;
        try
        {
            doc = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            }, ct);
        }
        catch (JsonException ex)
        {
            // типичная причина — ручная правка файла после обновления
            return Fail($"файл повреждён: строка {ex.LineNumber + 1}, {ex.Message.Split('.')[0]}");
        }

        using (doc)
        {
            var missing = ctx.Options.RequiredConfigKeys
                .Where(k => ArmConfig.TryGet(doc.RootElement, k) is null)
                .ToList();
            if (missing.Count > 0)
            {
                return Fail("нет обязательных ключей: " + string.Join(", ", missing));
            }

            var arm = ArmConfig.Parse(doc.RootElement, ctx.Options.InstallDir);
            if (!string.IsNullOrWhiteSpace(apiUrlOverride))
            {
                arm = arm with { ApiCheckUrl = apiUrlOverride };
            }

            ctx.Arm = arm;
            return Pass($"{ctx.Options.RequiredConfigKeys.Length} обязательных ключей, АРМ {arm.ArmId}");
        }
    }
}

/// <summary>
/// LOCAL_DB — локальная база SQLite (кэш пропусков на случай потери связи):
/// файл существует, это действительно SQLite, структура страниц не нарушена,
/// файл не заблокирован «зависшим» процессом старой версии.
/// Проверка выполняется без драйвера SQLite — по заголовку формата файла.
/// </summary>
public sealed class LocalDatabaseCheck(HealthCheckContext ctx) : HealthCheckBase
{
    private static readonly byte[] SqliteMagic = Encoding.ASCII.GetBytes("SQLite format 3\0");

    public override string Code => "LOCAL_DB";
    public override string Title => "Локальная БД SQLite (кэш пропусков)";

    public override async Task<(CheckStatus Status, string Details)> ExecuteAsync(CancellationToken ct)
    {
        if (ctx.Arm is null)
        {
            return Fail("не выполнена: нет корректной конфигурации (CFG)");
        }

        string path = ctx.Arm.LocalDbPath;
        if (!File.Exists(path))
        {
            return Fail($"файл БД не найден: {path}");
        }

        // FileShare.Read: если старый процесс держит файл на запись — получим IOException
        byte[] header = new byte[100];
        long length;
        await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            length = fs.Length;
            int read = await fs.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
            if (read < header.Length)
            {
                return Fail($"файл слишком мал ({length} байт) — БД повреждена");
            }
        }

        if (!header.AsSpan(0, 16).SequenceEqual(SqliteMagic))
        {
            return Fail("неверная сигнатура файла — это не база SQLite");
        }

        // Размер страницы: байты 16–17 (big-endian), значение 1 означает 65536
        int pageSize = (header[16] << 8) | header[17];
        if (pageSize == 1)
        {
            pageSize = 65536;
        }

        if (pageSize < 512 || (pageSize & (pageSize - 1)) != 0)
        {
            return Fail($"некорректный размер страницы {pageSize}");
        }

        // Число страниц в заголовке (байты 28–31) должно совпадать с размером файла
        long pagesInHeader = (header[28] << 24) | (header[29] << 16) | (header[30] << 8) | header[31];
        long pagesInFile = length / pageSize;
        if (length % pageSize != 0 || (pagesInHeader != 0 && pagesInHeader != pagesInFile))
        {
            return Fail($"размер файла {length} байт не соответствует заголовку ({pagesInHeader} стр. × {pageSize})");
        }

        return Pass($"{Path.GetFileName(path)}: {pagesInFile} стр. × {pageSize} Б, файл не заблокирован");
    }
}

/// <summary>DISK — свободное место на диске установки (журналы, кэш, следующее обновление).</summary>
public sealed class DiskSpaceCheck(HealthCheckContext ctx) : HealthCheckBase
{
    public override string Code => "DISK";
    public override string Title => "Свободное место на диске";
    public override CheckSeverity Severity => CheckSeverity.NonCritical;

    public override Task<(CheckStatus Status, string Details)> ExecuteAsync(CancellationToken ct)
    {
        string root = Path.GetPathRoot(ctx.Options.InstallDir) ?? ctx.Options.InstallDir;
        var drive = new DriveInfo(root);
        long freeMb = drive.AvailableFreeSpace / 1024 / 1024;
        string details = $"диск {drive.Name}: свободно {freeMb:N0} МБ, минимум {ctx.Options.Thresholds.MinFreeDiskMb:N0} МБ";

        return Task.FromResult(freeMb >= ctx.Options.Thresholds.MinFreeDiskMb ? Pass(details) : Warn(details));
    }
}
