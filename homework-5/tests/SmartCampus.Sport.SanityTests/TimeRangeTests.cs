using SmartCampus.Sport.Domain;
using Xunit;

namespace SmartCampus.Sport.SanityTests;

/// <summary>
/// Модульные тесты разбора TimeSlot -> TimeRange. Выполняются в CI на каждый PR
/// (в отличие от sanity-теста не требуют окружения).
/// </summary>
[Trait("Category", "Unit")]
public sealed class TimeRangeTests
{
    [Theory]
    [InlineData("18:00-19:30", "18:00", "19:30")]
    [InlineData("9:00-10:00", "09:00", "10:00")]
    [InlineData("18.00 - 19.30", "18:00", "19:30")]
    [InlineData(" 07:00–08:30 ", "07:00", "08:30")]
    public void TryParseLegacy_ValidFormats_AreParsed(string slot, string start, string end)
    {
        Assert.True(TimeRange.TryParseLegacy(slot, out var range));
        Assert.Equal(start, range.StartText);
        Assert.Equal(end, range.EndText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("весь день")]
    [InlineData("19:00-18:00")]
    [InlineData("25:00-26:00")]
    [InlineData("18:00-18:00")]
    public void TryParseLegacy_InvalidValues_ReturnFalse(string? slot)
    {
        Assert.False(TimeRange.TryParseLegacy(slot, out _));
    }

    [Fact]
    public void TryParseLegacy_Null_ReturnsFalse()
    {
        Assert.False(TimeRange.TryParseLegacy(null, out _));
    }

    [Fact]
    public void Overlaps_AdjacentSlots_DoNotOverlap()
    {
        var first = TimeRange.ParseLegacy("18:00-19:30");
        var second = TimeRange.ParseLegacy("19:30-21:00");

        Assert.False(first.Overlaps(second));
        Assert.True(first.Overlaps(TimeRange.ParseLegacy("19:00-20:00")));
    }

    [Fact]
    public void Conversions_AreCompatibleWithPostgresAndLegacyFormat()
    {
        var range = TimeRange.ParseLegacy("18.00 - 19.30");

        Assert.Equal("18:00-19:30", range.ToLegacyString());
        Assert.Equal("[18:00,19:30)", range.ToPostgresLiteral());
        Assert.Equal(TimeSpan.FromMinutes(90), range.Duration);
    }

    [Fact]
    public void Rules_RejectBookingOutsideOpeningHours()
    {
        var today = new DateOnly(2026, 10, 23);
        var night = TimeRange.ParseLegacy("23:00-23:30");

        Assert.NotNull(BookingRules.Validate(night, today, today));
        Assert.Null(BookingRules.Validate(TimeRange.ParseLegacy("18:00-19:30"), today, today));
    }
}
