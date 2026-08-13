namespace McpConsultant.Models;

public sealed record OpenRouterUsage(int PromptTokens, int CompletionTokens, int? ReasoningTokens);

public sealed record OpenRouterResult(string Answer, string? FinishReason, OpenRouterUsage? Usage);
