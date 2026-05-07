using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

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

internal static class EddPipeline
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

    private const string ExtractionSystemPrompt =
        """
        You map user messages to parameters for an Estimated Delivery Date (EDD) API.

        Output ONLY valid JSON (no markdown fences).

        If the user is NOT asking for a calculated delivery date / EDD / transit arrival estimate, output exactly:
        {"notAnEddRequest":true}

        If they want an EDD but required fields are missing, output:
        {"incomplete":true,"missing":["origin"],"hint":"short question to ask the user"}

        Required for a full calculation: origin, destination, mode, serviceType, pickupDate.
        Optional: country (ISO e.g. NZ), carrier (string), freightPayer (string), isRural (boolean).

        Use depot/site codes when possible (e.g. AUCK, CHCH). mode examples: Transit. serviceType examples: Standard, Express.
        pickupDate MUST be ISO 8601 UTC string (e.g. 2026-05-07T17:00:00.000Z).

        When you have all required fields, output:
        {"origin":"...","destination":"...","mode":"...","serviceType":"...","pickupDate":"...","country":"NZ","carrier":"","freightPayer":"","isRural":false}

        Use empty strings for unknown optional strings; default isRural to false if unsure.
        """;

    public static string BuildConversationSnippet(IReadOnlyList<ChatMessage> messages)
    {
        var lines = messages.TakeLast(12).Select(m => $"{m.Role}: {m.Content}");
        return string.Join("\n", lines);
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
        var userBlock = BuildConversationSnippet(messages);
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

        // RoundtripKind cannot be combined with AssumeUniversal (throws ArgumentException).
        if (!DateTime.TryParse(parsed.PickupDate, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var pickupUtc)
            && !DateTime.TryParse(parsed.PickupDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out pickupUtc))
        {
            return new EddExtractOutcome.Incomplete(
                $"I could not read pickup date \"{parsed.PickupDate}\". Use an ISO datetime (e.g. 2026-05-07T17:00:00Z).");
        }

        var dto = new EddCalculateRequestDto(
            parsed.Origin.Trim(),
            parsed.Destination.Trim(),
            parsed.Mode.Trim(),
            parsed.ServiceType.Trim(),
            pickupUtc,
            string.IsNullOrWhiteSpace(parsed.Country) ? null : parsed.Country.Trim(),
            string.IsNullOrWhiteSpace(parsed.Carrier) ? null : parsed.Carrier.Trim(),
            parsed.IsRural ?? false,
            string.IsNullOrWhiteSpace(parsed.FreightPayer) ? null : parsed.FreightPayer.Trim());

        return new EddExtractOutcome.Ok(dto);
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
