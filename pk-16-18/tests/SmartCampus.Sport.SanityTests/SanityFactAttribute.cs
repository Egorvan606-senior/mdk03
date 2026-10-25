using Xunit;

namespace SmartCampus.Sport.SanityTests;

/// <summary>
/// Факт, который выполняется только при заданном адресе боевого окружения.
/// В обычном CI (без переменной SANITY_BASE_URL) тест помечается как пропущенный,
/// а не падает, — production sanity запускается отдельным шагом после релиза.
/// </summary>
public sealed class SanityFactAttribute : FactAttribute
{
    public const string BaseUrlVariable = "SANITY_BASE_URL";
    public const string TokenVariable = "SANITY_TOKEN";

    public SanityFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(BaseUrlVariable)))
        {
            Skip = $"Production sanity: не задана переменная окружения {BaseUrlVariable}.";
        }
    }
}
