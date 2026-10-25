using System.Collections.Concurrent;
using SmartCampus.Sport.Domain;

namespace SmartCampus.Sport.Data;

/// <summary>
/// Хранилище в памяти — для локального запуска из Visual Studio без PostgreSQL
/// (используется, когда строка подключения ConnectionStrings:Campus пустая).
/// </summary>
public sealed class InMemoryBookingRepository : IBookingRepository
{
    private readonly ConcurrentDictionary<Guid, SportBooking> _items = new();
    private readonly object _writeLock = new();

    public Task CreateAsync(SportBooking booking, CancellationToken ct)
    {
        // Блокировка имитирует EXCLUDE-ограничение БД: проверка и вставка атомарны
        lock (_writeLock)
        {
            if (Overlaps(booking.FacilityId, booking.BookingDate, booking.Slot))
            {
                throw new BookingConflictException("Слот уже занят другой бронью.");
            }

            _items[booking.Id] = booking;
        }

        return Task.CompletedTask;
    }

    public Task<SportBooking?> GetAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(_items.TryGetValue(id, out var b) ? b : null);

    public Task<bool> HasOverlapAsync(int facilityId, DateOnly date, TimeRange slot, CancellationToken ct) =>
        Task.FromResult(Overlaps(facilityId, date, slot));

    public Task<bool> CancelAsync(Guid id, CancellationToken ct)
    {
        if (_items.TryGetValue(id, out var b) && b.Status == BookingStatus.Active)
        {
            b.Status = BookingStatus.Cancelled;
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public Task<bool> PurgeSanityRecordAsync(Guid id, CancellationToken ct)
    {
        bool removed = _items.TryGetValue(id, out var b)
                       && b.CreatedBy == SanityDefaults.BotName
                       && _items.TryRemove(id, out _);
        return Task.FromResult(removed);
    }

    private bool Overlaps(int facilityId, DateOnly date, TimeRange slot) =>
        _items.Values.Any(b => b.FacilityId == facilityId
                               && b.BookingDate == date
                               && b.Status == BookingStatus.Active
                               && b.Slot.Overlaps(slot));
}
