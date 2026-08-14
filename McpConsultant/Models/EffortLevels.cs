namespace McpConsultant.Models;

/// <summary>
/// The single source of truth for the allowed reasoning-effort values
/// (a condensed set, by design, of OpenRouter's wider ladder). The tool's
/// validation and the invalid-effort error text both derive from this list.
/// </summary>
internal static class EffortLevels
{
    internal static readonly IReadOnlyList<string> Allowed = ["low", "high", "max"];
}
