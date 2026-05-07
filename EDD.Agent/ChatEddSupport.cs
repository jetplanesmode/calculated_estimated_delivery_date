namespace EDD.Agent;

internal sealed record ChatRequest(List<ChatMessage> Messages);

internal sealed record ChatMessage(string Role, string Content);

/// <param name="EddApiResponse">Raw JSON body from <c>POST /edd/calculate</c> when the structured EDD pipeline ran successfully.</param>
internal sealed record ChatResponse(string Message, string? EddApiResponse = null);

internal static class ChatEddSupport
{
    public static string NormalizeChatBaseUrl(string? baseUrl)
    {
        var u = string.IsNullOrEmpty(baseUrl) ? "https://api.openai.com/v1/" : baseUrl;
        return u.EndsWith('/') ? u : u + "/";
    }

    /// <summary>Remote hosts (Groq, OpenAI, etc.) require <c>OpenAI:ApiKey</c>; loopback Ollama typically does not.</summary>
    public static bool RequiresChatApiKey(string normalizedBaseUrl) => !IsLocalChatHost(normalizedBaseUrl);

    public static bool IsLocalChatHost(string normalizedBaseUrl)
    {
        if (!Uri.TryCreate(normalizedBaseUrl, UriKind.Absolute, out var uri))
            return false;
        return uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("[::1]", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsOpenAiOfficialHost(string normalizedBaseUrl)
    {
        if (!Uri.TryCreate(normalizedBaseUrl, UriKind.Absolute, out var uri))
            return false;
        return uri.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase);
    }

    public static string DefaultChatModel(string normalizedBaseUrl)
    {
        if (IsLocalChatHost(normalizedBaseUrl))
            return "llama3.2";
        if (IsOpenAiOfficialHost(normalizedBaseUrl))
            return "gpt-4o-mini";
        return "llama-3.3-70b-versatile";
    }

    /// <summary>
    /// When true, the agent runs the structured EDD pipeline (extract JSON → EDD.API → formatted reply) instead of normal chat.
    /// </summary>
    public static bool LooksLikeEddCalculationRequest(IReadOnlyList<ChatMessage> messages)
    {
        var text = messages.LastOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content ?? "";
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lower = text.ToLowerInvariant();

        if (lower.Contains("holiday") && !lower.Contains("edd") && !lower.Contains("estimated delivery") && !lower.Contains("delivery date"))
            return false;

        if (lower.Contains("edd"))
            return true;
        if (lower.Contains("estimated delivery"))
            return true;
        if (lower.Contains("delivery date"))
            return true;

        var hasRouteHint = lower.Contains(" from ") || lower.Contains(" to ") || lower.Contains("auck") || lower.Contains("chch")
            || lower.Contains("christchurch") || lower.Contains("auckland");

        if (hasRouteHint && (lower.Contains("transit") || lower.Contains("express") || lower.Contains("standard")))
            return true;

        return false;
    }
}
