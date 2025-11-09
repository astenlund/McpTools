namespace McpWeb.Models;

/// <summary>
/// Configuration settings for URL fetching.
/// </summary>
public class FetchSettings
{
    /// <summary>
    /// Whether to require Mullvad VPN to be active before fetching URLs.
    /// Default: false (less privacy-sensitive than searching).
    /// </summary>
    public bool RequireVpn { get; set; } = false;
}
