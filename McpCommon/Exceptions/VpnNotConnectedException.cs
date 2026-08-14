namespace McpCommon.Exceptions;

/// <summary>
/// Exception thrown when Mullvad VPN is required but not connected.
/// </summary>
public class VpnNotConnectedException : Exception
{
    public VpnNotConnectedException()
        : base("VPN UNAVAILABLE: The user must connect to Mullvad VPN before this operation can be performed. " +
               "This is a privacy requirement that only the user can fulfill. " +
               "Please inform the user that they need to connect their VPN, and wait for them to do so before retrying.")
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
