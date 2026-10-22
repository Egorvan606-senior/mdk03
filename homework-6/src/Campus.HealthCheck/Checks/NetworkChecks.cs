using System.Buffers.Binary;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Campus.HealthCheck.Infrastructure;

namespace Campus.HealthCheck.Checks;

/// <summary>
/// CENTRAL_DB — доступность центральной БД PostgreSQL кампуса.
/// Мало открыть TCP-порт: на нём может отвечать что угодно. Поэтому утилита
/// отправляет служебный пакет протокола PostgreSQL SSLRequest и ждёт ответ
/// 'S' или 'N' — так подтверждается, что на порту действительно СУБД.
/// </summary>
public sealed class CentralDatabaseCheck(HealthCheckContext ctx) : HealthCheckBase
{
    /// <summary>Код запроса SSLRequest из спецификации протокола PostgreSQL.</summary>
    private const int SslRequestCode = 80877103;

    public override string Code => "CENTRAL_DB";
    public override string Title => "Центральная БД PostgreSQL";
    public override int MaxAttempts => ctx.Options.NetworkRetries;
    public override int WarnLatencyMs => ctx.Options.Thresholds.CentralDbWarnMs;
    public override TimeSpan Timeout => TimeSpan.FromMilliseconds(ctx.Options.Timeouts.CentralDbMs);

    public override async Task<(CheckStatus Status, string Details)> ExecuteAsync(CancellationToken ct)
    {
        if (ctx.Arm is null)
        {
            return Fail("не выполнена: нет корректной конфигурации (CFG)");
        }

        using var client = new TcpClient();
        await client.ConnectAsync(ctx.Arm.CentralDbHost, ctx.Arm.CentralDbPort, ct);
        await using var stream = client.GetStream();

        // SSLRequest: длина сообщения (8) + код запроса, оба int32 big-endian
        var request = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(0, 4), 8);
        BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(4, 4), SslRequestCode);
        await stream.WriteAsync(request, ct);

        var answer = new byte[1];
        int read = await stream.ReadAsync(answer, ct);
        string endpoint = $"{ctx.Arm.CentralDbHost}:{ctx.Arm.CentralDbPort}";

        return read == 1 && answer[0] is (byte)'S' or (byte)'N'
            ? Pass($"{endpoint}: PostgreSQL отвечает (SSL {(answer[0] == 'S' ? "поддерживается" : "не используется")})")
            : Fail($"{endpoint}: порт открыт, но это не PostgreSQL");
    }
}

/// <summary>
/// API — исходящая связь АРМ с внешним API через шлюз кампуса.
/// Публичный сервис api.ipify.org возвращает внешний IP рабочего места:
/// он нужен администратору для белого списка межсетевого экрана и попадает в акт.
/// </summary>
public sealed class ExternalApiCheck(HealthCheckContext ctx, HttpClient http) : HealthCheckBase
{
    public override string Code => "API";
    public override string Title => "Внешний API (api.ipify.org)";
    public override int MaxAttempts => ctx.Options.NetworkRetries;
    public override int WarnLatencyMs => ctx.Options.Thresholds.ApiWarnMs;
    public override TimeSpan Timeout => TimeSpan.FromMilliseconds(ctx.Options.Timeouts.ApiMs);

    private sealed record IpifyResponse(string? Ip);

    public override async Task<(CheckStatus Status, string Details)> ExecuteAsync(CancellationToken ct)
    {
        if (ctx.Arm is null)
        {
            return Fail("не выполнена: нет корректной конфигурации (CFG)");
        }

        using var response = await http.GetAsync(ctx.Arm.ApiCheckUrl, ct);
        if (!response.IsSuccessStatusCode)
        {
            // HttpRequestException -> повтор попытки в HealthCheckRunner
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var body = await response.Content.ReadFromJsonAsync<IpifyResponse>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web), ct);

        if (string.IsNullOrWhiteSpace(body?.Ip))
        {
            return Fail("ответ 200, но в JSON нет поля ip — изменился контракт API");
        }

        ctx.ExternalIp = body.Ip;
        return Pass($"HTTP 200, внешний IP АРМ {body.Ip}");
    }
}

/// <summary>
/// READER — считыватель пропусков на турникете. Контроллер считывателя
/// подключён по Ethernet (TCP); на команду STATUS он отвечает строкой "OK …".
/// </summary>
public sealed class CardReaderCheck(HealthCheckContext ctx) : HealthCheckBase
{
    public override string Code => "READER";
    public override string Title => "Считыватель пропусков";
    public override int MaxAttempts => ctx.Options.NetworkRetries;
    public override int WarnLatencyMs => ctx.Options.Thresholds.ReaderWarnMs;
    public override TimeSpan Timeout => TimeSpan.FromMilliseconds(ctx.Options.Timeouts.ReaderMs);

    public override async Task<(CheckStatus Status, string Details)> ExecuteAsync(CancellationToken ct)
    {
        if (ctx.Arm is null)
        {
            return Fail("не выполнена: нет корректной конфигурации (CFG)");
        }

        using var client = new TcpClient();
        await client.ConnectAsync(ctx.Arm.ReaderHost, ctx.Arm.ReaderPort, ct);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };

        await writer.WriteLineAsync("STATUS".AsMemory(), ct);
        string? answer = await reader.ReadLineAsync(ct);

        if (answer is null)
        {
            throw new IOException("считыватель закрыл соединение без ответа");
        }

        return answer.StartsWith("OK", StringComparison.Ordinal)
            ? Pass($"{ctx.Arm.ReaderModel} ({ctx.Arm.ReaderHost}:{ctx.Arm.ReaderPort}): {answer}")
            : Fail($"считыватель сообщил об ошибке: {answer}");
    }
}
