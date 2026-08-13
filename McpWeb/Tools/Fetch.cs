using System.ComponentModel;
using System.Net;
using McpCommon.Exceptions;
using McpCommon.Services;
using McpWeb.Models;
using McpWeb.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace McpWeb.Tools;

/// <summary>
/// MCP tool for fetching URL content.
/// </summary>
internal class Fetch
{
    private readonly ContentFetcher _contentFetcher;
    private readonly VpnDetectionService _vpnDetectionService;
    private readonly FetchSettings _fetchSettings;
    private readonly ILogger<Fetch> _logger;

    public Fetch(
        ContentFetcher contentFetcher,
        VpnDetectionService vpnDetectionService,
        IOptions<FetchSettings> fetchSettings,
        ILogger<Fetch> logger)
    {
        _contentFetcher = contentFetcher;
        _vpnDetectionService = vpnDetectionService;
        _fetchSettings = fetchSettings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Fetches the full HTML content from a specific URL.
    /// </summary>
    /// <param name="url">The URL to fetch (must be valid HTTP/HTTPS URL).</param>
    /// <returns>The full HTML content from the URL, or an error message.</returns>
    [McpServerTool]
    [Description("Fetches the full HTML content from a specific URL. Use this when you need to retrieve content from a known URL.")]
    public async Task<string> FetchUrl(
        [Description("The URL to fetch (e.g., 'https://example.com')")] string url)
    {
        // Validate URL
        if (string.IsNullOrWhiteSpace(url))
        {
            return "Error: URL cannot be empty.";
        }

        // Parse and validate URL format
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return $"Error: Invalid URL format: '{url}'. URL must be a valid absolute URL.";
        }

        // Only allow HTTP and HTTPS schemes
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return $"Error: Invalid URL scheme '{uri.Scheme}'. Only HTTP and HTTPS are allowed.";
        }

        // Security: Block localhost and private IP ranges (SSRF prevention)
        if (IsLocalOrPrivateAddress(uri))
        {
            return $"Error: Access to local or private network addresses is not allowed: '{url}'";
        }

        try
        {
            // Check VPN if required
            if (_fetchSettings.RequireVpn)
            {
                _vpnDetectionService.EnsureVpnConnected();
            }

            _logger.LogInformation("Fetching content from URL: {Url}", url);

            var content = await _contentFetcher.FetchContentAsync(url);

            if (content == null)
            {
                return $"Error: Failed to fetch content from '{url}'. The URL may be unreachable, blocked, or returned an error.";
            }

            _logger.LogInformation("Successfully fetched {ContentLength} characters from {Url}", content.Length, url);

            return content;
        }
        catch (VpnNotConnectedException ex)
        {
            return $"Fetch unavailable: {ex.Message}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching URL: {Url}", url);
            return $"Error fetching '{url}': {ex.Message}";
        }
    }

    /// <summary>
    /// Checks if a URI points to localhost or a private network address.
    /// </summary>
    private static bool IsLocalOrPrivateAddress(Uri uri)
    {
        var host = uri.Host;

        // Check for localhost variations
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("127.0.0.1") ||
            host.StartsWith("127.") ||
            host.Equals("::1") ||
            host.Equals("0.0.0.0"))
        {
            return true;
        }

        // Try to parse as IP address
        if (IPAddress.TryParse(host, out var ipAddress))
        {
            var bytes = ipAddress.GetAddressBytes();

            // IPv4 private ranges
            if (bytes.Length == 4)
            {
                // 10.0.0.0/8
                if (bytes[0] == 10)
                    return true;

                // 172.16.0.0/12
                if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                    return true;

                // 192.168.0.0/16
                if (bytes[0] == 192 && bytes[1] == 168)
                    return true;

                // 169.254.0.0/16 (link-local)
                if (bytes[0] == 169 && bytes[1] == 254)
                    return true;
            }

            // IPv6 private ranges (simplified check for common cases)
            if (bytes.Length == 16)
            {
                // fc00::/7 (Unique Local Addresses)
                if ((bytes[0] & 0xfe) == 0xfc)
                    return true;

                // fe80::/10 (Link-Local)
                if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80)
                    return true;
            }
        }

        return false;
    }
}
