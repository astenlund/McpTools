namespace McpSearch.Exceptions;

/// <summary>
/// Exception thrown when Mullvad VPN is required but not connected.
/// </summary>
public class VpnNotConnectedException : Exception
{
    public VpnNotConnectedException()
        : base("Mullvad VPN is not active. Please connect to your VPN before performing web searches.")
    {
    }

    public VpnNotConnectedException(string message)
        : base(message)
    {
    }

    public VpnNotConnectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
