using McpWeb.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace McpWeb.Tests;

/// <summary>
/// Diagnostic tests for VPN detection.
/// These tests output information to help verify detection works correctly.
/// </summary>
public class VpnDetectionTests
{
    private readonly ITestOutputHelper _output;
    private readonly VpnDetectionService _vpnService;

    public VpnDetectionTests(ITestOutputHelper output)
    {
        _output = output;

        // Create a logger that writes to test output
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(new XunitLoggerProvider(output));
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        var logger = loggerFactory.CreateLogger<VpnDetectionService>();
        _vpnService = new VpnDetectionService(logger);
    }

    [Fact]
    public void DetectMullvadVpn_ShowsDiagnostics()
    {
        _output.WriteLine("=== VPN DETECTION DIAGNOSTIC TEST ===");
        _output.WriteLine("");

        // Get all network adapter information
        _output.WriteLine("All Network Adapters:");
        _output.WriteLine("---------------------");
        var diagnostics = _vpnService.GetNetworkAdapterDiagnostics();

        foreach (var info in diagnostics)
        {
            _output.WriteLine(info);
        }

        _output.WriteLine("");
        _output.WriteLine("---------------------");

        // Check if Mullvad VPN is detected
        bool isActive = _vpnService.IsMullvadVpnActive();

        _output.WriteLine("");
        _output.WriteLine($"Mullvad VPN Active: {isActive}");
        _output.WriteLine("");

        if (isActive)
        {
            _output.WriteLine("✅ Mullvad VPN detected!");
        }
        else
        {
            _output.WriteLine("❌ Mullvad VPN NOT detected");
            _output.WriteLine("");
            _output.WriteLine("If VPN is connected but not detected:");
            _output.WriteLine("- Check the adapter list above for Mullvad/WireGuard/Wintun");
            _output.WriteLine("- Verify the adapter Type is 'Tunnel'");
            _output.WriteLine("- Verify the adapter Status is 'Up'");
        }

        // Note: No assertions - this is purely diagnostic
        // We just want to see the output
    }

    [Fact]
    public void GetNetworkAdapterDiagnostics_ReturnsAdapters()
    {
        // This test verifies the diagnostics method works
        var diagnostics = _vpnService.GetNetworkAdapterDiagnostics();

        Assert.NotNull(diagnostics);
        Assert.NotEmpty(diagnostics);

        _output.WriteLine($"Found {diagnostics.Count} network adapter(s)");
    }
}

/// <summary>
/// Logger provider that writes to xUnit test output.
/// </summary>
public class XunitLoggerProvider : ILoggerProvider
{
    private readonly ITestOutputHelper _output;

    public XunitLoggerProvider(ITestOutputHelper output)
    {
        _output = output;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new XunitLogger(_output, categoryName);
    }

    public void Dispose() { }
}

/// <summary>
/// Logger that writes to xUnit test output.
/// </summary>
public class XunitLogger : ILogger
{
    private readonly ITestOutputHelper _output;
    private readonly string _categoryName;

    public XunitLogger(ITestOutputHelper output, string categoryName)
    {
        _output = output;
        _categoryName = categoryName;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        try
        {
            var message = formatter(state, exception);
            _output.WriteLine($"[{logLevel}] {_categoryName}: {message}");

            if (exception != null)
            {
                _output.WriteLine($"Exception: {exception}");
            }
        }
        catch
        {
            // Ignore errors writing to output
        }
    }
}
