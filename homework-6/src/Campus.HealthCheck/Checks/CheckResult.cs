namespace Campus.HealthCheck.Checks;

/// <summary>Итог отдельной проверки.</summary>
public enum CheckStatus
{
    /// <summary>Проверка пройдена.</summary>
    Pass,

    /// <summary>Работает, но с замечанием (например, высокая задержка).</summary>
    Warning,

    /// <summary>Проверка не пройдена.</summary>
    Fail
}

/// <summary>
/// Насколько важна проверка для допуска АРМ к работе.
/// Critical — без неё оператор не может работать (БД, API, считыватель);
/// NonCritical — работа возможна, но нужно устранить замечание.
/// </summary>
public enum CheckSeverity
{
    Critical,
    NonCritical
}

/// <summary>Результат одной проверки цепочки Health Check.</summary>
public sealed record CheckResult(
    string Code,
    string Title,
    CheckSeverity Severity,
    CheckStatus Status,
    long LatencyMs,
    int Attempts,
    string Details)
{
    /// <summary>Проверка блокирует ввод АРМ в эксплуатацию.</summary>
    public bool IsBlocking => Severity == CheckSeverity.Critical && Status == CheckStatus.Fail;

    public string StatusText => Status switch
    {
        CheckStatus.Pass => "OK",
        CheckStatus.Warning => "WARN",
        _ => "FAIL"
    };
}

/// <summary>Итоговое заключение о готовности АРМ.</summary>
public enum Readiness
{
    /// <summary>Все проверки пройдены — АРМ готово к промышленной эксплуатации.</summary>
    Ready,

    /// <summary>Есть некритичные замечания — допуск с условием устранения.</summary>
    ReadyWithRemarks,

    /// <summary>Есть непройденные критичные проверки — АРМ не допускается.</summary>
    NotReady
}
