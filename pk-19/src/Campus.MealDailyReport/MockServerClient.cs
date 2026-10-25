using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace Campus.MealDailyReport;

/// <summary>Результат выгрузки: записи источника и признак «данные взяты из кэша».</summary>
public sealed record LoadResult(IReadOnlyList<SourcePost> Posts, bool FromCache, DateTime LoadedAt, long LatencyMs);

/// <summary>
/// Клиент публичного мок-сервера (выгрузка транзакций кассы).
/// Обработка сетевых ошибок по методическим указаниям: try-catch, таймауты,
/// повторные попытки с увеличением паузы; при полной недоступности — кэш последней выгрузки,
/// чтобы бухгалтерия получила хотя бы предварительный отчёт.
/// </summary>
public sealed class MockServerClient(HttpClient http, ReportLog log, string cachePath, int retries)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<LoadResult> LoadAsync(string url, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= retries; attempt++)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                log.Info($"Запрос к мок-серверу ({attempt}/{retries}): GET {url}");
                using var response = await http.GetAsync(url, ct);
                sw.Stop();

                if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                {
                    // временная ошибка сервера — имеет смысл повторить
                    throw new HttpRequestException($"сервер ответил {(int)response.StatusCode} {response.ReasonPhrase}");
                }

                if (!response.IsSuccessStatusCode)
                {
                    // 4xx — ошибка адреса/доступа, повтор не поможет
                    throw new InvalidOperationException($"Источник вернул {(int)response.StatusCode} {response.ReasonPhrase}: проверьте SourceUrl");
                }

                string body = await response.Content.ReadAsStringAsync(ct);
                var posts = Parse(body);
                log.Ok($"Получено записей: {posts.Count}, время отклика {sw.ElapsedMilliseconds} мс");

                // кэш последней успешной выгрузки
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                await File.WriteAllTextAsync(cachePath, body, ct);
                return new LoadResult(posts, false, DateTime.Now, sw.ElapsedMilliseconds);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                log.Warn($"Таймаут {http.Timeout.TotalSeconds:0} с — сервер не ответил");
            }
            catch (HttpRequestException ex)
            {
                log.Warn($"Сетевая ошибка: {ex.Message}");
            }
            catch (JsonException ex)
            {
                log.Warn($"Ответ сервера не является корректным JSON: {ex.Message}");
            }

            if (attempt < retries)
            {
                var pause = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)); // 1 с, 2 с, 4 с ...
                log.Info($"Повтор через {pause.TotalSeconds:0} с");
                await Task.Delay(pause, ct);
            }
        }

        // Все попытки исчерпаны — работаем по кэшу
        if (File.Exists(cachePath))
        {
            var posts = Parse(await File.ReadAllTextAsync(cachePath, ct));
            var cachedAt = File.GetLastWriteTime(cachePath);
            log.Warn($"Мок-сервер недоступен. Используется кэш выгрузки от {cachedAt:dd.MM.yyyy HH:mm} ({posts.Count} записей)");
            return new LoadResult(posts, true, cachedAt, -1);
        }

        throw new InvalidOperationException("Мок-сервер недоступен, кэш выгрузки отсутствует — отчёт не может быть сформирован");
    }

    /// <summary>Разбор и валидация выгрузки: отбрасываем записи без идентификатора.</summary>
    private static List<SourcePost> Parse(string json)
    {
        var posts = JsonSerializer.Deserialize<List<SourcePost>>(json, Json)
                    ?? throw new JsonException("пустой ответ");
        return posts.Where(p => p.Id > 0 && p.UserId > 0)
                    .Select(p => p with { Title = p.Title ?? "", Body = p.Body ?? "" })
                    .ToList();
    }
}
