using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EDD.Agent;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();
builder.Services.AddHttpClient("chat", (sp, client) =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var baseUrl = ChatEddSupport.NormalizeChatBaseUrl(cfg["OpenAI:BaseUrl"]?.Trim());
    client.BaseAddress = new Uri(baseUrl);
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    client.Timeout = TimeSpan.FromMinutes(5);
});
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/api/chat", async (ChatRequest request, IHttpClientFactory httpFactory, IConfiguration config, CancellationToken ct) =>
{
    var baseUrl = ChatEddSupport.NormalizeChatBaseUrl(config["OpenAI:BaseUrl"]?.Trim());
    var apiKey = config["OpenAI:ApiKey"]?.Trim();
    if (ChatEddSupport.RequiresChatApiKey(baseUrl) && string.IsNullOrEmpty(apiKey))
    {
        return Results.Json(
            new
            {
                error =
                    "OpenAI:ApiKey is required for this chat host (Groq, OpenAI, etc.). Set it with: dotnet user-secrets set \"OpenAI:ApiKey\" \"YOUR_KEY\" --project EDD.Agent. For local Ollama without a key, set OpenAI:BaseUrl to http://localhost:11434/v1/",
            },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var modelRaw = config["OpenAI:Model"]?.Trim();
    var model = string.IsNullOrEmpty(modelRaw) ? ChatEddSupport.DefaultChatModel(baseUrl) : modelRaw;
    var systemMessage = config["OpenAI:SystemMessage"]?.Trim();
    foreach (var m in request.Messages)
    {
        if (m.Role is not ("user" or "assistant" or "system"))
            return Results.BadRequest(new { error = "Invalid message role." });
    }

    var eddBase = config["EddApi:BaseUrl"]?.Trim().TrimEnd('/');
    var useEddTools = !string.IsNullOrEmpty(eddBase);

    var client = httpFactory.CreateClient("chat");

    // Structured pipeline: human text → JSON payload → EDD.API → human-readable summary (no LLM tool loop).
    if (useEddTools
        && config.GetValue("OpenAI:UseStructuredEddPipeline", true)
        && ChatEddSupport.LooksLikeEddCalculationRequest(request.Messages))
    {
        var outcome = await EddPipeline.TryExtractAndBuildRequestAsync(client, model, apiKey, request.Messages, ct);
        switch (outcome)
        {
            case EddExtractOutcome.Incomplete inc:
                return Results.Json(new ChatResponse(inc.UserMessage));
            case EddExtractOutcome.Ok ok:
            {
                var payloadJson = EddPipeline.SerializeRequest(ok.Request);
                var (status, apiBody) = await EddPipeline.PostEddCalculateAsync(httpFactory, eddBase!, payloadJson, ct);
                if (status != StatusCodes.Status200OK)
                {
                    if (status == StatusCodes.Status404NotFound)
                    {
                        return Results.Json(new ChatResponse(
                            "I don't have a delivery estimate for that request right now—either it wasn't found or there's no result to show yet. You can try again in a moment or rephrase your question."));
                    }

                    var preview = apiBody.Length > 800 ? apiBody[..800] + "…" : apiBody;
                    return Results.Json(new ChatResponse(
                        $"The EDD service returned HTTP {status}. Details:\n\n{preview}"));
                }

                var calc = EddPipeline.TryParseApiResponse(apiBody);
                if (calc is null)
                {
                    var snippet = apiBody.Length > 600 ? apiBody[..600] + "…" : apiBody;
                    return Results.Json(new ChatResponse(
                        "Unexpected response from EDD.API (could not parse as calculate result). First part:\n\n" + snippet));
                }

                return Results.Json(new ChatResponse(EddPipeline.FormatHumanReadable(calc), apiBody));
            }
            case EddExtractOutcome.NotEddRequest:
            case EddExtractOutcome.EddExtractFailed:
            default:
                break;
        }
    }

    var messages = new List<OpenAiMessage>();
    if (!string.IsNullOrEmpty(systemMessage))
        messages.Add(new OpenAiMessage("system", systemMessage));
    foreach (var m in request.Messages)
        messages.Add(new OpenAiMessage(m.Role, m.Content));

    var payload = new OpenAiChatRequest(model, messages);
    using var httpReqSimple = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
    {
        Content = new StringContent(JsonSerializer.Serialize(payload, JsonContext.Default.OpenAiChatRequest), Encoding.UTF8, "application/json"),
    };
    if (!string.IsNullOrEmpty(apiKey))
        httpReqSimple.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

    string bodySimple;
    HttpResponseMessage responseSimple;
    try
    {
        responseSimple = await client.SendAsync(httpReqSimple, HttpCompletionOption.ResponseHeadersRead, ct);
        bodySimple = await responseSimple.Content.ReadAsStringAsync(ct);
    }
    catch (HttpRequestException ex)
    {
        return Results.Json(
            new
            {
                error =
                    "Cannot reach the chat model server. Start Ollama (or your configured LLM) and confirm OpenAI:BaseUrl — e.g. http://localhost:11434/v1/ after `ollama serve`.",
                detail = ex.Message,
            },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
    {
        return Results.Json(
            new { error = "Chat request timed out.", detail = ex.Message },
            statusCode: StatusCodes.Status504GatewayTimeout);
    }

    using (responseSimple)
    {
        if (!responseSimple.IsSuccessStatusCode)
            return Results.Json(new { error = "Chat model request failed.", detail = bodySimple }, statusCode: (int)responseSimple.StatusCode);

        OpenAiChatResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(bodySimple, JsonContext.Default.OpenAiChatResponse);
        }
        catch (JsonException)
        {
            return Results.Json(new { error = "Unexpected response from chat model." }, statusCode: 502);
        }

        var textOut = parsed?.Choices?.FirstOrDefault()?.Message?.Content?.Trim();
        if (string.IsNullOrEmpty(textOut))
            return Results.Json(new { error = "No assistant message in response.", detail = bodySimple }, statusCode: 502);

        return Results.Json(new ChatResponse(textOut));
    }
})
.WithName("Chat");

/// <summary>Forwards JSON to EDD.API <c>POST /edd/calculate</c> so the Agent UI talks to one origin.</summary>
app.MapPost("/api/edd/calculate", async (HttpRequest httpReq, IHttpClientFactory httpFactory, IConfiguration config, CancellationToken ct) =>
    {
        var baseUrl = config["EddApi:BaseUrl"]?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl))
        {
            return Results.Json(
                new { error = "EDD API base URL is not configured. Set EddApi:BaseUrl (e.g. http://localhost:5139)." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        using var reader = new StreamReader(httpReq.Body);
        var body = await reader.ReadToEndAsync(ct);
        var url = $"{baseUrl}/edd/calculate";
        using var forward = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        var client = httpFactory.CreateClient();
        using var response = await client.SendAsync(forward, HttpCompletionOption.ResponseHeadersRead, ct);
        var respBody = await response.Content.ReadAsStringAsync(ct);
        return Results.Text(respBody, "application/json", statusCode: (int)response.StatusCode);
    })
    .WithName("ProxyEddCalculate");

app.MapFallbackToFile("index.html");

app.Run();

internal sealed record OpenAiMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

internal sealed record OpenAiChatRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] List<OpenAiMessage> Messages);

internal sealed record OpenAiChatResponse(
    [property: JsonPropertyName("choices")] List<OpenAiChoice>? Choices);

internal sealed record OpenAiChoice(
    [property: JsonPropertyName("message")] OpenAiAssistantMessage? Message);

internal sealed record OpenAiAssistantMessage(
    [property: JsonPropertyName("content")] string? Content);

[JsonSerializable(typeof(OpenAiChatRequest))]
[JsonSerializable(typeof(OpenAiMessage))]
[JsonSerializable(typeof(OpenAiChatResponse))]
internal partial class JsonContext : JsonSerializerContext;
