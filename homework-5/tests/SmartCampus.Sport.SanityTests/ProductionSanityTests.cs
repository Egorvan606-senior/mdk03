using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace SmartCampus.Sport.SanityTests;

/// <summary>
/// Production Sanity-тест релиза v1.9.0 (CAMPUS-110).
/// Выполняется СРАЗУ после выкатки на боевой контур одной точечной проверкой
/// главного сценария: «создать бронь зала -> прочитать её -> удалить».
///
/// Запуск:
///   set SANITY_BASE_URL=https://sport.campus.spbiem.local
///   set SANITY_TOKEN=***            (секрет из хранилища CI, не из репозитория)
///   dotnet test --filter Category=Sanity
///
/// Безопасность для боевых данных:
///   * бронь создаётся в служебном зале 999 (не виден студентам) и на дату через 60 дней;
///   * запись помечается created_by = 'sanity-bot' (по токену X-Sanity-Token);
///   * Cleanup выполняется в DisposeAsync — ВСЕГДА, даже если проверки упали;
///   * API физически удаляет только записи sanity-bot.
/// </summary>
[Trait("Category", "Sanity")]
public sealed class ProductionSanityTests : IAsyncLifetime
{
    private const int SanityFacilityId = 999;
    private const string Endpoint = "api/v1/sport/bookings";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly List<Guid> _createdIds = [];

    public ProductionSanityTests()
    {
        string baseUrl = Environment.GetEnvironmentVariable(SanityFactAttribute.BaseUrlVariable) ?? "http://localhost";
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(15)
        };
        _http.DefaultRequestHeaders.Add("X-Sanity-Token",
            Environment.GetEnvironmentVariable(SanityFactAttribute.TokenVariable) ?? string.Empty);
        _http.DefaultRequestHeaders.Add("User-Agent", "campus-sanity/1.9.0");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    [SanityFact]
    public async Task CreateBooking_InNewTimeRangeFormat_IsStoredAndReturnedWithLegacyField()
    {
        // Arrange — тестовая бронь 22:00–22:30 в служебном зале
        var date = DateOnly.FromDateTime(DateTime.Today.AddDays(60));
        var request = new
        {
            facilityId = SanityFacilityId,
            studentId = "SANITY-BOT",
            bookingDate = date,
            timeRange = new { start = "22:00", end = "22:30" }
        };

        // Act 1 — создание (боевой сценарий)
        using var created = await _http.PostAsJsonAsync(Endpoint, request, Json);
        string createdBody = await created.Content.ReadAsStringAsync();
        Assert.True(created.StatusCode == HttpStatusCode.Created,
            $"Ожидался 201 Created, получено {(int)created.StatusCode}: {createdBody}");

        var booking = JsonSerializer.Deserialize<BookingDto>(createdBody, Json)!;
        _createdIds.Add(booking.Id); // регистрируем для Cleanup ДО остальных проверок

        // Assert — новый формат и обратная совместимость
        Assert.Equal("22:00", booking.TimeRange.Start);
        Assert.Equal("22:30", booking.TimeRange.End);
        Assert.Equal("22:00-22:30", booking.TimeSlot);   // старое поле для клиентов v1.8
        Assert.Equal(30, booking.DurationMinutes);
        Assert.Equal("Active", booking.Status);
        Assert.True(created.Headers.Contains("X-Trace-Id"), "Нет заголовка X-Trace-Id — цифровой след не формируется");
        Assert.False(string.IsNullOrWhiteSpace(booking.TraceId));

        // Act 2 — чтение записи из БД через API
        var loaded = await _http.GetFromJsonAsync<BookingDto>($"{Endpoint}/{booking.Id}", Json);
        Assert.NotNull(loaded);
        Assert.Equal(booking.Id, loaded!.Id);
        Assert.Equal(booking.TimeRange, loaded.TimeRange);

        // Act 3 — повторная бронь того же слота должна быть отклонена (запрет пересечений)
        using var duplicate = await _http.PostAsJsonAsync(Endpoint, request, Json);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [SanityFact]
    public async Task CreateBooking_InLegacyTimeSlotFormat_StillAccepted()
    {
        // Старый мобильный клиент 2.x присылает строку — релиз не должен его сломать
        var request = new
        {
            facilityId = SanityFacilityId,
            studentId = "SANITY-BOT",
            bookingDate = DateOnly.FromDateTime(DateTime.Today.AddDays(61)),
            timeSlot = "21:00-21:45"
        };

        using var created = await _http.PostAsJsonAsync(Endpoint, request, Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var booking = (await created.Content.ReadFromJsonAsync<BookingDto>(Json))!;
        _createdIds.Add(booking.Id);

        Assert.Equal(new TimeRangeDto("21:00", "21:45"), booking.TimeRange);
        Assert.Equal(45, booking.DurationMinutes);
    }

    /// <summary>
    /// CLEANUP — мгновенная очистка тестовых данных. Вызывается xUnit после
    /// каждого теста независимо от результата, поэтому на проде не остаётся
    /// «мусорных» броней, а служебный слот освобождается для следующего запуска.
    /// </summary>
    public async Task DisposeAsync()
    {
        var errors = new List<string>();
        foreach (var id in _createdIds)
        {
            try
            {
                using var deleted = await _http.DeleteAsync($"{Endpoint}/{id}");
                if (deleted.StatusCode != HttpStatusCode.NoContent)
                {
                    errors.Add($"{id}: DELETE вернул {(int)deleted.StatusCode}");
                    continue;
                }

                // контроль: запись действительно удалена
                using var check = await _http.GetAsync($"{Endpoint}/{id}");
                if (check.StatusCode != HttpStatusCode.NotFound)
                {
                    errors.Add($"{id}: после очистки GET вернул {(int)check.StatusCode}");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                errors.Add($"{id}: {ex.Message}");
            }
        }

        _http.Dispose();

        if (errors.Count > 0)
        {
            // Падение на очистке — сигнал дежурному удалить записи вручную
            throw new InvalidOperationException("Sanity Cleanup не выполнен: " + string.Join("; ", errors));
        }
    }

    private sealed record TimeRangeDto(string Start, string End);

    private sealed record BookingDto(
        Guid Id,
        int FacilityId,
        string StudentId,
        DateOnly BookingDate,
        string TimeSlot,
        TimeRangeDto TimeRange,
        int DurationMinutes,
        string Status,
        string TraceId);
}
