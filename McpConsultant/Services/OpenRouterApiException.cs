namespace McpConsultant.Services;

/// <summary>
/// Thrown for a non-2xx OpenRouter response (StatusCode set) or an unusable 2xx body (StatusCode null).
/// BodyExcerpt is truncated to 500 characters.
/// </summary>
public class OpenRouterApiException(int? statusCode, string bodyExcerpt)
    : Exception($"OpenRouter call failed (status: {(statusCode?.ToString() ?? "unusable body")})")
{
    public int? StatusCode { get; } = statusCode;

    public string BodyExcerpt { get; } = bodyExcerpt;
}
