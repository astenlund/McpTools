using System.Text;

using McpConsultant.Models;

namespace McpConsultant.Services;

/// <summary>
/// Assembles the OpenRouter user message: supporting material first, question last,
/// every part explicitly framed (see the spec's Message assembly section for the rationale).
/// </summary>
internal static class PromptAssembler
{
    internal const string DefaultSystemPrompt =
        "You are an independent expert consultant. Give a candid, critical second opinion. " +
        "Disagree plainly when warranted, state uncertainty clearly, and do not defer to the asker's framing.";

    // Newlines are explicit '\n' throughout: the assembled message is a wire payload, and
    // AppendLine's Environment.NewLine would make it (and the unit tests) platform-dependent.
    internal static string BuildUserMessage(string question, string? context, IReadOnlyList<AttachedFile> files)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(context))
        {
            sb.Append("===== BEGIN CONTEXT =====\n");
            sb.Append(context).Append('\n');
            sb.Append("===== END CONTEXT =====\n");
        }

        foreach (var file in files)
        {
            sb.Append('\n');
            sb.Append($"===== BEGIN FILE: {file.Path} =====\n");
            sb.Append(file.Content).Append('\n');
            sb.Append($"===== END FILE: {file.Path} =====\n");
        }

        sb.Append('\n');
        sb.Append("===== QUESTION =====\n");
        sb.Append(question);

        return sb.ToString();
    }
}
