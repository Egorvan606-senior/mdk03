using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrderService.Waf;

/// <summary>Режим работы WAF — по аналогии с директивой ModSecurity SecRuleEngine.</summary>
public enum WafMode
{
    Off,            // правила не применяются
    DetectionOnly,  // срабатывания только журналируются (режим «наблюдения» и режим отката)
    On              // запросы, нарушающие правила, блокируются ответом 403
}

/// <summary>Правило WAF (упрощённый аналог правил OWASP Core Rule Set).</summary>
public sealed record WafRule(string Id, string Description, Regex Pattern);

/// <summary>
/// Учебный стенд WAF перед API (кейс 10). В production эту роль выполняет
/// ModSecurity + OWASP CRS на Ingress NGINX (deploy/k8s/order-service-ingress-waf.yaml);
/// здесь те же принципы реализованы middleware, чтобы smoke-тесты и план отката можно
/// было проверить локально в Visual Studio:
///   * сигнатуры SQL-инъекций (942xxx) и XSS (941xxx);
///   * ограничение размера тела запроса (защита от «тяжёлых» запросов при DDoS);
///   * JSON-тело разбирается и проверяются ЗНАЧЕНИЯ полей, а не сырой текст —
///     это главная мера против ложных срабатываний на сложных JSON мобильного приложения
///     (в ModSecurity — ctl:requestBodyProcessor=JSON).
/// </summary>
public sealed class WafStandMiddleware(RequestDelegate next, IConfiguration configuration, ILogger<WafStandMiddleware> logger)
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);

    public static readonly IReadOnlyList<WafRule> Rules =
    [
        new("942100", "SQLi: UNION SELECT", new Regex(@"\bunion\b[\s\S]{0,20}\bselect\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout)),
        new("942130", "SQLi: тавтология ' OR 1=1", new Regex(@"['""]\s*(or|and)\s+['""]?\d+['""]?\s*=\s*['""]?\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout)),
        new("942350", "SQLi: DROP/TRUNCATE/DELETE после ;", new Regex(@";\s*(drop|truncate|delete)\s+(table|from)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout)),
        new("942160", "SQLi: слепая инъекция SLEEP()/WAITFOR", new Regex(@"\bsleep\s*\(\s*\d+\s*\)|\bwaitfor\s+delay\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout)),
        new("941100", "XSS: тег <script>", new Regex(@"<\s*script\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout)),
        new("941120", "XSS: обработчик события / javascript:", new Regex(@"\bon(error|load|click|mouseover)\s*=|javascript\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout)),
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        var mode = configuration.GetValue("Waf:Mode", WafMode.On);
        long maxBody = configuration.GetValue("Waf:MaxBodyBytes", 65536L);
        context.Response.Headers["X-Waf-Mode"] = mode.ToString(); // smoke-тест проверяет режим после деплоя

        if (mode == WafMode.Off)
        {
            await next(context);
            return;
        }

        // 1. Ограничение размера тела
        if (context.Request.ContentLength > maxBody)
        {
            if (await Violation(context, mode, "920350", $"Тело запроса {context.Request.ContentLength} байт > лимита {maxBody}", StatusCodes.Status413PayloadTooLarge))
            {
                return;
            }
        }

        // 2. Сбор проверяемых значений: путь, параметры строки запроса, значения полей JSON
        var values = new List<string> { Uri.UnescapeDataString(context.Request.Path.Value ?? "") };
        foreach (var (_, v) in context.Request.Query)
        {
            values.AddRange(v.Where(s => s is not null)!);
        }

        if (context.Request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true && context.Request.ContentLength is > 0)
        {
            context.Request.EnableBuffering();
            try
            {
                using var doc = await JsonDocument.ParseAsync(context.Request.Body);
                CollectStrings(doc.RootElement, values);
            }
            catch (JsonException)
            {
                // некорректный JSON отклонит сам API (400) — WAF его не блокирует
            }
            finally
            {
                context.Request.Body.Position = 0; // тело остаётся доступным для API
            }
        }

        // 3. Проверка правил
        foreach (string value in values)
        {
            foreach (var rule in Rules)
            {
                bool hit;
                try
                {
                    hit = rule.Pattern.IsMatch(value);
                }
                catch (RegexMatchTimeoutException)
                {
                    hit = true; // защита от ReDoS: подозрительно «тяжёлое» значение
                }

                if (hit && await Violation(context, mode, rule.Id, rule.Description, StatusCodes.Status403Forbidden))
                {
                    return;
                }
            }
        }

        await next(context);
    }

    /// <summary>Журналирует срабатывание; в режиме On блокирует запрос. Возвращает true, если запрос заблокирован.</summary>
    private async Task<bool> Violation(HttpContext context, WafMode mode, string ruleId, string description, int status)
    {
        string traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
        if (mode == WafMode.DetectionOnly)
        {
            logger.LogWarning("WAF [DetectionOnly] правило {RuleId} «{Description}» сработало на {Method} {Path} — запрос ПРОПУЩЕН, trace {TraceId}",
                ruleId, description, context.Request.Method, context.Request.Path, traceId);
            return false;
        }

        logger.LogWarning("WAF [On] правило {RuleId} «{Description}» — запрос {Method} {Path} ЗАБЛОКИРОВАН ({Status}), trace {TraceId}",
            ruleId, description, context.Request.Method, context.Request.Path, status, traceId);
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { error = "Request blocked by WAF", ruleId, traceId });
        return true;
    }

    private static void CollectStrings(JsonElement element, List<string> values)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    values.Add(property.Name);
                    CollectStrings(property.Value, values);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectStrings(item, values);
                }
                break;
            case JsonValueKind.String:
                values.Add(element.GetString() ?? "");
                break;
        }
    }
}
