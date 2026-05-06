namespace EDD.API.Models.Response;

public sealed record CalculateEddResponse(
    DateTime CalculatedEdd,
    int BaseTransitDays,
    int RuleAddedDays,
    /// <summary>Transit working days + rule <c>add_days</c> sums (not naive calendar span).</summary>
    int TotalCalendarDaysBeforeAdjustment,
    IReadOnlyList<string> AppliedRules,
    /// <summary>Calendar days the candidate EDD was moved forward due to weekends and/or country holidays.</summary>
    int NonDeliveryDaysSkipped);
