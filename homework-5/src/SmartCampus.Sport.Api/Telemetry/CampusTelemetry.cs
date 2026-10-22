using System.Diagnostics;

namespace SmartCampus.Sport.Telemetry;

/// <summary>
/// Единая точка телеметрии модуля «Спортивный комплекс».
/// Имена тегов вынесены в константы, чтобы по ним можно было искать
/// трассы в Jaeger / Grafana Tempo одинаково во всех сервисах кампуса.
/// </summary>
public static class CampusTelemetry
{
    /// <summary>Имя ActivitySource — регистрируется в OpenTelemetry через AddSource().</summary>
    public const string SourceName = "SmartCampus.Sport";

    public static readonly ActivitySource Source = new(SourceName, "1.9.0");

    /// <summary>Задача Jira, по которой внесено изменение (цифровой след CALS).</summary>
    public const string JiraIssueTag = "jira.issue";

    /// <summary>Значение тега для варианта 10.</summary>
    public const string JiraIssue = "CAMPUS-110";

    // Доменные идентификаторы сущностей
    public const string BookingIdTag = "campus.booking_id";
    public const string FacilityIdTag = "campus.facility_id";
    public const string TimeRangeTag = "campus.time_range";
    public const string ModuleTag = "campus.module";
    public const string ModuleName = "sport-complex";

    /// <summary>
    /// Проставляет обязательные теги на текущий span (Activity.Current),
    /// который создала инструментация ASP.NET Core для входящего запроса.
    /// </summary>
    public static Activity? TagCurrent(int? facilityId, Guid? bookingId = null)
    {
        var activity = Activity.Current;
        if (activity is null)
        {
            return null; // трассировка выключена или запрос не сэмплирован
        }

        activity.SetTag(JiraIssueTag, JiraIssue);
        activity.SetTag(ModuleTag, ModuleName);
        if (facilityId is not null)
        {
            activity.SetTag(FacilityIdTag, facilityId.Value);
        }

        if (bookingId is not null)
        {
            activity.SetTag(BookingIdTag, bookingId.Value.ToString());
        }

        // Baggage уходит в заголовке baggage во все исходящие вызовы,
        // поэтому номер задачи увидят и нижележащие сервисы (биллинг, турникеты).
        activity.SetBaggage(JiraIssueTag, JiraIssue);
        return activity;
    }
}
