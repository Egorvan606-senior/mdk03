using SmartCampus.Sport.Domain;

namespace SmartCampus.Sport.Contracts;

/// <summary>Слот в новом формате (v1.6.0+).</summary>
public sealed record TimeRangeDto(string Start, string End);

/// <summary>
/// Запрос на бронирование. Принимает ОБА формата слота, чтобы старые
/// клиенты (мобильное приложение 2.x, киоск) продолжали работать:
///   * timeSlot  — "18:00-19:30" (устаревший, будет удалён в v2.0.0);
///   * timeRange — { "start": "18:00", "end": "19:30" } (новый).
/// </summary>
public sealed record CreateBookingRequest(
    int FacilityId,
    string StudentId,
    DateOnly BookingDate,
    string? TimeSlot,
    TimeRangeDto? TimeRange);

/// <summary>
/// Ответ. Аддитивное изменение контракта: добавлено timeRange,
/// поле timeSlot сохранено (помечено устаревшим) — Breaking Change нет.
/// </summary>
public sealed record BookingResponse(
    Guid Id,
    int FacilityId,
    string StudentId,
    DateOnly BookingDate,
    [property: Obsolete("Используйте TimeRange. Поле будет удалено в v2.0.0 (CAMPUS-110).")]
    string TimeSlot,
    TimeRangeDto TimeRange,
    int DurationMinutes,
    string Status,
    string TraceId)
{
    public static BookingResponse From(SportBooking b, string traceId) =>
#pragma warning disable CS0618 // заполняем устаревшее поле намеренно — для старых клиентов
        new(b.Id,
            b.FacilityId,
            b.StudentId,
            b.BookingDate,
            b.Slot.ToLegacyString(),
            new TimeRangeDto(b.Slot.StartText, b.Slot.EndText),
            (int)b.Slot.Duration.TotalMinutes,
            b.Status,
            traceId);
#pragma warning restore CS0618
}
