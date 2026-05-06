using System.Globalization;
using EDD.API.Supabase;

namespace EDD.API.Services;

/// <summary>
/// Result of advancing by N working days: final instant, count of calendar non-working days stepped over, and one message per step.
/// </summary>
public sealed record WorkingDaySpanResult(
    DateTime End,
    int NonWorkingDaysCrossed,
    IReadOnlyList<string> Notes);

/// <summary>
/// Working-day logic (weekends + country holidays from Supabase). Holidays are loaded once per call.
/// </summary>
public sealed class CalendarService(SupabaseDataClient db)
{
    public async Task<DateTime> AdjustToWorkingDay(DateTime date, string country, CancellationToken cancellationToken = default)
    {
        var holidays = await LoadHolidaysAsync(country, cancellationToken);

        while (IsNonWorkingDay(date, holidays))
            date = date.AddDays(1);

        return date;
    }

    public async Task<DateTime> AddWorkingDays(DateTime startDate, int days, string country, CancellationToken cancellationToken = default)
    {
        var r = await AddWorkingDaysWithNotesAsync(startDate, days, country, cancellationToken);
        return r.End;
    }


    /// <summary>Adds <paramref name="days"/> working days, recording each calendar Sat/Sun/holiday that does not count.</summary>
    public async Task<WorkingDaySpanResult> AddWorkingDaysWithNotesAsync(
        DateTime startDate,
        int days,
        string country,
        CancellationToken cancellationToken = default)
    {
        var holidays = await LoadHolidaysAsync(country, cancellationToken);
        var date = startDate;
        var notes = new List<string>();
        var skipped = 0;

        while (days > 0)
        {
            date = date.AddDays(1);

            if (!IsNonWorkingDay(date, holidays))
            {
                days--;
                continue;
            }

            skipped++;
            notes.Add(DescribeNonWorkingDay(date, holidays));
        }

        return new WorkingDaySpanResult(date, skipped, notes);
    }

    /// <summary>Human-readable explanation for why <paramref name="date"/> is non-working (matches final adjustment wording).</summary>
    public static string DescribeNonWorkingDay(DateTime date, HashSet<DateOnly> holidays)
    {
        var day = DateOnly.FromDateTime(date.Date);
        var weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        var holiday = holidays.Contains(day);

        if (weekend && holiday)
        {
            return $"Non-working day: weekend and public holiday ({day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)})";
        }

        if (weekend)
            return $"Non-working day: weekend ({date.DayOfWeek})";

        return $"Non-working day: public holiday ({day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)})";
    }

    private async Task<HashSet<DateOnly>> LoadHolidaysAsync(string country, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(country))
            return [];

        var dates = await db.GetHolidayDatesForCountryAsync(country.Trim(), cancellationToken);
        return dates.ToHashSet();
    }

    private static bool IsNonWorkingDay(DateTime date, HashSet<DateOnly> holidays)
    {
        if (date.DayOfWeek == DayOfWeek.Saturday ||
            date.DayOfWeek == DayOfWeek.Sunday)
            return true;

        return holidays.Contains(DateOnly.FromDateTime(date.Date));
    }
}
