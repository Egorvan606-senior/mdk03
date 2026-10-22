using System.Text;

namespace Campus.HealthCheck.Infrastructure;

/// <summary>
/// Простой журнал: пишет одновременно в консоль (с цветом) и в файл
/// logs/healthcheck_ГГГГММДД.log. Файл открывается на дозапись с FileShare.Read,
/// чтобы администратор мог смотреть журнал во время работы утилиты.
/// </summary>
public sealed class FileLogger : IDisposable
{
    private readonly StreamWriter? _file;
    private readonly object _sync = new();

    public string? FilePath { get; }

    public FileLogger(string logsDir)
    {
        try
        {
            Directory.CreateDirectory(logsDir);
            FilePath = Path.Combine(logsDir, $"healthcheck_{DateTime.Now:yyyyMMdd}.log");
            var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            _file = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Нет прав на запись журнала — продолжаем работу только с консолью
            WriteConsole(ConsoleColor.Yellow, $"[WARN] Журнал в файл недоступен: {ex.Message}");
        }
    }

    public void Info(string message) => Write("INFO", ConsoleColor.Gray, message);
    public void Ok(string message) => Write("OK", ConsoleColor.Green, message);
    public void Warn(string message) => Write("WARN", ConsoleColor.Yellow, message);
    public void Error(string message) => Write("FAIL", ConsoleColor.Red, message);
    public void Step(string message) => Write("STEP", ConsoleColor.Cyan, message);

    /// <summary>Строка только для консоли (таблицы, заголовки) — в файл идёт как есть.</summary>
    public void Raw(string text, ConsoleColor color = ConsoleColor.Gray)
    {
        lock (_sync)
        {
            WriteConsole(color, text);
            _file?.WriteLine(text);
        }
    }

    private void Write(string level, ConsoleColor color, string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} [{level,-4}] {message}";
        lock (_sync)
        {
            WriteConsole(color, line);
            _file?.WriteLine(line);
        }
    }

    private static void WriteConsole(ConsoleColor color, string text)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ForegroundColor = previous;
    }

    public void Dispose() => _file?.Dispose();
}
