using System.Globalization;
using System.Text.Json;
using EDD.API.Models;
using EDD.API.Models.Dtos;
using EDD.API.Models.Request;

namespace EDD.API.Services;

/// <summary>
/// Evaluates <see cref="RuleDefinition"/> JSON per rule (priority handled by caller ordering).
/// </summary>
public sealed class RuleEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public async Task<(DateTime Edd, List<string> AppliedRuleNames, int DaysAddedByActions, int NonWorkingDaysCrossed, List<string> WorkingDayWalkNotes)> ApplyRulesAsync(
        DateTime eddAfterTransit,
        CalculateEddRequest request,
        IReadOnlyList<EddRule> rulesOrdered,
        Func<DateTime, int, CancellationToken, Task<WorkingDaySpanResult>> addWorkingDaysWithNotesAsync,
        CancellationToken ct)
    {
        var applied = new List<string>();
        var daysAdded = 0;
        var edd = eddAfterTransit;
        var walkSkipped = 0;
        var walkNotes = new List<string>();

        foreach (var rule in rulesOrdered.OrderBy(r => r.Priority))
        {
            RuleDefinition? def;
            try
            {
                var json = rule.RuleJson.GetRawText();
                if (string.IsNullOrWhiteSpace(json) || json == "{}")
                    continue;
                def = JsonSerializer.Deserialize<RuleDefinition>(json, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (def?.Actions is null || def.Actions.Count == 0)
                continue;

            if (!Evaluate(def.Conditions, request))
                continue;

            var (next, added, skipped, notes) =
                await ExecuteAsync(def.Actions, edd, addWorkingDaysWithNotesAsync, ct);
            edd = next;
            daysAdded += added;
            walkSkipped += skipped;
            walkNotes.AddRange(notes);
            applied.Add(rule.Name);
        }

        return (edd, applied, daysAdded, walkSkipped, walkNotes);
    }

    private static bool Evaluate(List<Condition>? conditions, CalculateEddRequest req)
    {
        if (conditions is null || conditions.Count == 0)
            return true;

        foreach (var c in conditions)
        {
            var left = GetFieldValue(req, c.Field);
            if (!Compare(left, c.Op, c.Value))
                return false;
        }

        return true;
    }

    private static object? GetFieldValue(CalculateEddRequest req, string field)
    {
        return field.Trim().ToLowerInvariant() switch
        {
            "origin" => req.Origin,
            "destination" => req.Destination,
            "mode" => req.Mode,
            "servicetype" or "service_type" => req.ServiceType,
            "pickuptime" or "pickup_time" => FormatTimeHm(req.PickupDate),
            "month" => req.PickupDate.Month.ToString(CultureInfo.InvariantCulture),
            "dayofweek" or "day_of_week" => ((int)req.PickupDate.DayOfWeek).ToString(CultureInfo.InvariantCulture),
            "carrier" => req.Carrier ?? "",
            "isrural" or "is_rural" => req.IsRural ? "true" : "false",
            "freightpayer" or "freight_payer" => req.FreightPayer ?? "",
            _ => null
        };
    }

    private static string FormatTimeHm(DateTime dt)
    {
        var utc = dt.Kind switch
        {
            DateTimeKind.Utc => dt,
            DateTimeKind.Local => dt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc)
        };
        return utc.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private static bool Compare(object? left, string op, JsonElement? rightEl)
    {
        var opNorm = op.Trim().ToLowerInvariant();
        var l = left?.ToString();

        switch (opNorm)
        {
            case "equals":
            case "eq":
                if (!rightEl.HasValue || rightEl.Value.ValueKind == JsonValueKind.Null)
                    return l is null;
                return l == ScalarToString(rightEl.Value);

            case "greater_than":
            case "gt":
                if (!rightEl.HasValue)
                    return false;
                return CompareGreaterThan(l, rightEl.Value);

            case "in":
                if (!rightEl.HasValue || rightEl.Value.ValueKind != JsonValueKind.Array)
                    return false;
                foreach (var item in rightEl.Value.EnumerateArray())
                {
                    if (ScalarToString(item) == l)
                        return true;
                }

                return false;

            default:
                return false;
        }
    }

    private static bool CompareGreaterThan(string? left, JsonElement right)
    {
        var r = ScalarToString(right);
        if (string.IsNullOrEmpty(left))
            return false;

        if (IsHm(left) && IsHm(r))
        {
            if (TryParseHm(left, out var tl) && TryParseHm(r, out var tr))
                return tl > tr;
        }

        if (decimal.TryParse(left, NumberStyles.Any, CultureInfo.InvariantCulture, out var dl)
            && decimal.TryParse(r, NumberStyles.Any, CultureInfo.InvariantCulture, out var dr))
            return dl > dr;

        return string.Compare(left, r, StringComparison.Ordinal) > 0;
    }

    private static bool IsHm(string s) => s.Length >= 4 && s.Contains(':', StringComparison.Ordinal);

    private static bool TryParseHm(string s, out TimeSpan t)
    {
        t = default;
        var parts = s.Split(':');
        if (parts.Length < 2)
            return false;
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var h))
            return false;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var m))
            return false;
        if (h is < 0 or > 23 || m is < 0 or > 59)
            return false;
        t = new TimeSpan(h, m, 0);
        return true;
    }

    private static string ScalarToString(JsonElement el) =>
        el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? "",
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => el.GetRawText()
        };

    private static async Task<(DateTime Edd, int DaysAdded, int NonWorkingSkipped, List<string> Notes)> ExecuteAsync(
        List<ActionRule> actions,
        DateTime edd,
        Func<DateTime, int, CancellationToken, Task<WorkingDaySpanResult>> addWorkingDaysWithNotesAsync,
        CancellationToken ct)
    {
        var added = 0;
        var skipped = 0;
        var notes = new List<string>();
        foreach (var a in actions)
        {
            var type = a.Type.Trim().ToLowerInvariant();
            switch (type)
            {
                case "add_days":
                    var span = await addWorkingDaysWithNotesAsync(edd, a.Value, ct);
                    edd = span.End;
                    skipped += span.NonWorkingDaysCrossed;
                    notes.AddRange(span.Notes);
                    added += a.Value;
                    break;
            }
        }

        return (edd, added, skipped, notes);
    }
}
