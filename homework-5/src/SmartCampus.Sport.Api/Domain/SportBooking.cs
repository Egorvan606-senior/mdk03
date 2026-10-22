namespace SmartCampus.Sport.Domain;

/// <summary>Статусы брони (в БД — строка в колонке status).</summary>
public static class BookingStatus
{
    public const string Active = "Active";
    public const string Cancelled = "Cancelled";
}

/// <summary>Бронь зала / корта / бассейна спорткомплекса.</summary>
public sealed class SportBooking
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public int FacilityId { get; init; }
    public string StudentId { get; init; } = string.Empty;
    public DateOnly BookingDate { get; init; }

    /// <summary>Новый тип слота (CAMPUS-110).</summary>
    public TimeRange Slot { get; init; }

    public string Status { get; set; } = BookingStatus.Active;
    public string CreatedBy { get; init; } = "app";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Бизнес-правила бронирования спорткомплекса.
/// </summary>
public static class BookingRules
{
    public static readonly TimeOnly Opening = new(7, 0);
    public static readonly TimeOnly Closing = new(23, 0);
    public static readonly TimeSpan MinDuration = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(3);

    /// <summary>Возвращает текст ошибки или null, если слот допустим.</summary>
    public static string? Validate(TimeRange slot, DateOnly date, DateOnly today)
    {
        if (date < today)
        {
            return "Нельзя забронировать зал на прошедшую дату.";
        }

        if (slot.Start < Opening || slot.End > Closing)
        {
            return $"Спорткомплекс работает с {Opening:HH\\:mm} до {Closing:HH\\:mm}.";
        }

        if (slot.Duration < MinDuration)
        {
            return "Минимальная длительность занятия — 30 минут.";
        }

        if (slot.Duration > MaxDuration)
        {
            return "Максимальная длительность занятия — 3 часа.";
        }

        return null;
    }
}
