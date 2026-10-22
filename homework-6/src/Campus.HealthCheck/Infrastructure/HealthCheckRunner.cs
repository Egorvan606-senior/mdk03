using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using Campus.HealthCheck.Checks;

namespace Campus.HealthCheck.Infrastructure;

/// <summary>
/// Выполняет цепочку проверок по порядку: CFG → версия → БД → API → считыватель → диск.
/// Для каждой проверки: замер задержки (Stopwatch), таймаут попытки, повтор при
/// сетевых сбоях с нарастающей паузой и перевод любых исключений в понятный
/// оператору текст. Исключение в одной проверке не останавливает цепочку.
/// </summary>
public sealed class HealthCheckRunner(FileLogger log)
{
    public async Task<IReadOnlyList<CheckResult>> RunAsync(
        IReadOnlyList<IHealthCheck> checks, CancellationToken ct, int numberOffset = 0, int? total = null)
    {
        var results = new List<CheckResult>();
        int number = numberOffset;
        int count = total ?? checks.Count;

        foreach (var check in checks)
        {
            number++;
            var result = await RunOneAsync(check, ct);
            results.Add(result);

            string line = $"[{number}/{count}] {result.Code,-10} {result.StatusText,-4} {result.LatencyMs,6} мс  {result.Details}";
            switch (result.Status)
            {
                case CheckStatus.Pass: log.Ok(line); break;
                case CheckStatus.Warning: log.Warn(line); break;
                default: log.Error(line); break;
            }
        }

        return results;
    }

    private async Task<CheckResult> RunOneAsync(IHealthCheck check, CancellationToken ct)
    {
        CheckStatus status = CheckStatus.Fail;
        string details = "не выполнялась";
        long latency = 0;
        int attempt = 0;

        while (attempt < Math.Max(1, check.MaxAttempts))
        {
            attempt++;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(check.Timeout);
            var sw = Stopwatch.StartNew();

            try
            {
                (status, details) = await check.ExecuteAsync(timeout.Token);
                latency = sw.ElapsedMilliseconds;
                break; // проверка отработала (успешно или с логической ошибкой) — повтор не нужен
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                latency = sw.ElapsedMilliseconds;
                details = $"таймаут {check.Timeout.TotalMilliseconds:0} мс";
            }
            catch (HttpRequestException ex)
            {
                latency = sw.ElapsedMilliseconds;
                details = $"ошибка HTTP: {Short(ex)}";
            }
            catch (SocketException ex)
            {
                latency = sw.ElapsedMilliseconds;
                details = $"сеть: {SocketText(ex)}";
            }
            catch (IOException ex)
            {
                latency = sw.ElapsedMilliseconds;
                details = $"ввод-вывод: {Short(ex)}";
            }
            catch (UnauthorizedAccessException ex)
            {
                latency = sw.ElapsedMilliseconds;
                details = $"нет прав доступа: {Short(ex)}";
                break; // повтор не поможет
            }
            catch (JsonException ex)
            {
                latency = sw.ElapsedMilliseconds;
                details = $"некорректный JSON: {Short(ex)}";
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                latency = sw.ElapsedMilliseconds;
                details = $"{ex.GetType().Name}: {Short(ex)}";
                break;
            }

            status = CheckStatus.Fail;
            if (attempt < check.MaxAttempts)
            {
                int pauseMs = 300 * attempt; // 300, 600, 900 мс — не «долбим» упавший сервис
                log.Warn($"       {check.Code}: попытка {attempt}/{check.MaxAttempts} неудачна ({details}), повтор через {pauseMs} мс");
                await Task.Delay(pauseMs, ct);
            }
        }

        // Работает, но медленно — понижаем до Warning: оператору будет неудобно
        if (status == CheckStatus.Pass && check.WarnLatencyMs > 0 && latency > check.WarnLatencyMs)
        {
            status = CheckStatus.Warning;
            details += $" (задержка выше порога {check.WarnLatencyMs} мс)";
        }

        return new CheckResult(check.Code, check.Title, check.Severity, status, latency, attempt, details);
    }

    public static Readiness Evaluate(IReadOnlyList<CheckResult> results)
    {
        if (results.Any(r => r.IsBlocking))
        {
            return Readiness.NotReady;
        }

        return results.Any(r => r.Status != CheckStatus.Pass) ? Readiness.ReadyWithRemarks : Readiness.Ready;
    }

    private static string Short(Exception ex) =>
        (ex.InnerException?.Message ?? ex.Message).Split(Environment.NewLine)[0].TrimEnd('.');

    private static string SocketText(SocketException ex) => ex.SocketErrorCode switch
    {
        SocketError.ConnectionRefused => "соединение отклонено (служба не запущена или порт закрыт)",
        SocketError.HostNotFound or SocketError.NoData => "имя узла не найдено (DNS)",
        SocketError.TimedOut => "узел не отвечает",
        SocketError.NetworkUnreachable or SocketError.HostUnreachable => "узел недоступен (сеть/маршрут)",
        _ => ex.Message
    };
}
