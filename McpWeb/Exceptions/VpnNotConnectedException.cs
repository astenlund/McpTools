namespace McpWeb.Exceptions;

/// <summary>
/// Exception thrown when Mullvad VPN is required but not connected.
/// </summary>
public class VpnNotConnectedException : Exception
{
    public VpnNotConnectedException()
        : base("SEARCH UNAVAILABLE: The user must connect to Mullvad VPN before web searches can be performed. " +
               "This is a privacy requirement that only the user can fulfill. " +
               "Please inform the user that they need to connect their VPN, and wait for them to do so before retrying the search.")
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
