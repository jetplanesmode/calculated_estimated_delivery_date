using EDD.API.Models;
using EDD.API.Models.Request;
using EDD.API.Models.Response;
using EDD.API.Supabase;

namespace EDD.API.Services;

public interface IEddCalculationService
{
    Task<CalculateEddResponse?> CalculateAsync(CalculateEddRequest request, CancellationToken ct = default);
}

/// <summary>
/// Base transit from DB (working days), data-driven <see cref="RuleEngine"/> on <c>rule_json</c> (<c>add_days</c> = working days),
/// then weekend/holiday roll-forward if landing date is still non-working, then log.
/// </summary>
public sealed class EddCalculationService(SupabaseDataClient db, RuleEngine engine, CalendarService calendar)
    : IEddCalculationService
{
    public async Task<CalculateEddResponse?> CalculateAsync(CalculateEddRequest request, CancellationToken ct = default)
    {
        var transit = await db.FindTransitTimeAsync(
            request.Origin,
            request.Destination,
            request.Mode,
            request.ServiceType,
            ct);

        if (transit is null)
            return null;

        var rules = await db.GetActiveRulesOrderedAsync(ct);

        var country = request.Country?.Trim() ?? "";
        var transitSpan =
            await calendar.AddWorkingDaysWithNotesAsync(request.PickupDate, transit.TransitDays, country, ct);
        var eddAfterTransit = transitSpan.End;

        var (eddAfterRules, appliedRuleNames, ruleAddedDays, rulesWalkSkipped, rulesWalkNotes) =
            await engine.ApplyRulesAsync(
                eddAfterTransit,
                request,
                rules,
                async (start, days, c) => await calendar.AddWorkingDaysWithNotesAsync(start, days, country, c),
                ct);

        var totalCalendar = transit.TransitDays + ruleAddedDays;

        HashSet<DateOnly> holidayDates = new();
        if (!string.IsNullOrWhiteSpace(request.Country))
        {
            var dates = await db.GetHolidayDatesForCountryAsync(request.Country.Trim(), ct);
            holidayDates = dates.ToHashSet();
        }

        var (adjusted, nonDeliverySkipped, adjustmentNotes) =
            AdjustCandidateForNonDeliveryDays(eddAfterRules, holidayDates);

        var appliedRules = new List<string>(appliedRuleNames);
        appliedRules.AddRange(transitSpan.Notes);
        appliedRules.AddRange(rulesWalkNotes);
        appliedRules.AddRange(adjustmentNotes);

        var nonDeliveryTotal =
            transitSpan.NonWorkingDaysCrossed + rulesWalkSkipped + nonDeliverySkipped;

        var log = new EddLog
        {
            Id = Guid.NewGuid(),
            Origin = request.Origin,
            Destination = request.Destination,
            Mode = request.Mode,
            ServiceType = request.ServiceType,
            PickupDate = request.PickupDate,
            CalculatedEdd = adjusted,
            CreatedAt = DateTime.UtcNow,
            AppliedRules = appliedRules,
            NonDeliveryDaysSkipped = nonDeliveryTotal
        };
        await db.InsertEddLogAsync(log, ct);

        return new CalculateEddResponse(
            adjusted,
            transit.TransitDays,
            ruleAddedDays,
            totalCalendar,
            appliedRules,
            nonDeliveryTotal);
    }

    private static (DateTime Adjusted, int DaysSkipped, List<string> Notes) AdjustCandidateForNonDeliveryDays(
        DateTime candidateEdd,
        HashSet<DateOnly> holidays)
    {
        var datePart = candidateEdd.Date;
        var notes = new List<string>();
        var daysSkipped = 0;

        while (IsWeekend(datePart) || holidays.Contains(DateOnly.FromDateTime(datePart)))
        {
            daysSkipped++;
            notes.Add(CalendarService.DescribeNonWorkingDay(datePart, holidays));

            datePart = datePart.AddDays(1);
        }

        return (datePart.Add(candidateEdd.TimeOfDay), daysSkipped, notes);
    }

    private static bool IsWeekend(DateTime d) =>
        d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
}
