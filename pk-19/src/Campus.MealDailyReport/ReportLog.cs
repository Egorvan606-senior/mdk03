using System.Text;

namespace Campus.MealDailyReport;

/// <summary>
/// Журнал работы утилиты: цветной вывод в консоль оператора + запись в файл.
/// Файл открывается только на дозапись (append-only): строки прошлых запусков не изменяются,
/// что соответствует требованию «защита от изменений» для отчётности кампуса.
/// </summary>
public sealed class ReportLog : IDisposable
{
    private readonly StreamWriter _file;

    public ReportLog(string directory)
    {
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, "meal_report.log");
        _file = new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false))
        {
            AutoFlush = true
        };
    }

    public string FilePath { get; }

    public void Info(string message) => Write("INF", message, ConsoleColor.Gray);
    public void Ok(string message) => Write("OK ", message, ConsoleColor.Green);
    public void Warn(string message) => Write("WRN", message, ConsoleColor.Yellow);
    public void Error(string message) => Write("ERR", message, ConsoleColor.Red);

    /// <summary>Строка без метки уровня (таблицы итогов, заголовки).</summary>
    public void Raw(string text, ConsoleColor color = ConsoleColor.Gray)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ResetColor();
        _file.WriteLine(text);
    }

    private void Write(string level, string message, ConsoleColor color)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        Console.ForegroundColor = color;
        Console.WriteLine(line);
        Console.ResetColor();
        _file.WriteLine(line);
    }

    public void Dispose() => _file.Dispose();
}
