namespace McpSearch.Models;

/// <summary>
/// Configuration settings for VPN detection.
/// </summary>
public class VpnDetectionSettings
{
    /// <summary>
    /// Whether to require Mullvad VPN to be active before performing web searches.
    /// Default: true (secure by default).
    /// </summary>
    public bool RequireVpn { get; set; } = true;
}

/// <summary>
/// Configuration settings for search provider.
/// </summary>
public class SearchSettings
{
    /// <summary>
    /// Serper.dev API key. Get free key at https://serper.dev (2,500 queries/month free).
    /// Required for web search functionality.
    /// </summary>
    public string? SerperApiKey { get; set; }
}
