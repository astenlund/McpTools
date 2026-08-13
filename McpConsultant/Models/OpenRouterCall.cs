namespace McpConsultant.Models;

/// <summary>All inputs for one OpenRouter chat-completions call, resolved by the tool.</summary>
public sealed record OpenRouterCall
{
    public required string ApiKey { get; init; }

    public required string Model { get; init; }

    public required string Effort { get; init; }

    public required string SystemPrompt { get; init; }

    public required string UserMessage { get; init; }

    public required TimeSpan Timeout { get; init; }
}
