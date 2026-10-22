using System.Text;

namespace Campus.MealStatement;

/// <summary>Журнал выгрузки: консоль (цветом) + файл logs/meal_export_ГГГГММДД.log.</summary>
public sealed class Logger : IDisposable
{
    private readonly StreamWriter? _file;

    public string? FilePath { get; }

    public Logger(string logsDir)
    {
        try
        {
            Directory.CreateDirectory(logsDir);
            FilePath = Path.Combine(logsDir, $"meal_export_{DateTime.Now:yyyyMMdd}.log");
            _file = new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[WARN] журнал в файл недоступен: {ex.Message}");
        }
    }

    public void Info(string m) => Write("INFO", ConsoleColor.Gray, m);
    public void Ok(string m) => Write("OK", ConsoleColor.Green, m);
    public void Warn(string m) => Write("WARN", ConsoleColor.Yellow, m);
    public void Error(string m) => Write("FAIL", ConsoleColor.Red, m);

    public void Raw(string text, ConsoleColor color = ConsoleColor.Gray)
    {
        var c = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ForegroundColor = c;
        _file?.WriteLine(text);
    }

    private void Write(string level, ConsoleColor color, string message) =>
        Raw($"{DateTime.Now:HH:mm:ss.fff} [{level,-4}] {message}", color);

    public void Dispose() => _file?.Dispose();
}
