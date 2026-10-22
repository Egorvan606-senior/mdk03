using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Campus.HealthCheck.Infrastructure;

namespace Campus.HealthCheck.Update;

/// <summary>Манифест пакета обновления (manifest.json в корне пакета).</summary>
public sealed record UpdateManifest(string Version, string Channel, DateTime BuiltAt, List<ManifestFile> Files);

public sealed record ManifestFile(string Path, string Sha256);

/// <summary>
/// Имитация установки обновления на АРМ по регламенту Patch Management:
///   1) проверка целостности пакета (SHA-256 каждого файла по манифесту);
///   2) резервная копия каталога установки в ZIP (кэш, appsettings.json, SQLite);
///   3) снимок контрольных сумм защищённых данных (data/);
///   4) копирование файлов новой версии и перенос новых ключей конфигурации
///      без затирания значений, настроенных на АРМ;
///   5) проверка, что локальные данные не изменились (целостность);
///   6) при сбое — откат из резервной копии.
/// Health Check после установки запускает вызывающий код (Program).
/// </summary>
public sealed class UpdateInstaller(ToolOptions options, FileLogger log)
{
    private const string TemplateName = "appsettings.template.json";

    /// <summary>Шаг 1. Проверка пакета. Возвращает манифест или бросает InvalidDataException.</summary>
    public UpdateManifest VerifyPackage(string packageDir)
    {
        string manifestPath = Path.Combine(packageDir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidDataException($"в пакете нет manifest.json ({packageDir})");
        }

        var manifest = JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(manifestPath), ToolOptions.JsonOptions)
                       ?? throw new InvalidDataException("manifest.json пуст");

        foreach (var file in manifest.Files)
        {
            string path = Path.Combine(packageDir, "files", file.Path);
            if (!File.Exists(path))
            {
                throw new InvalidDataException($"в пакете отсутствует файл {file.Path}");
            }

            string actual = Sha256(path);
            if (!actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                // пакет повреждён при скачивании или подменён — ставить нельзя
                throw new InvalidDataException($"контрольная сумма {file.Path} не совпадает: ожидалось {file.Sha256[..12]}…, получено {actual[..12]}…");
            }
        }

        return manifest;
    }

    /// <summary>Шаг 2. Полная резервная копия каталога установки.</summary>
    public string Backup(string currentVersion, string armId)
    {
        Directory.CreateDirectory(options.BackupsDir);
        string zip = Path.Combine(options.BackupsDir, $"backup_{armId}_{currentVersion}_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
        ZipFile.CreateFromDirectory(options.InstallDir, zip, CompressionLevel.Optimal, includeBaseDirectory: false);

        // проверяем, что архив читается, — бэкап, который нельзя открыть, хуже, чем никакого
        using (var archive = ZipFile.OpenRead(zip))
        {
            if (archive.Entries.Count == 0)
            {
                throw new IOException("резервная копия пуста");
            }
        }

        return zip;
    }

    /// <summary>Шаг 3/5. Контрольные суммы защищённых данных АРМ.</summary>
    public Dictionary<string, string> SnapshotProtectedData()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string entry in options.ProtectedData)
        {
            string full = Path.Combine(options.InstallDir, entry);
            var files = Directory.Exists(full)
                ? Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
                : File.Exists(full) ? [full] : [];

            foreach (string f in files)
            {
                map[Path.GetRelativePath(options.InstallDir, f)] = Sha256(f);
            }
        }

        return map;
    }

    /// <summary>Шаг 4. Установка файлов новой версии.</summary>
    public (int Copied, int NewConfigKeys) Install(string packageDir, UpdateManifest manifest)
    {
        int copied = 0;
        int newKeys = 0;
        foreach (var file in manifest.Files)
        {
            string source = Path.Combine(packageDir, "files", file.Path);

            if (Path.GetFileName(file.Path).Equals(TemplateName, StringComparison.OrdinalIgnoreCase))
            {
                newKeys = MergeConfig(source);
                continue;
            }

            if (options.ProtectedData.Any(p => file.Path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                log.Warn($"       файл {file.Path} относится к данным АРМ и не перезаписывается");
                continue;
            }

            string target = Path.Combine(options.InstallDir, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
            copied++;
        }

        // version.json пишется последним: если установка оборвётся раньше,
        // Health Check увидит старую версию и не даст допустить АРМ к работе
        File.WriteAllText(Path.Combine(options.InstallDir, "version.json"), JsonSerializer.Serialize(new
        {
            version = manifest.Version,
            channel = manifest.Channel,
            installedAt = DateTime.Now
        }, ToolOptions.JsonOptions));

        return (copied, newKeys);
    }

    /// <summary>
    /// Перенос НОВЫХ ключей из шаблона конфигурации новой версии в appsettings.json АРМ.
    /// Существующие значения (адреса, порты, секреты) не изменяются.
    /// </summary>
    private int MergeConfig(string templatePath)
    {
        string configPath = Path.Combine(options.InstallDir, options.ArmConfigFile);
        var current = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
        var template = JsonNode.Parse(File.ReadAllText(templatePath))!.AsObject();

        int added = AddMissing(current, template);
        File.WriteAllText(configPath, current.ToJsonString(ToolOptions.JsonOptions));
        return added;

        static int AddMissing(JsonObject target, JsonObject source)
        {
            int count = 0;
            foreach (var (key, value) in source)
            {
                if (!target.ContainsKey(key))
                {
                    target[key] = value?.DeepClone();
                    count++;
                }
                else if (target[key] is JsonObject t && value is JsonObject s)
                {
                    count += AddMissing(t, s);
                }
            }

            return count;
        }
    }

    /// <summary>Шаг 6. Откат: каталог установки полностью восстанавливается из ZIP.</summary>
    public void Rollback(string backupZip)
    {
        foreach (string dir in Directory.EnumerateDirectories(options.InstallDir))
        {
            Directory.Delete(dir, recursive: true);
        }

        foreach (string file in Directory.EnumerateFiles(options.InstallDir))
        {
            File.Delete(file);
        }

        ZipFile.ExtractToDirectory(backupZip, options.InstallDir, overwriteFiles: true);
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
