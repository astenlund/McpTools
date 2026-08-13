namespace McpConsultant.Services;

/// <summary>Formats the tool's success output per the spec's Response format section.</summary>
internal static class ResponseFormatter
{
    internal static string FormatSuccess(string model, string effort, string? finishReason, string answer)
    {
        var header = $"Consulted {model} (effort: {effort})";
        var warning = finishReason is not null && !string.Equals(finishReason, "stop", StringComparison.OrdinalIgnoreCase)
            ? $"\nWarning: answer incomplete (finish_reason: {finishReason})"
            : string.Empty;

        return $"{header}{warning}\n\n{answer}";
    }
}
