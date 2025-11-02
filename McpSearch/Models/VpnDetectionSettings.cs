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
