using System.Collections.Frozen;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EDD.Agent;

/// <summary>Matches <c>POST /edd/calculate</c> JSON body (camelCase).</summary>
internal sealed record EddCalculateRequestDto(
    string Origin,
    string Destination,
    string Mode,
    string ServiceType,
    DateTime PickupDate,
    string? Country,
    string? Carrier,
    bool IsRural,
    string? FreightPayer);

/// <summary>Matches EDD.API calculate response JSON (camelCase).</summary>
internal sealed record EddCalculateResponseDto(
    [property: JsonPropertyName("calculatedEdd")] DateTime CalculatedEdd,
    [property: JsonPropertyName("baseTransitDays")] int BaseTransitDays,
    [property: JsonPropertyName("ruleAddedDays")] int RuleAddedDays,
    [property: JsonPropertyName("totalCalendarDaysBeforeAdjustment")] int TotalCalendarDaysBeforeAdjustment,
    [property: JsonPropertyName("appliedRules")] IReadOnlyList<string>? AppliedRules,
    [property: JsonPropertyName("nonDeliveryDaysSkipped")] int NonDeliveryDaysSkipped);

internal sealed class EddExtractionDto
{
    [JsonPropertyName("notAnEddRequest")]
    public bool NotAnEddRequest { get; init; }

    [JsonPropertyName("incomplete")]
    public bool Incomplete { get; init; }

    [JsonPropertyName("missing")]
    public List<string>? Missing { get; init; }

    [JsonPropertyName("hint")]
    public string? Hint { get; init; }

    [JsonPropertyName("origin")]
    public string? Origin { get; init; }

    [JsonPropertyName("destination")]
    public string? Destination { get; init; }

    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    [JsonPropertyName("serviceType")]
    public string? ServiceType { get; init; }

    [JsonPropertyName("pickupDate")]
    public string? PickupDate { get; init; }

    [JsonPropertyName("country")]
    public string? Country { get; init; }

    [JsonPropertyName("carrier")]
    public string? Carrier { get; init; }

    [JsonPropertyName("isRural")]
    public bool? IsRural { get; init; }

    [JsonPropertyName("freightPayer")]
    public string? FreightPayer { get; init; }
}

internal static partial class EddPipeline
{
    private static readonly JsonSerializerOptions CamelJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions DeserializeApi = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Loaded from <c>wwwroot/system-prompt/ExtractionSystemPrompt.txt</c> at startup.</summary>
    private static readonly string ExtractionSystemPrompt = LoadSystemPromptFile(nameof(ExtractionSystemPrompt));

    /// <summary>Loaded from <c>wwwroot/system-prompt/ExtractionUserEnvelope.txt</c> at startup.</summary>
    private static readonly string ExtractionUserEnvelope = LoadSystemPromptFile(nameof(ExtractionUserEnvelope));

    /// <summary>Reads a file under <c>wwwroot/</c> by walking up from <see cref="AppContext.BaseDirectory"/> (same resolution as <c>dotnet run</c> / publish).</summary>
    /// <param name="relativeUnderWwwroot">e.g. <c>system-prompt/ExtractionSystemPrompt.txt</c> or <c>lookup/DepotMasterRows.txt</c></param>
    private static string ReadWwwrootFile(string relativeUnderWwwroot)
    {
        var normalized = relativeUnderWwwroot.Replace('/', Path.DirectorySeparatorChar);
        for (var cur = (string?)AppContext.BaseDirectory; cur != null; cur = Directory.GetParent(cur)?.FullName)
        {
            var path = Path.Combine(cur, "wwwroot", normalized);
            if (File.Exists(path))
                return File.ReadAllText(path);
        }

        throw new InvalidOperationException(
            $"Missing wwwroot file: {normalized}. Searched upward from {AppContext.BaseDirectory}.");
    }

    private static string LoadSystemPromptFile(string baseName) =>
        ReadWwwrootFile(Path.Combine("system-prompt", $"{baseName}.txt"));

    /// <summary>Parses <c>wwwroot/lookup/DepotMasterRows.txt</c> (tab-separated LongText, ShortText).</summary>
    private static (string LongText, string ShortText)[] LoadDepotMasterRows()
    {
        var text = ReadWwwrootFile(Path.Combine("lookup", "DepotMasterRows.txt"));
        var list = new List<(string LongText, string ShortText)>();
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.None))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            var tabIdx = line.IndexOf('\t');
            if (tabIdx < 0)
                continue;

            var longText = line[..tabIdx].Trim();
            var shortText = line[(tabIdx + 1)..].Trim();
            if (longText.Length == 0 || shortText.Length == 0)
                continue;

            if (longText.Equals("LongText", StringComparison.OrdinalIgnoreCase)
                && shortText.Equals("ShortText", StringComparison.OrdinalIgnoreCase))
                continue;

            list.Add((longText, shortText));
        }

        if (list.Count == 0)
            throw new InvalidOperationException("lookup/DepotMasterRows.txt contains no data rows.");

        return list.ToArray();
    }

    private static string BuildExtractionReferencePreamble()
    {
        var utcNow = DateTime.UtcNow;
        var utcToday = utcNow.Date;
        var sb = new StringBuilder();
        sb.AppendLine("Reference dates for expanding \"today\", \"tomorrow\", or similar relative wording (use only these lines — not your model training date):");
        if (TryPacificAucklandTimeZone() is { } nz)
        {
            var nzToday = TimeZoneInfo.ConvertTimeFromUtc(utcNow, nz).Date;
            sb.AppendLine($"- \"Today\" in Pacific/Auckland: {nzToday:yyyy-MM-dd}");
            sb.AppendLine($"- \"Tomorrow\" in Pacific/Auckland: {nzToday.AddDays(1):yyyy-MM-dd}");
        }

        sb.AppendLine($"- \"Today\" as UTC calendar date (e.g. user clearly implies UTC or non-NZ with no local zone): {utcToday:yyyy-MM-dd}");
        sb.AppendLine($"- \"Tomorrow\" (UTC calendar): {utcToday.AddDays(1):yyyy-MM-dd}");
        sb.AppendLine($"Current instant (UTC): {utcNow:yyyy-MM-ddTHH:mm:ss.fffZ}");
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>
    /// Text sent to the extraction model. Only user messages are included so prior assistant EDD summaries
    /// are not treated as hidden parameters (which previously caused repeat calculations for incomplete follow-ups).
    /// </summary>
    public static string BuildConversationSnippet(IReadOnlyList<ChatMessage> messages)
    {
        var userTurns = messages
            .Where(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
            .TakeLast(12)
            .Select(static m => m.Content.Trim());
        return string.Join("\n\n", userTurns);
    }

    /// <summary>
    /// Calls the chat model once to get extraction JSON, then builds <see cref="EddCalculateRequestDto"/> or a skip/incomplete outcome.
    /// </summary>
    public static async Task<EddExtractOutcome> TryExtractAndBuildRequestAsync(
        HttpClient chatClient,
        string model,
        string? apiKey,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken ct)
    {
        var snippet = BuildConversationSnippet(messages);
        var userBlock = BuildExtractionReferencePreamble()
            + (string.IsNullOrEmpty(snippet)
                ? ExtractionUserEnvelope + "(no user message content)"
                : ExtractionUserEnvelope + snippet);
        var root = new JsonObject
        {
            ["model"] = model,
            ["temperature"] = 0.1,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = ExtractionSystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userBlock },
            },
            ["response_format"] = JsonNode.Parse("""{"type":"json_object"}"""),
        };

        string body;
        try
        {
            using var httpReq = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
            {
                Content = new StringContent(root.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrEmpty(apiKey))
                httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await chatClient.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct);
            body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return new EddExtractOutcome.EddExtractFailed($"Extraction model error HTTP {(int)response.StatusCode}: {body[..Math.Min(500, body.Length)]}");
        }
        catch (HttpRequestException ex)
        {
            return new EddExtractOutcome.EddExtractFailed($"Cannot reach chat model for extraction: {ex.Message}");
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            return new EddExtractOutcome.EddExtractFailed($"Extraction timed out: {ex.Message}");
        }

        string? content;
        try
        {
            using var doc = JsonDocument.Parse(body);
            content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        }
        catch (Exception ex)
        {
            return new EddExtractOutcome.EddExtractFailed($"Could not parse extraction response: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(content))
            return new EddExtractOutcome.EddExtractFailed("Empty extraction content.");

        EddExtractionDto? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<EddExtractionDto>(content, DeserializeApi);
        }
        catch (JsonException jx)
        {
            return new EddExtractOutcome.EddExtractFailed($"Invalid extraction JSON: {jx.Message}");
        }

        if (parsed is null)
            return new EddExtractOutcome.EddExtractFailed("Extraction deserialized to null.");

        if (parsed.NotAnEddRequest)
            return new EddExtractOutcome.NotEddRequest();

        if (parsed.Incomplete)
        {
            var miss = parsed.Missing is { Count: > 0 } ? string.Join(", ", parsed.Missing) : "details";
            var hint = string.IsNullOrWhiteSpace(parsed.Hint)
                ? $"Please provide: {miss}."
                : parsed.Hint.Trim();
            return new EddExtractOutcome.Incomplete(hint);
        }

        if (string.IsNullOrWhiteSpace(parsed.Origin)
            || string.IsNullOrWhiteSpace(parsed.Destination)
            || string.IsNullOrWhiteSpace(parsed.Mode)
            || string.IsNullOrWhiteSpace(parsed.ServiceType)
            || string.IsNullOrWhiteSpace(parsed.PickupDate))
        {
            return new EddExtractOutcome.Incomplete(
                "I need origin, destination, mode, service type, and pickup date/time to calculate EDD.");
        }

        if (!TryParsePickupToUtc(parsed.PickupDate, parsed.Country, out var pickupUtc))
        {
            return new EddExtractOutcome.Incomplete(
                $"I could not read pickup date \"{parsed.PickupDate}\". Try a clear date and time (e.g. today at 5 PM, May 9, 2026 7:00 PM, or 2026-05-09T07:00:00Z).");
        }

        var origin = NormalizeDepotLabel(parsed.Origin);
        var destination = NormalizeDepotLabel(parsed.Destination);
        var mode = NormalizeLaneToken(parsed.Mode);
        var serviceType = NormalizeLaneToken(parsed.ServiceType);

        var dto = new EddCalculateRequestDto(
            origin,
            destination,
            mode,
            serviceType,
            pickupUtc,
            string.IsNullOrWhiteSpace(parsed.Country) ? null : parsed.Country.Trim(),
            string.IsNullOrWhiteSpace(parsed.Carrier) ? null : parsed.Carrier.Trim(),
            parsed.IsRural ?? false,
            string.IsNullOrWhiteSpace(parsed.FreightPayer) ? null : parsed.FreightPayer.Trim());

        return new EddExtractOutcome.Ok(dto);
    }

    /// <summary>Loaded from <c>wwwroot/lookup/DepotMasterRows.txt</c>. Must initialize before <see cref="DepotShortCodes"/> / <see cref="DepotLongTextToCode"/>.</summary>
    private static readonly (string LongText, string ShortText)[] DepotMasterRows = LoadDepotMasterRows();

    /// <summary>All known depot ShortText codes (identity when the user already types a code).</summary>
    private static readonly FrozenSet<string> DepotShortCodes = BuildDepotShortCodes();

    /// <summary>LongText keys (spaced + compact) → ShortText only. Short codes are handled separately.</summary>
    private static readonly FrozenDictionary<string, string> DepotLongTextToCode = BuildDepotLongTextLookup();

    private static FrozenSet<string> BuildDepotShortCodes()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, shortText) in DepotMasterRows)
            set.Add(shortText.Trim().ToUpperInvariant());
        return set.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    private static FrozenDictionary<string, string> BuildDepotLongTextLookup()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (longText, shortText) in DepotMasterRows)
        {
            var code = shortText.Trim().ToUpperInvariant();
            var spaced = Regex.Replace(longText.Trim(), @"\s+", " ");
            var compactLetters = string.Concat(spaced.Where(static c => !char.IsWhiteSpace(c)));

            d[spaced.ToUpperInvariant()] = code;
            if (compactLetters.Length > 0)
                d[compactLetters.ToUpperInvariant()] = code;
        }

        return d.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// EDD.API uses exact PostgREST <c>eq</c> on <c>transit_times</c>; rows use ShortText depot codes.
    /// If the user already sent a ShortText code, keep it (canonical uppercase only). If they sent LongText, map to ShortText.
    /// </summary>
    private static string NormalizeDepotLabel(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        var t = raw.Trim();
        var spaced = Regex.Replace(t, @"\s+", " ");
        var compactLetters = string.Concat(spaced.Where(static c => !char.IsWhiteSpace(c)));
        if (compactLetters.Length == 0) return t;

        // Single-token ShortText: no long-name remapping (already a depot code).
        if (!spaced.Contains(' ', StringComparison.Ordinal) && DepotShortCodes.Contains(compactLetters))
            return compactLetters.ToUpperInvariant();

        if (DepotLongTextToCode.TryGetValue(spaced, out var code))
            return code;
        if (DepotLongTextToCode.TryGetValue(compactLetters, out code))
            return code;

        return t;
    }

    /// <summary>Title-case mode and service so values match DB casing (e.g. Transit, Standard).</summary>
    private static string NormalizeLaneToken(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        var t = Regex.Replace(raw.Trim(), @"\s+", " ");
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(t.ToLowerInvariant());
    }

    /// <summary>Replaces whole-word today/tomorrow (not possessive forms like today's) with yyyy-MM-dd using NZ or UTC calendar per country hint.</summary>
    private static string ExpandRelativeCalendarWords(string raw, string? countryHint)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 512)
            return raw;

        var today = PreferNzCalendar(countryHint) && TryPacificAucklandTimeZone() is { } nz
            ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, nz).Date
            : DateTime.UtcNow.Date;
        var tomorrow = today.AddDays(1);
        var todayStr = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var tomorrowStr = tomorrow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // (?!') avoids turning "today's" into "2024-01-01's"
        var s = RelativeTodayRegex().Replace(raw, todayStr);
        s = RelativeTomorrowRegex().Replace(s, tomorrowStr);
        return s;
    }

    [GeneratedRegex(@"\b(?i:today)\b(?!')", RegexOptions.CultureInvariant)]
    private static partial Regex RelativeTodayRegex();

    [GeneratedRegex(@"\b(?i:tomorrow)\b(?!')", RegexOptions.CultureInvariant)]
    private static partial Regex RelativeTomorrowRegex();

    private static bool PreferNzCalendar(string? countryHint) =>
        string.IsNullOrWhiteSpace(countryHint)
        || countryHint.Equals("NZ", StringComparison.OrdinalIgnoreCase)
        || countryHint.Contains("New Zealand", StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses pickup from ISO or common natural-language formats; ambiguous local times default to NZ when country matches.</summary>
    private static bool TryParsePickupToUtc(string? raw, string? countryHint, out DateTime pickupUtc)
    {
        pickupUtc = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var s = ExpandRelativeCalendarWords(raw.Trim(), countryHint);

        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
        {
            pickupUtc = NormalizePickupToUtc(dt, countryHint);
            return true;
        }

        if (DateTime.TryParse(
                s,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out dt))
        {
            pickupUtc = NormalizePickupToUtc(dt, countryHint);
            return true;
        }

        foreach (var culture in new[] { CultureInfo.GetCultureInfo("en-NZ"), CultureInfo.GetCultureInfo("en-US"), CultureInfo.InvariantCulture })
        {
            if (!DateTime.TryParse(s, culture, DateTimeStyles.AllowWhiteSpaces, out dt))
                continue;
            pickupUtc = NormalizePickupToUtc(dt, countryHint);
            return true;
        }

        return false;
    }

    private static DateTime NormalizePickupToUtc(DateTime dt, string? countryHint)
    {
        if (dt.Kind == DateTimeKind.Utc)
            return dt;
        if (dt.Kind == DateTimeKind.Local)
            return dt.ToUniversalTime();

        if (PreferNzCalendar(countryHint) && TryPacificAucklandTimeZone() is { } nz)
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), nz);

        return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
    }

    private static TimeZoneInfo? TryPacificAucklandTimeZone()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById("New Zealand Standard Time");
            }
            catch (TimeZoneNotFoundException)
            {
                // fall through
            }
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
    }

    public static string SerializeRequest(EddCalculateRequestDto dto) =>
        JsonSerializer.Serialize(dto, CamelJson);

    public static EddCalculateResponseDto? TryParseApiResponse(string rawJson)
    {
        try
        {
            return JsonSerializer.Deserialize<EddCalculateResponseDto>(rawJson, DeserializeApi);
        }
        catch
        {
            return null;
        }
    }

    public static string FormatHumanReadable(EddCalculateResponseDto r)
    {
        var sb = new StringBuilder();
        var culture = CultureInfo.GetCultureInfo("en-NZ");

        sb.AppendLine("Here is your estimated delivery:");
        sb.AppendLine();
        sb.Append("**Estimated delivery (EDD):** ");
        sb.AppendLine(r.CalculatedEdd.ToUniversalTime().ToString("dddd, d MMMM yyyy 'at' HH:mm 'UTC'", culture));
        sb.AppendLine();
        sb.AppendLine($"**Base transit days:** {r.BaseTransitDays}");
        sb.AppendLine($"**Extra days from rules:** {r.RuleAddedDays}");
        sb.AppendLine($"**Working-day span before weekend/holiday adjustment:** {r.TotalCalendarDaysBeforeAdjustment} calendar days (per service rules).");
        sb.AppendLine($"**Non-delivery days skipped** (weekends/holidays moving the date forward): **{r.NonDeliveryDaysSkipped}**");
        sb.AppendLine();

        if (r.AppliedRules is { Count: > 0 })
        {
            sb.AppendLine("**Applied rules:**");
            foreach (var rule in r.AppliedRules)
                sb.AppendLine($"• {rule}");
        }
        else
        {
            sb.AppendLine("**Applied rules:** (none listed)");
        }

        return sb.ToString().TrimEnd();
    }

    public static async Task<(int StatusCode, string Body)> PostEddCalculateAsync(
        IHttpClientFactory httpFactory,
        string eddBase,
        string jsonBody,
        CancellationToken ct)
    {
        try
        {
            var client = httpFactory.CreateClient();
            var url = $"{eddBase.TrimEnd('/')}/edd/calculate";
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
            };
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            return ((int)resp.StatusCode, text);
        }
        catch (Exception ex)
        {
            return (503, JsonSerializer.Serialize(new { error = "EDD.API request failed.", detail = ex.Message }));
        }
    }
}

internal abstract record EddExtractOutcome
{
    internal sealed record Ok(EddCalculateRequestDto Request) : EddExtractOutcome;

    internal sealed record NotEddRequest : EddExtractOutcome;

    internal sealed record Incomplete(string UserMessage) : EddExtractOutcome;

    internal sealed record EddExtractFailed(string Detail) : EddExtractOutcome;
}
