using System.Net.Http.Headers;
using EDD.API.Models.Request;
using EDD.API.Services;
using EDD.API.Supabase;

var builder = WebApplication.CreateBuilder(args);

var supabaseUrl = SupabaseEnv.GetUrl(builder.Configuration);
var supabaseKey = SupabaseEnv.GetKey(builder.Configuration);
if (string.IsNullOrWhiteSpace(supabaseUrl) || string.IsNullOrWhiteSpace(supabaseKey))
    throw new InvalidOperationException(
        $"Set {SupabaseEnv.UrlVariable} and {SupabaseEnv.KeyVariable} (environment variables or configuration). " +
        "Use the Supabase project URL and API key (anon or service_role per your RLS setup). " +
        "No Postgres connection string is required; data uses PostgREST at {{SUPABASE_URL}}/rest/v1/.");

builder.Services.AddOpenApi();
builder.Services.AddSingleton<RuleEngine>();
builder.Services.AddHttpClient<SupabaseDataClient>((_, client) =>
{
    client.BaseAddress = new Uri(supabaseUrl.TrimEnd('/') + "/rest/v1/");
    client.DefaultRequestHeaders.Add("apikey", supabaseKey);
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", supabaseKey);
});
builder.Services.AddScoped<IEddCalculationService, EddCalculationService>();
builder.Services.AddScoped<CalendarService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseHttpsRedirection();

app.MapPost("/edd/calculate", async (CalculateEddRequest body, IEddCalculationService edd, CancellationToken ct) =>
    {
        var result = await edd.CalculateAsync(body, ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    })
    .WithName("CalculateEdd")
    .WithSummary("EDD: transit + JSON rule engine (conditions/actions) + weekend/holiday adjustment.");

app.Run();
