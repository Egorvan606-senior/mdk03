using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Campus.HealthCheck.Infrastructure;

/// <summary>
/// Эмуляторы оборудования и серверов для испытательного стенда.
/// Запускаются ТОЛЬКО если включены в healthcheck.json (раздел Emulators)
/// и слушают локальный порт из конфигурации АРМ. Проверки при этом работают
/// по тем же сетевым протоколам, что и с настоящим оборудованием.
/// </summary>
public sealed class StandEmulators : IAsyncDisposable
{
    private readonly List<TcpListener> _listeners = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _loops = [];

    public List<string> Started { get; } = [];

    public void StartCardReader(int port, string model)
    {
        var listener = Listen(port);
        _loops.Add(Task.Run(() => ServeAsync(listener, async stream =>
        {
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            await using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
            string? command = await reader.ReadLineAsync(_stop.Token);
            await writer.WriteLineAsync(command == "STATUS"
                ? $"OK READER-EMU {model} FW 3.2.1 TAMPER=0"
                : "ERR UNKNOWN COMMAND");
        })));
        Started.Add($"считыватель 127.0.0.1:{port}");
    }

    public void StartCentralDb(int port)
    {
        var listener = Listen(port);
        _loops.Add(Task.Run(() => ServeAsync(listener, async stream =>
        {
            var buffer = new byte[8];
            int read = await stream.ReadAtLeastAsync(buffer, 8, throwOnEndOfStream: false, _stop.Token);
            if (read == 8)
            {
                await stream.WriteAsync("N"u8.ToArray(), _stop.Token); // «SSL не используется»
            }
        })));
        Started.Add($"PostgreSQL 127.0.0.1:{port}");
    }

    private TcpListener Listen(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _listeners.Add(listener);
        return listener;
    }

    private async Task ServeAsync(TcpListener listener, Func<NetworkStream, Task> handler)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync(_stop.Token);
                await using var stream = client.GetStream();
                await handler(stream);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                // клиент оборвал соединение — ждём следующего
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        foreach (var l in _listeners)
        {
            l.Stop();
        }

        await Task.WhenAll(_loops).ContinueWith(_ => { });
        _stop.Dispose();
    }
}
