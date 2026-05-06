using System.Text;
using System.Text.Json;
using EDD.API.Models;

namespace EDD.API.Supabase;

/// <summary>
/// Data access via Supabase PostgREST (<c>{SUPABASE_URL}/rest/v1</c>) using <c>SUPABASE_URL</c> + <c>SUPABASE_KEY</c>.
/// Same data path as the official Supabase C# <c>Client</c> (PostgREST); no Postgres connection string required.
/// Use the <b>service_role</b> key only on trusted servers, or relax RLS / use <b>anon</b> + policies for reads/writes.
/// </summary>
public sealed class SupabaseDataClient
{
    /// <summary>PostgREST returns snake_case keys; Supabase tables use snake_case columns.</summary>
    private static readonly JsonSerializerOptions JsonRead = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private static readonly JsonSerializerOptions JsonWrite = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly HttpClient _http;

    public SupabaseDataClient(HttpClient http) => _http = http;

    public async Task<TransitTime?> FindTransitTimeAsync(
        string origin,
        string destination,
        string mode,
        string serviceType,
        CancellationToken ct)
    {
        var q = string.Join('&', new[]
        {
            Eq("origin", origin),
            Eq("destination", destination),
            Eq("mode", mode),
            Eq("service_type", serviceType),
            "limit=1"
        });
        using var resp = await _http.GetAsync($"transit_times?{q}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var rows = await JsonSerializer.DeserializeAsync<List<TransitTime>>(stream, JsonRead, ct);
        return rows?.FirstOrDefault();
    }

    public async Task<IReadOnlyList<EddRule>> GetActiveRulesOrderedAsync(CancellationToken ct)
    {
        const string q = "is_active=eq.true&order=priority.asc";
        using var resp = await _http.GetAsync($"edd_rules?{q}", ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var rows = await JsonSerializer.DeserializeAsync<List<EddRule>>(stream, JsonRead, ct);
        return rows ?? [];
    }

    public async Task<IReadOnlyList<DateOnly>> GetHolidayDatesForCountryAsync(string country, CancellationToken ct)
    {
        var q = $"{Eq("country", country)}&select=holiday_date";
        using var resp = await _http.GetAsync($"holidays?{q}", ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var rows = await JsonSerializer.DeserializeAsync<List<HolidayDateRow>>(stream, JsonRead, ct);
        return rows?.Select(r => r.HolidayDate).ToList() ?? [];
    }

    private sealed class HolidayDateRow
    {
        public DateOnly HolidayDate { get; set; }
    }

    public async Task InsertEddLogAsync(EddLog log, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new[] { log }, JsonWrite);
        using var req = new HttpRequestMessage(HttpMethod.Post, "edd_logs")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("Prefer", "return=minimal");
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }

    private static string Eq(string column, string value) => $"{column}=eq.{Uri.EscapeDataString(value)}";
}
