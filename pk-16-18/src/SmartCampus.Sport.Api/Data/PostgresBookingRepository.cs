using System.Data.Common;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using SmartCampus.Sport.Domain;
using SmartCampus.Sport.Telemetry;

namespace SmartCampus.Sport.Data;

/// <summary>
/// Репозиторий PostgreSQL с поддержкой паттерна Expand and Contract.
/// Работает через абстракцию DbDataSource (в Program.cs подставляется NpgsqlDataSource),
/// поэтому SQL-логика не зависит от конкретного драйвера.
/// </summary>
public sealed class PostgresBookingRepository(
    DbDataSource dataSource,
    IOptionsMonitor<BookingSchemaOptions> options,
    ILogger<PostgresBookingRepository> logger) : IBookingRepository
{
    /// <summary>SQLSTATE нарушения EXCLUDE-ограничения ex_bookings_no_overlap (фаза Contract).</summary>
    private const string ExclusionViolation = "23P01";

    private MigrationPhase Phase => options.CurrentValue.Phase;

    // -----------------------------------------------------------------
    // ЗАПИСЬ: DUAL WRITE
    // -----------------------------------------------------------------
    public async Task CreateAsync(SportBooking booking, CancellationToken ct)
    {
        // Фаза Expand: пишем ОДНОВРЕМЕННО в старую (time_slot) и новую (time_range)
        // структуру. Старые экземпляры v1.5.x и отчёты продолжают читать time_slot,
        // новые — time_range. Откат приложения на v1.5.x безопасен.
        const string dualWriteSql = """
            INSERT INTO sport.bookings
                (id, facility_id, student_id, booking_date, time_slot, time_range, status, created_by, created_at)
            VALUES
                (@id, @facility_id, @student_id, @booking_date, @time_slot, CAST(@time_range AS sport.timerange),
                 @status, @created_by, @created_at)
            """;

        // Фаза Contract: колонки time_slot больше нет — пишем только новую структуру.
        const string newOnlySql = """
            INSERT INTO sport.bookings
                (id, facility_id, student_id, booking_date, time_range, status, created_by, created_at)
            VALUES
                (@id, @facility_id, @student_id, @booking_date, CAST(@time_range AS sport.timerange),
                 @status, @created_by, @created_at)
            """;

        bool dualWrite = Phase == MigrationPhase.Expand;

        using var span = CampusTelemetry.Source.StartActivity("db.sport.bookings.insert", ActivityKind.Client);
        span?.SetTag(CampusTelemetry.JiraIssueTag, CampusTelemetry.JiraIssue);
        span?.SetTag(CampusTelemetry.BookingIdTag, booking.Id.ToString());
        span?.SetTag("db.system", "postgresql");
        span?.SetTag("campus.migration.phase", Phase.ToString());
        span?.SetTag("campus.migration.dual_write", dualWrite);

        await using var cmd = dataSource.CreateCommand(dualWrite ? dualWriteSql : newOnlySql);
        AddParameter(cmd, "id", booking.Id);
        AddParameter(cmd, "facility_id", booking.FacilityId);
        AddParameter(cmd, "student_id", booking.StudentId);
        AddParameter(cmd, "booking_date", booking.BookingDate);
        if (dualWrite)
        {
            AddParameter(cmd, "time_slot", booking.Slot.ToLegacyString());   // старая структура
        }
        AddParameter(cmd, "time_range", booking.Slot.ToPostgresLiteral());   // новая структура
        AddParameter(cmd, "status", booking.Status);
        AddParameter(cmd, "created_by", booking.CreatedBy);
        AddParameter(cmd, "created_at", booking.CreatedAt.UtcDateTime);

        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
            logger.LogInformation(
                "[{Jira}] Бронь {BookingId} сохранена (phase={Phase}, dualWrite={DualWrite}, slot={Slot})",
                CampusTelemetry.JiraIssue, booking.Id, Phase, dualWrite, booking.Slot);
        }
        catch (DbException ex) when (ex.SqlState == ExclusionViolation)
        {
            // Гонка двух запросов: проверка HasOverlapAsync прошла у обоих,
            // но EXCLUDE-ограничение в БД пропустило только один.
            span?.SetStatus(ActivityStatusCode.Error, "overlap");
            throw new BookingConflictException("Слот уже занят другой бронью.");
        }
    }

    // -----------------------------------------------------------------
    // ЧТЕНИЕ: новая структура с запасным вариантом на время миграции
    // -----------------------------------------------------------------
    public async Task<SportBooking?> GetAsync(Guid id, CancellationToken ct)
    {
        string sql = Phase == MigrationPhase.Expand
            ? """
              SELECT id, facility_id, student_id, booking_date, status, created_by, created_at,
                     lower(time_range) AS range_start, upper(time_range) AS range_end, time_slot
              FROM sport.bookings WHERE id = @id
              """
            : """
              SELECT id, facility_id, student_id, booking_date, status, created_by, created_at,
                     lower(time_range) AS range_start, upper(time_range) AS range_end, NULL::text AS time_slot
              FROM sport.bookings WHERE id = @id
              """;

        await using var cmd = dataSource.CreateCommand(sql);
        AddParameter(cmd, "id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        TimeRange slot;
        if (!await reader.IsDBNullAsync(7, ct))
        {
            // строка уже в новом формате
            slot = new TimeRange(reader.GetFieldValue<TimeOnly>(7), reader.GetFieldValue<TimeOnly>(8));
        }
        else
        {
            // строка ещё не перенесена фазой 02_migrate — читаем старое поле
            string legacy = reader.GetString(9);
            slot = TimeRange.ParseLegacy(legacy);
            Activity.Current?.AddEvent(new ActivityEvent("campus.migration.legacy_read",
                tags: new ActivityTagsCollection { [CampusTelemetry.BookingIdTag] = id.ToString() }));
        }

        return new SportBooking
        {
            Id = reader.GetGuid(0),
            FacilityId = reader.GetInt32(1),
            StudentId = reader.GetString(2),
            BookingDate = reader.GetFieldValue<DateOnly>(3),
            Status = reader.GetString(4),
            CreatedBy = reader.GetString(5),
            CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc)),
            Slot = slot
        };
    }

    public async Task<bool> HasOverlapAsync(int facilityId, DateOnly date, TimeRange slot, CancellationToken ct)
    {
        // В фазе Expand часть строк может быть ещё без time_range — для них
        // диапазон вычисляется из старой строки на лету.
        string rangeExpr = Phase == MigrationPhase.Expand
            ? "COALESCE(time_range, sport.parse_time_slot(time_slot))"
            : "time_range";

        string sql = $"""
            SELECT EXISTS (
                SELECT 1 FROM sport.bookings
                WHERE facility_id = @facility_id
                  AND booking_date = @booking_date
                  AND status = 'Active'
                  AND {rangeExpr} && CAST(@time_range AS sport.timerange))
            """;

        await using var cmd = dataSource.CreateCommand(sql);
        AddParameter(cmd, "facility_id", facilityId);
        AddParameter(cmd, "booking_date", date);
        AddParameter(cmd, "time_range", slot.ToPostgresLiteral());
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<bool> CancelAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            "UPDATE sport.bookings SET status = 'Cancelled' WHERE id = @id AND status = 'Active'");
        AddParameter(cmd, "id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> PurgeSanityRecordAsync(Guid id, CancellationToken ct)
    {
        // Удаляются ТОЛЬКО записи, созданные sanity-ботом: даже при ошибке
        // в тесте реальную бронь студента так удалить невозможно.
        await using var cmd = dataSource.CreateCommand(
            "DELETE FROM sport.bookings WHERE id = @id AND created_by = @bot");
        AddParameter(cmd, "id", id);
        AddParameter(cmd, "bot", SanityDefaults.BotName);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}

/// <summary>Общие константы production sanity-теста.</summary>
public static class SanityDefaults
{
    public const string BotName = "sanity-bot";
    public const string HeaderName = "X-Sanity-Token";
}
