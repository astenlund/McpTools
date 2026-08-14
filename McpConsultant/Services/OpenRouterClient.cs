using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using McpConsultant.Models;
using Microsoft.Extensions.Logging;

namespace McpConsultant.Services;

/// <summary>
/// Owns the HTTP call to OpenRouter. Holds no IOptions: the tool resolves settings and passes values,
/// keeping every settings dereference inside the tool's terminal catch-all.
/// Timeout is enforced per request via a linked CancellationTokenSource (HttpClient.Timeout is infinite),
/// which is what makes timeout and client cancellation distinguishable.
/// </summary>
public sealed class OpenRouterClient(HttpClient httpClient, ILogger<OpenRouterClient> logger)
{
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const int ExcerptLength = 500;

    public async Task<OpenRouterResult> ConsultAsync(OpenRouterCall call, CancellationToken clientToken)
    {
        using var timeoutCts = new CancellationTokenSource(call.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(clientToken, timeoutCts.Token);

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", call.ApiKey);
        request.Headers.Add("X-Title", "McpConsultant");
        var body = new
        {
            model = call.Model,
            messages = new object[]
            {
                new { role = "system", content = call.SystemPrompt },
                new { role = "user", content = call.UserMessage },
            },
            reasoning = new { effort = call.Effort },
        };
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        HttpResponseMessage? response = null;
        string responseBody;
        try
        {
            response = await httpClient.SendAsync(request, linkedCts.Token);
            responseBody = await response.Content.ReadAsStringAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !clientToken.IsCancellationRequested)
        {
            response?.Dispose();

            throw new ConsultationTimeoutException(call.Timeout);
        }
        catch
        {
            // Client cancellation or transport failure below the timeout discriminator: dispose before propagating.
            response?.Dispose();

            throw;
        }

        using var _ = response;
        var excerpt = responseBody.Length > ExcerptLength ? responseBody[..ExcerptLength] : responseBody;

        if (!response.IsSuccessStatusCode)
        {
            throw new OpenRouterApiException((int)response.StatusCode, excerpt);
        }

        OpenRouterResult result;
        try
        {
            using var json = JsonDocument.Parse(responseBody);
            var choice = json.RootElement.GetProperty("choices")[0];
            var answer = choice.GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrEmpty(answer))
            {
                throw new OpenRouterApiException(null, excerpt);
            }

            string? finishReason = choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String
                ? fr.GetString()
                : null;

            result = new OpenRouterResult(answer, finishReason, ParseUsage(json.RootElement));
        }
        catch (Exception ex) when (ex is not OpenRouterApiException)
        {
            logger.LogDebug(ex, "Failed to parse OpenRouter response body");

            throw new OpenRouterApiException(null, excerpt);
        }

        return result;
    }

    // usage is observability-only: a malformed usage shape must never fail a call
    // that carried a good answer, so every field access is guarded (TryGetInt32
    // rejects non-integral and out-of-range numbers that GetInt32 would throw on)
    // and a partial shape just yields null.
    private static OpenRouterUsage? ParseUsage(JsonElement root)
    {
        if (root.TryGetProperty("usage", out var u)
            && u.ValueKind == JsonValueKind.Object
            && u.TryGetProperty("prompt_tokens", out var pt)
            && pt.ValueKind == JsonValueKind.Number
            && pt.TryGetInt32(out var promptTokens)
            && u.TryGetProperty("completion_tokens", out var cot)
            && cot.ValueKind == JsonValueKind.Number
            && cot.TryGetInt32(out var completionTokens))
        {
            int? reasoningTokens = u.TryGetProperty("completion_tokens_details", out var details)
                && details.ValueKind == JsonValueKind.Object
                && details.TryGetProperty("reasoning_tokens", out var rt)
                && rt.ValueKind == JsonValueKind.Number
                && rt.TryGetInt32(out var reasoning)
                    ? reasoning
                    : (int?)null;

            return new OpenRouterUsage(promptTokens, completionTokens, reasoningTokens);
        }

        return null;
    }
}
