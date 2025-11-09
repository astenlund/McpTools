using System.Diagnostics;
using System.Net.NetworkInformation;
using McpWeb.Exceptions;
using Microsoft.Extensions.Logging;

namespace McpWeb.Services;

/// <summary>
/// Service for detecting if Mullvad VPN is active on Windows.
/// </summary>
public class VpnDetectionService
{
    private readonly ILogger<VpnDetectionService> _logger;

    public VpnDetectionService(ILogger<VpnDetectionService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Checks if Mullvad VPN is currently active.
    /// </summary>
    /// <returns>True if Mullvad VPN is active, false otherwise.</returns>
    public bool IsMullvadVpnActive()
    {
        try
        {
            _logger.LogDebug("Checking for Mullvad VPN connection");

            // Check network adapters (most reliable method)
            if (CheckMullvadNetworkAdapter())
            {
                _logger.LogInformation("Mullvad VPN detected via network adapter");
                return true;
            }

            // No active Mullvad adapter found
            _logger.LogWarning("Mullvad VPN is not active");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking Mullvad VPN status");
            return false;
        }
    }

    /// <summary>
    /// Ensures that Mullvad VPN is connected.
    /// Throws an exception if VPN is not active.
    /// </summary>
    /// <exception cref="VpnNotConnectedException">Thrown when Mullvad VPN is not connected.</exception>
    public void EnsureVpnConnected()
    {
        if (!IsMullvadVpnActive())
        {
            throw new VpnNotConnectedException();
        }
    }

    /// <summary>
    /// Gets diagnostic information about all network adapters.
    /// Useful for debugging VPN detection issues.
    /// </summary>
    /// <returns>List of strings describing each network adapter.</returns>
    public List<string> GetNetworkAdapterDiagnostics()
    {
        var diagnostics = new List<string>();

        try
        {
            NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();

            foreach (NetworkInterface ni in interfaces)
            {
                var info = $"Name: {ni.Name}, " +
                          $"Description: {ni.Description}, " +
                          $"Type: {ni.NetworkInterfaceType}, " +
                          $"Status: {ni.OperationalStatus}, " +
                          $"Speed: {ni.Speed / 1_000_000} Mbps";

                diagnostics.Add(info);
            }
        }
        catch (Exception ex)
        {
            diagnostics.Add($"Error getting diagnostics: {ex.Message}");
        }

        return diagnostics;
    }

    /// <summary>
    /// Checks for Mullvad VPN network adapter.
    /// </summary>
    private bool CheckMullvadNetworkAdapter()
    {
        NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();

        foreach (NetworkInterface ni in interfaces)
        {
            // Only check adapters that are currently up
            if (ni.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            string description = ni.Description.ToLower();
            string name = ni.Name.ToLower();

            // Skip AdGuard to avoid false positives
            if (IsAdGuardAdapter(description, name))
            {
                _logger.LogDebug("Skipping AdGuard adapter: {Name}", ni.Name);
                continue;
            }

            // Check for Mullvad-specific indicators
            bool hasMullvadKeyword = description.Contains("mullvad") || name.Contains("mullvad");
            bool hasWireGuardKeyword = description.Contains("wireguard");
            bool hasWintunKeyword = description.Contains("wintun");
            bool isTunnelType = ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
                               (int)ni.NetworkInterfaceType == 53; // Mullvad uses type 53

            // Detect Mullvad adapter by name/description (most reliable)
            if (hasMullvadKeyword)
            {
                _logger.LogDebug(
                    "Found Mullvad adapter by name: Name={Name}, Description={Description}, Type={Type}",
                    ni.Name, ni.Description, ni.NetworkInterfaceType);
                return true;
            }

            // Also detect by WireGuard/Wintun tunnel type (fallback)
            if (isTunnelType && (hasWireGuardKeyword || hasWintunKeyword))
            {
                _logger.LogDebug(
                    "Found potential Mullvad adapter by tunnel type: Name={Name}, Description={Description}, Type={Type}",
                    ni.Name, ni.Description, ni.NetworkInterfaceType);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if the adapter is AdGuard (to filter out false positives).
    /// </summary>
    private bool IsAdGuardAdapter(string description, string name)
    {
        return description.Contains("adguard") ||
               description.Contains("wfp") ||
               name.Contains("adguard");
    }

    /// <summary>
    /// Checks if Mullvad daemon process is running.
    /// </summary>
    private bool IsMullvadProcessRunning()
    {
        try
        {
            Process[] processes = Process.GetProcessesByName("mullvad-daemon");
            bool isRunning = processes.Length > 0;

            if (isRunning)
            {
                _logger.LogDebug("Found {Count} mullvad-daemon process(es)", processes.Length);
            }

            // Clean up process handles
            foreach (var process in processes)
            {
                process.Dispose();
            }

            return isRunning;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not check for Mullvad process");
            return false;
        }
    }
}
