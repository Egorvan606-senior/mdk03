namespace Campus.HealthCheck.Checks;

/// <summary>
/// Одна проверка в цепочке Health Check. Реализация возвращает
/// (статус, подробности) — замер задержки, повторы и перехват исключений
/// выполняет общий HealthCheckRunner, чтобы все проверки вели себя одинаково.
/// </summary>
public interface IHealthCheck
{
    /// <summary>Короткий код для журналов и отчёта (CFG, LOCAL_DB, API …).</summary>
    string Code { get; }

    /// <summary>Название проверки для оператора.</summary>
    string Title { get; }

    CheckSeverity Severity { get; }

    /// <summary>Сколько раз повторять при сетевых сбоях (1 — без повторов).</summary>
    int MaxAttempts { get; }

    /// <summary>Порог задержки, выше которого результат понижается до Warning (0 — не проверять).</summary>
    int WarnLatencyMs { get; }

    /// <summary>Таймаут одной попытки.</summary>
    TimeSpan Timeout { get; }

    Task<(CheckStatus Status, string Details)> ExecuteAsync(CancellationToken ct);
}

/// <summary>Базовый класс с общими значениями по умолчанию.</summary>
public abstract class HealthCheckBase : IHealthCheck
{
    public abstract string Code { get; }
    public abstract string Title { get; }
    public virtual CheckSeverity Severity => CheckSeverity.Critical;
    public virtual int MaxAttempts => 1;
    public virtual int WarnLatencyMs => 0;
    public virtual TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public abstract Task<(CheckStatus Status, string Details)> ExecuteAsync(CancellationToken ct);

    protected static (CheckStatus, string) Pass(string details) => (CheckStatus.Pass, details);
    protected static (CheckStatus, string) Warn(string details) => (CheckStatus.Warning, details);
    protected static (CheckStatus, string) Fail(string details) => (CheckStatus.Fail, details);
}
