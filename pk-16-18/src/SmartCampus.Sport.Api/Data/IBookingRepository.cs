using SmartCampus.Sport.Domain;

namespace SmartCampus.Sport.Data;

/// <summary>Хранилище броней спорткомплекса.</summary>
public interface IBookingRepository
{
    Task CreateAsync(SportBooking booking, CancellationToken ct);

    Task<SportBooking?> GetAsync(Guid id, CancellationToken ct);

    /// <summary>Есть ли активная бронь того же зала в тот же день, пересекающая слот.</summary>
    Task<bool> HasOverlapAsync(int facilityId, DateOnly date, TimeRange slot, CancellationToken ct);

    /// <summary>Обычная отмена брони пользователем (запись остаётся для отчётности).</summary>
    Task<bool> CancelAsync(Guid id, CancellationToken ct);

    /// <summary>Физическое удаление ТОЛЬКО служебных записей sanity-теста (Cleanup).</summary>
    Task<bool> PurgeSanityRecordAsync(Guid id, CancellationToken ct);
}

/// <summary>Бронь пересекается с уже существующей (HTTP 409).</summary>
public sealed class BookingConflictException(string message) : Exception(message);

/// <summary>
/// Фаза миграции CAMPUS-110 — переключается конфигурацией, без пересборки:
///   Expand   — пишем в time_slot И time_range (dual write), читаем time_range,
///              а для ещё не перенесённых строк — разбираем time_slot;
///   Contract — работаем только с time_range (после 03_contract.sql).
/// </summary>
public enum MigrationPhase
{
    Expand,
    Contract
}

public sealed class BookingSchemaOptions
{
    public const string Section = "BookingSchema";

    public MigrationPhase Phase { get; set; } = MigrationPhase.Expand;
}
