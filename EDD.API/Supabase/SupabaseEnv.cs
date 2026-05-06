namespace EDD.API.Supabase;

/// <summary>Reads Supabase project URL and API key from the environment (same vars as the Supabase JS/C# clients).</summary>
public static class SupabaseEnv
{
    public const string UrlVariable = "SUPABASE_URL";
    public const string KeyVariable = "SUPABASE_KEY";

    public static string? GetUrl(IConfiguration configuration) =>
        Environment.GetEnvironmentVariable(UrlVariable)
        ?? configuration[UrlVariable];

    public static string? GetKey(IConfiguration configuration) =>
        Environment.GetEnvironmentVariable(KeyVariable)
        ?? configuration[KeyVariable];
}
