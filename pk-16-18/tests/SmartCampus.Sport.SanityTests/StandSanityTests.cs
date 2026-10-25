using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using SmartCampus.Sport.Telemetry;
using Xunit;

namespace SmartCampus.Sport.SanityTests;

/// <summary>
/// Стенд API в памяти теста: тот же код сервиса v1.6.0, хранилище InMemory,
/// служебный токен sanity-бота. Позволяет прогнать сценарий production sanity-теста
/// (создание брони -> проверки -> Cleanup) на каждом PR в CI — до выхода на боевой контур.
/// </summary>
public sealed class SportApiFactory : WebApplicationFactory<Program>
{
    public const string SanityToken = "stand-sanity-token";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Campus", "");   // InMemory вместо PostgreSQL
        builder.UseSetting("Sanity:Token", SanityToken);
        builder.UseEnvironment("Staging");
    }
}

/// <summary>Сценарий sanity-теста на стенде: запись на слот тренировки с последующей отменой (Cleanup).</summary>
[Trait("Category", "Stand")]
public sealed class StandSanityTests(SportApiFactory factory) : IClassFixture<SportApiFactory>
{
    private const string Endpoint = "api/v1/sport/bookings";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact(DisplayName = "Sanity на стенде: запись на слот тренировки -> чтение -> запрет дубля -> отмена (Cleanup)")]
    public async Task BookTrainingSlot_ThenCleanup()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Sanity-Token", SportApiFactory.SanityToken);
        var date = DateOnly.FromDateTime(DateTime.Today.AddDays(30));
        var request = new { facilityId = 999, studentId = "SANITY-BOT", bookingDate = date, timeRange = new { start = "22:00", end = "22:30" } };

        // 1. Запись на слот (новый формат TimeRange)
        using var created = await client.PostAsJsonAsync(Endpoint, request, Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var booking = (await created.Content.ReadFromJsonAsync<JsonElement>(Json));
        var id = booking.GetProperty("id").GetGuid();
        try
        {
            Assert.Equal("22:00-22:30", booking.GetProperty("timeSlot").GetString());   // обратная совместимость
            Assert.Equal(30, booking.GetProperty("durationMinutes").GetInt32());
            Assert.True(created.Headers.Contains("X-Trace-Id"), "нет X-Trace-Id — цифровой след не формируется");

            // 2. Чтение
            using var loaded = await client.GetAsync($"{Endpoint}/{id}");
            Assert.Equal(HttpStatusCode.OK, loaded.StatusCode);

            // 3. Повторная бронь того же слота запрещена (пересечение)
            using var duplicate = await client.PostAsJsonAsync(Endpoint, request, Json);
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        }
        finally
        {
            // 4. CLEANUP — выполняется всегда, даже если проверки выше упали
            using var deleted = await client.DeleteAsync($"{Endpoint}/{id}");
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using var afterCleanup = await client.GetAsync($"{Endpoint}/{id}");
        Assert.Equal(HttpStatusCode.NotFound, afterCleanup.StatusCode); // тестовых данных не осталось
    }

    [Fact(DisplayName = "Старый клиент: слот строкой TimeSlot \"21:00-21:45\" по-прежнему принимается")]
    public async Task LegacyTimeSlot_StillAccepted()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Sanity-Token", SportApiFactory.SanityToken);
        var request = new { facilityId = 998, studentId = "SANITY-BOT", bookingDate = DateOnly.FromDateTime(DateTime.Today.AddDays(31)), timeSlot = "21:00-21:45" };

        using var created = await client.PostAsJsonAsync(Endpoint, request, Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var booking = await created.Content.ReadFromJsonAsync<JsonElement>(Json);

        Assert.Equal("21:00", booking.GetProperty("timeRange").GetProperty("start").GetString());
        Assert.Equal(45, booking.GetProperty("durationMinutes").GetInt32());

        using var deleted = await client.DeleteAsync($"{Endpoint}/{booking.GetProperty("id").GetGuid()}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }
}

/// <summary>
/// Цифровой след (Блок 1): каждый запрос бронирования оставляет в трассе OpenTelemetry
/// теги jira.issue = CAMPUS-110 и campus.entity_id = идентификатор брони.
/// </summary>
[Trait("Category", "Stand")]
public sealed class DigitalTraceTests(SportApiFactory factory) : IClassFixture<SportApiFactory>, IDisposable
{
    private readonly ConcurrentBag<Activity> _stopped = [];
    private readonly ActivityListener _listener = new()
    {
        ShouldListenTo = s => s.Name == CampusTelemetry.SourceName,
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
    };

    [Fact(DisplayName = "Трасса бронирования содержит jira.issue=CAMPUS-110 и campus.entity_id")]
    public async Task BookingSpan_HasJiraAndEntityTags()
    {
        _listener.ActivityStopped = a => _stopped.Add(a);
        ActivitySource.AddActivityListener(_listener);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Sanity-Token", SportApiFactory.SanityToken);
        var request = new { facilityId = 997, studentId = "SANITY-BOT", bookingDate = DateOnly.FromDateTime(DateTime.Today.AddDays(32)), timeRange = new { start = "20:00", end = "21:00" } };

        using var created = await client.PostAsJsonAsync("api/v1/sport/bookings", request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        string id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        string traceId = created.Headers.GetValues("X-Trace-Id").Single();

        var span = Assert.Single(_stopped, a => a.OperationName == "sport.booking.create" && (string?)a.GetTagItem(CampusTelemetry.EntityIdTag) == id);
        Assert.Equal("CAMPUS-110", span.GetTagItem(CampusTelemetry.JiraIssueTag));
        Assert.Equal(traceId, span.TraceId.ToString());   // TraceId из ответа = TraceId спана: по нему лог находится в Jaeger

        await client.DeleteAsync($"api/v1/sport/bookings/{id}"); // Cleanup
    }

    public void Dispose() => _listener.Dispose();
}
