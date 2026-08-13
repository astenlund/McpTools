namespace McpConsultant.Models;

/// <summary>
/// Settings for the Consultant tool, bound from the "Consultant" configuration section.
/// Code defaults here are the authoritative fallback when the section is absent;
/// the committed appsettings.json spells out the same values for discoverability.
/// </summary>
public class ConsultantSettings
{
    public string? ApiKey { get; set; }

    public string DefaultModel { get; set; } = "openai/gpt-5.2";

    public string? SystemPrompt { get; set; }

    public string DefaultEffort { get; set; } = "high";

    public bool RequireVpn { get; set; }

    public int TimeoutSeconds { get; set; } = 300;

    public long MaxAttachmentBytes { get; set; } = 1_048_576;
}
