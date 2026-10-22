using System.Globalization;
using System.Text.RegularExpressions;

namespace SmartCampus.Sport.Domain;

/// <summary>
/// Слот бронирования спорткомплекса — полуоткрытый интервал времени [Start, End).
/// Заменяет строковое поле TimeSlot ("18:00-19:30") по задаче CAMPUS-110.
/// В PostgreSQL хранится в колонке time_range типа sport.timerange.
/// </summary>
public readonly partial record struct TimeRange
{
    /// <summary>Конец «до полуночи» — в .NET TimeOnly не умеет 24:00.</summary>
    private static readonly TimeOnly EndOfDay = TimeOnly.MaxValue;

    public TimeOnly Start { get; }
    public TimeOnly End { get; }

    public TimeRange(TimeOnly start, TimeOnly end)
    {
        if (end <= start)
        {
            throw new ArgumentException($"Конец слота ({end:HH\\:mm}) должен быть позже начала ({start:HH\\:mm}).");
        }

        Start = start;
        End = end;
    }

    /// <summary>Длительность занятия.</summary>
    public TimeSpan Duration => End - Start;

    /// <summary>Пересекаются ли два слота (границы [) — 18:00-19:30 и 19:30-21:00 НЕ пересекаются).</summary>
    public bool Overlaps(TimeRange other) => Start < other.End && other.Start < End;

    // Старый формат: "18:00-19:30", "18.00 - 19.30", "9:00–10:00" (короткое/длинное тире)
    [GeneratedRegex(@"^\s*(\d{1,2})[:.](\d{2})\s*[-–—]\s*(\d{1,2})[:.](\d{2})\s*$")]
    private static partial Regex LegacyPattern();

    /// <summary>Разбор строки старого формата TimeSlot. Не бросает исключений.</summary>
    public static bool TryParseLegacy(string? slot, out TimeRange range)
    {
        range = default;
        if (string.IsNullOrWhiteSpace(slot))
        {
            return false;
        }

        var m = LegacyPattern().Match(slot);
        if (!m.Success)
        {
            return false;
        }

        int h1 = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        int m1 = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        int h2 = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        int m2 = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);

        if (h1 > 23 || m1 > 59 || h2 > 24 || m2 > 59 || (h2 == 24 && m2 != 0))
        {
            return false;
        }

        var start = new TimeOnly(h1, m1);
        var end = h2 == 24 ? EndOfDay : new TimeOnly(h2, m2);
        if (end <= start)
        {
            return false;
        }

        range = new TimeRange(start, end);
        return true;
    }

    /// <summary>Разбор с исключением — для мест, где некорректный слот = ошибка программиста.</summary>
    public static TimeRange ParseLegacy(string slot) =>
        TryParseLegacy(slot, out var range)
            ? range
            : throw new FormatException($"Некорректный формат TimeSlot: '{slot}'. Ожидается 'HH:MM-HH:MM'.");

    /// <summary>Строка старого формата — для обратной совместимости (поле timeSlot в API v1).</summary>
    public string ToLegacyString() => $"{Format(Start)}-{FormatEnd(End)}";

    /// <summary>Литерал диапазона PostgreSQL: [18:00,19:30)</summary>
    public string ToPostgresLiteral() => $"[{Format(Start)},{FormatEnd(End)})";

    /// <summary>Начало слота текстом "HH:mm".</summary>
    public string StartText => Format(Start);

    /// <summary>Конец слота текстом "HH:mm" (полночь — "24:00").</summary>
    public string EndText => FormatEnd(End);

    /// <summary>Создание из пары строк нового формата { start, end }.</summary>
    public static bool TryCreate(string? start, string? end, out TimeRange range) =>
        TryParseLegacy($"{start}-{end}", out range);

    public override string ToString() => ToLegacyString();

    private static string Format(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string FormatEnd(TimeOnly t) => t == EndOfDay ? "24:00" : Format(t);
}
