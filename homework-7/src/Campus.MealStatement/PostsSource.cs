using System.Net.Http.Json;
using System.Text.Json;

namespace Campus.MealStatement;

/// <summary>Результат загрузки: данные и откуда они взяты.</summary>
public sealed record LoadResult(IReadOnlyList<SourcePost> Posts, bool FromCache, DateTime LoadedAt, int Rejected);

/// <summary>
/// Загрузка транзакций из внешнего источника.
/// Отказоустойчивость: таймаут, до 3 попыток с паузой, проверка контракта JSON,
/// а при недоступности API — работа по кэшу последней успешной выгрузки
/// (ведомость при этом помечается «сформирована по кэшу»).
/// </summary>
public sealed class PostsSource(HttpClient http, Logger log, string cacheFile, int retries = 3)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<LoadResult> LoadAsync(string url, CancellationToken ct)
    {
        Exception? last = null;
        for (int attempt = 1; attempt <= retries; attempt++)
        {
            try
            {
                log.Info($"Запрос {url} (попытка {attempt}/{retries})");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var response = await http.GetAsync(url, ct);
                response.EnsureSuccessStatusCode();
                var raw = await response.Content.ReadFromJsonAsync<List<JsonElement>>(Json, ct)
                          ?? throw new JsonException("пустой ответ");
                log.Ok($"Ответ HTTP {(int)response.StatusCode} за {sw.ElapsedMilliseconds} мс, записей: {raw.Count}");

                var (posts, rejected) = Validate(raw);
                await SaveCacheAsync(posts, ct);
                return new LoadResult(posts, false, DateTime.Now, rejected);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       && !ct.IsCancellationRequested)
            {
                last = ex;
                string reason = ex is TaskCanceledException ? $"таймаут {http.Timeout.TotalSeconds:0} с" : ex.Message.Split('\n')[0];
                log.Warn($"Попытка {attempt} неудачна: {reason}");
                if (attempt < retries)
                {
                    await Task.Delay(500 * attempt, ct);
                }
            }
        }

        // API недоступен — используем кэш, если он есть
        if (File.Exists(cacheFile))
        {
            log.Warn($"API недоступен ({last?.GetType().Name}). Используется кэш: {cacheFile}");
            await using var stream = File.OpenRead(cacheFile);
            var cached = await JsonSerializer.DeserializeAsync<List<SourcePost>>(stream, Json, ct) ?? [];
            return new LoadResult(cached, true, File.GetLastWriteTime(cacheFile), 0);
        }

        throw new InvalidOperationException("Источник данных недоступен, а кэша предыдущей выгрузки нет.", last);
    }

    /// <summary>
    /// Проверка контракта: у каждой записи должны быть userId, id, title, body.
    /// Некорректные записи не роняют выгрузку — они пропускаются и считаются.
    /// </summary>
    private (List<SourcePost> Posts, int Rejected) Validate(List<JsonElement> raw)
    {
        var result = new List<SourcePost>(raw.Count);
        int rejected = 0;
        foreach (var e in raw)
        {
            if (e.ValueKind == JsonValueKind.Object
                && e.TryGetProperty("userId", out var u) && u.TryGetInt32(out int userId) && userId > 0
                && e.TryGetProperty("id", out var i) && i.TryGetInt32(out int id) && id > 0
                && e.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                && e.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String)
            {
                result.Add(new SourcePost(userId, id, t.GetString()!, b.GetString()!));
            }
            else
            {
                rejected++;
                log.Warn($"Пропущена запись с нарушением контракта: {e.GetRawText()[..Math.Min(60, e.GetRawText().Length)]}");
            }
        }

        return (result, rejected);
    }

    private async Task SaveCacheAsync(List<SourcePost> posts, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cacheFile))!);
            await using var stream = File.Create(cacheFile);
            await JsonSerializer.SerializeAsync(stream, posts, Json, ct);
        }
        catch (IOException ex)
        {
            log.Warn($"Не удалось обновить кэш: {ex.Message}"); // не критично для выгрузки
        }
    }
}
