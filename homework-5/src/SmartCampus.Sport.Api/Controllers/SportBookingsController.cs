using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SmartCampus.Sport.Contracts;
using SmartCampus.Sport.Data;
using SmartCampus.Sport.Domain;
using SmartCampus.Sport.Telemetry;

namespace SmartCampus.Sport.Controllers;

/// <summary>
/// API бронирования залов спорткомплекса ИС «Умный кампус».
/// CAMPUS-110: слот TimeSlot (string) заменён на TimeRange; каждая операция
/// оставляет цифровой след — теги jira.issue и campus.booking_id в трассе OpenTelemetry.
/// </summary>
[ApiController]
[Route("api/v1/sport/bookings")]
[Produces("application/json")]
public sealed class SportBookingsController(
    IBookingRepository repository,
    IConfiguration configuration,
    TimeProvider clock,
    ILogger<SportBookingsController> logger) : ControllerBase
{
    /// <summary>Создать бронь зала.</summary>
    [HttpPost]
    [ProducesResponseType<BookingResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BookingResponse>> Create(CreateBookingRequest request, CancellationToken ct)
    {
        // 1. Цифровой след: span входящего HTTP-запроса получает тег задачи Jira
        //    и доменные идентификаторы. По тегу jira.issue = CAMPUS-110 в Jaeger
        //    находятся все запросы, прошедшие через изменённый код.
        var activity = CampusTelemetry.TagCurrent(request.FacilityId);

        // 2. Дочерний span бизнес-операции — видно, сколько времени заняла
        //    именно логика бронирования, а не сеть и сериализация.
        using var operation = CampusTelemetry.Source.StartActivity("sport.booking.create");
        operation?.SetTag(CampusTelemetry.JiraIssueTag, CampusTelemetry.JiraIssue);
        operation?.SetTag(CampusTelemetry.FacilityIdTag, request.FacilityId);

        // 3. Слот: новый формат имеет приоритет, старый — для клиентов v1.8.x
        TimeRange slot;
        bool legacyFormat;
        if (request.TimeRange is not null)
        {
            if (!TimeRange.TryCreate(request.TimeRange.Start, request.TimeRange.End, out slot))
            {
                return SlotError(operation, "Некорректный timeRange. Ожидается { \"start\": \"HH:mm\", \"end\": \"HH:mm\" }.");
            }
            legacyFormat = false;
        }
        else if (TimeRange.TryParseLegacy(request.TimeSlot, out slot))
        {
            legacyFormat = true;
        }
        else
        {
            return SlotError(operation, "Укажите timeRange или timeSlot в формате 'HH:mm-HH:mm'.");
        }

        operation?.SetTag(CampusTelemetry.TimeRangeTag, slot.ToPostgresLiteral());
        operation?.SetTag("campus.request.legacy_time_slot", legacyFormat);

        // 4. Бизнес-правила: часы работы, длительность, дата
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        if (BookingRules.Validate(slot, request.BookingDate, today) is { } ruleError)
        {
            return SlotError(operation, ruleError);
        }

        if (await repository.HasOverlapAsync(request.FacilityId, request.BookingDate, slot, ct))
        {
            operation?.SetStatus(ActivityStatusCode.Error, "overlap");
            return Conflict(new ProblemDetails
            {
                Title = "Слот занят",
                Detail = $"Зал {request.FacilityId} на {request.BookingDate:dd.MM.yyyy} уже забронирован на {slot}."
            });
        }

        var booking = new SportBooking
        {
            FacilityId = request.FacilityId,
            StudentId = request.StudentId,
            BookingDate = request.BookingDate,
            Slot = slot,
            CreatedBy = IsAuthorizedSanityCall() ? SanityDefaults.BotName : "app"
        };

        // Идентификатор сущности известен до записи — сразу кладём его в трассу,
        // чтобы при ошибке БД было видно, какая бронь не сохранилась.
        activity?.SetTag(CampusTelemetry.BookingIdTag, booking.Id.ToString());
        operation?.SetTag(CampusTelemetry.BookingIdTag, booking.Id.ToString());

        try
        {
            await repository.CreateAsync(booking, ct);
        }
        catch (BookingConflictException ex)
        {
            operation?.SetStatus(ActivityStatusCode.Error, "overlap");
            return Conflict(new ProblemDetails { Title = "Слот занят", Detail = ex.Message });
        }

        operation?.AddEvent(new ActivityEvent("booking.created"));
        operation?.SetStatus(ActivityStatusCode.Ok);

        string traceId = WriteTraceHeader(activity);
        logger.LogInformation("[{Jira}] Создана бронь {BookingId}: зал {FacilityId}, {Date}, {Slot}, trace {TraceId}",
            CampusTelemetry.JiraIssue, booking.Id, booking.FacilityId, booking.BookingDate, slot, traceId);

        return CreatedAtAction(nameof(GetById), new { id = booking.Id }, BookingResponse.From(booking, traceId));
    }

    /// <summary>Получить бронь по идентификатору.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<BookingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BookingResponse>> GetById(Guid id, CancellationToken ct)
    {
        var booking = await repository.GetAsync(id, ct);
        var activity = CampusTelemetry.TagCurrent(booking?.FacilityId, id);
        if (booking is null)
        {
            return NotFound();
        }

        return Ok(BookingResponse.From(booking, WriteTraceHeader(activity)));
    }

    /// <summary>
    /// Отменить бронь. Для служебных записей sanity-теста (заголовок X-Sanity-Token)
    /// выполняется физическое удаление — шаг Cleanup после проверки релиза.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var activity = CampusTelemetry.TagCurrent(null, id);
        bool sanity = IsAuthorizedSanityCall();
        activity?.SetTag("campus.sanity_cleanup", sanity);

        bool done = sanity
            ? await repository.PurgeSanityRecordAsync(id, ct)
            : await repository.CancelAsync(id, ct);

        return done ? NoContent() : NotFound();
    }

    private bool IsAuthorizedSanityCall()
    {
        string? expected = configuration["Sanity:Token"];
        return !string.IsNullOrEmpty(expected)
               && Request.Headers.TryGetValue(SanityDefaults.HeaderName, out var actual)
               && string.Equals(actual.ToString(), expected, StringComparison.Ordinal);
    }

    private string WriteTraceHeader(Activity? activity)
    {
        // TraceId в ответе — оператор Service Desk по нему находит запрос в трассах
        string traceId = activity?.TraceId.ToString() ?? HttpContext.TraceIdentifier;
        Response.Headers["X-Trace-Id"] = traceId;
        return traceId;
    }

    private BadRequestObjectResult SlotError(Activity? operation, string message)
    {
        operation?.SetStatus(ActivityStatusCode.Error, "validation");
        return BadRequest(new ProblemDetails { Title = "Некорректный слот бронирования", Detail = message });
    }
}
