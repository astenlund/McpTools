using System.Diagnostics;
using System.Text.Json;

namespace McpWeb.Tests;

/// <summary>
/// Integration tests that start the actual MCP server and communicate with it.
/// Tests the search_web, fetch_url, and get_context tools.
/// </summary>
public sealed class McpWebIntegrationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private Process? _serverProcess;
    private int _nextRequestId = 3;

    public McpWebIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task SearchWeb_WithVpnConnected_ReturnsResults()
    {
        // Arrange
        await StartAndInitializeServerAsync();

        // Act
        var response = await CallToolAsync("search_web", new { query = "C# async programming", maxResults = 3, fetchContent = false });

        // Assert
        Assert.NotNull(response);
        Assert.Contains("Search completed successfully", response);
        Assert.Contains("results for", response);
        Assert.DoesNotContain("Search unavailable", response);
        Assert.DoesNotContain("Search failed", response);
    }

    [Fact]
    public async Task SearchWeb_WithRequireVpnFalse_ReturnsResults()
    {
        // Arrange
        await StartAndInitializeServerAsync(requireVpn: false);

        // Act
        var response = await CallToolAsync("search_web", new { query = "test query without VPN", maxResults = 3, fetchContent = false });

        // Assert
        Assert.NotNull(response);
        Assert.Contains("Search completed successfully", response);
        Assert.Contains("results for", response);
        Assert.DoesNotContain("connect to Mullvad VPN", response);
        Assert.DoesNotContain("Search unavailable", response);
        Assert.DoesNotContain("Search failed", response);
    }

    [Fact]
    public async Task SearchWeb_WithVpnDisconnected_ReturnsError()
    {
        // Arrange
        await StartAndInitializeServerAsync();

        // Act
        var response = await CallToolAsync("search_web", new { query = "test query", maxResults = 5, fetchContent = false });

        // Assert
        Assert.NotNull(response);
        Assert.Contains("connect to Mullvad VPN", response);
        Assert.Contains("Search unavailable", response);
    }

    [Fact]
    public async Task FetchUrl_WithValidUrl_ReturnsContent()
    {
        // Arrange
        await StartAndInitializeServerAsync();

        // Act
        var response = await CallToolAsync("fetch_url", new { url = "https://example.com" });

        // Assert
        Assert.NotNull(response);
        Assert.Contains("Example Domain", response);
        Assert.DoesNotContain("Error:", response);
    }

    [Fact]
    public async Task FetchUrl_WithInvalidUrl_ReturnsError()
    {
        // Arrange
        await StartAndInitializeServerAsync();

        // Act
        var response = await CallToolAsync("fetch_url", new { url = "not a valid url" });

        // Assert
        Assert.NotNull(response);
        Assert.Contains("Error: Invalid URL format", response);
    }

    [Fact]
    public async Task FetchUrl_WithLocalhostUrl_ReturnsError()
    {
        // Arrange
        await StartAndInitializeServerAsync();

        // Act - localhost URL exercises SSRF prevention
        var response = await CallToolAsync("fetch_url", new { url = "http://localhost:8080/admin" });

        // Assert
        Assert.NotNull(response);
        Assert.Contains("Error: Access to local or private network addresses is not allowed", response);
    }

    [Fact]
    public async Task FetchUrl_WithPrivateIpUrl_ReturnsError()
    {
        // Arrange
        await StartAndInitializeServerAsync();

        // Act - private IP exercises SSRF prevention
        var response = await CallToolAsync("fetch_url", new { url = "http://192.168.1.1/router" });

        // Assert
        Assert.NotNull(response);
        Assert.Contains("Error: Access to local or private network addresses is not allowed", response);
    }

    [Fact]
    public async Task GetContext_ReturnsCurrentDateTimeAndTimezone()
    {
        // Arrange
        await StartAndInitializeServerAsync();

        // Act
        var response = await CallToolAsync("get_context", new { });

        // Assert
        Assert.NotNull(response);
        Assert.Contains("Current date and time:", response);
        Assert.Contains("Timezone:", response);
        Assert.Contains("week", response);  // lowercase in "This is week X of YYYY"
        Assert.Contains($"of {DateTime.Now.Year}", response);
        Assert.DoesNotContain("Error retrieving context", response);
    }

    [Fact]
    public async Task SearchWeb_FailedSearch_DoesNotTriggerDuplicateDetection()
    {
        // Arrange - failed searches must not count toward duplicate detection
        await StartAndInitializeServerAsync(requireVpn: false);

        // Act - send the same invalid search (empty query) twice in a row
        var response1 = await CallToolAsync("search_web", new { query = "", maxResults = 5, fetchContent = false });
        var response2 = await CallToolAsync("search_web", new { query = "", maxResults = 5, fetchContent = false });

        // Assert - both fail validation; the second must not be blocked as a duplicate
        Assert.NotNull(response1);
        Assert.Contains("Error: Search query cannot be empty", response1);
        Assert.NotNull(response2);
        Assert.Contains("Error: Search query cannot be empty", response2);
        Assert.DoesNotContain("DUPLICATE SEARCH DETECTED", response2);
    }

    public void Dispose()
    {
        if (_serverProcess is { HasExited: false })
        {
            _serverProcess.Kill(entireProcessTree: true);
            _serverProcess.Dispose();
        }
    }

    /// <summary>
    /// Starts the MCP server process, waits for it to come up, and performs the MCP handshake.
    /// </summary>
    private async Task StartAndInitializeServerAsync(bool? requireVpn = null)
    {
        StartMcpServer(requireVpn);

        // Wait for the server process to start before speaking the protocol
        await Task.Delay(2000, TestContext.Current.CancellationToken);

        await InitializeMcpProtocolAsync();
    }

    /// <summary>
    /// Sends a tools/call request and returns the raw JSON-RPC response line.
    /// </summary>
    private async Task<string?> CallToolAsync(string name, object arguments)
    {
        var request = new
        {
            jsonrpc = "2.0",
            id = _nextRequestId++,
            method = "tools/call",
            @params = new { name, arguments }
        };

        var requestJson = JsonSerializer.Serialize(request);
        _output.WriteLine($"Request: {requestJson}");
        await SendToServerAsync(requestJson);

        var response = await ReadFromServerAsync();
        _output.WriteLine($"Response: {response}");

        return response;
    }

    private void StartMcpServer(bool? requireVpn = null)
    {
        var projectPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "..",
            "..",
            "..",
            "..",
            "McpWeb",
            "McpWeb.csproj");

        _output.WriteLine($"Starting MCP server from: {projectPath}");

        _serverProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --project \"{projectPath}\" --no-build",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        // Set environment to Development so appsettings.Development.json is loaded
        _serverProcess.StartInfo.EnvironmentVariables["DOTNET_ENVIRONMENT"] = "Development";
        _output.WriteLine("Setting DOTNET_ENVIRONMENT=Development");

        // Override VPN requirement via environment variable if specified
        if (requireVpn.HasValue)
        {
            _serverProcess.StartInfo.EnvironmentVariables["Search__RequireVpn"] = requireVpn.Value.ToString();
            _output.WriteLine($"Setting Search__RequireVpn={requireVpn.Value}");
        }

        // Capture stderr for debugging
        _serverProcess.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _output.WriteLine($"[Server stderr]: {e.Data}");
            }
        };

        _serverProcess.Start();
        _serverProcess.BeginErrorReadLine();

        _output.WriteLine("MCP server started");
    }

    private async Task InitializeMcpProtocolAsync()
    {
        // Step 1: Send initialize request
        var initializeRequest = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new
                {
                    name = "test-client",
                    version = "1.0.0"
                }
            }
        };

        var initializeJson = JsonSerializer.Serialize(initializeRequest);
        _output.WriteLine($"Sending initialize: {initializeJson}");
        await SendToServerAsync(initializeJson);

        // Step 2: Read server response
        var initializeResponse = await ReadFromServerAsync();
        _output.WriteLine($"Initialize response: {initializeResponse}");

        // Step 3: Send initialized notification (no id, it's a notification)
        var initializedNotification = new
        {
            jsonrpc = "2.0",
            method = "notifications/initialized"
        };

        var initializedJson = JsonSerializer.Serialize(initializedNotification);
        _output.WriteLine($"Sending initialized: {initializedJson}");
        await SendToServerAsync(initializedJson);

        // Give server a moment to process the notification
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Step 4: List available tools to discover them
        var toolsListRequest = new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/list"
        };

        var toolsListJson = JsonSerializer.Serialize(toolsListRequest);
        _output.WriteLine($"Sending tools/list: {toolsListJson}");
        await SendToServerAsync(toolsListJson);

        // Read tools list response
        var toolsListResponse = await ReadFromServerAsync();
        _output.WriteLine($"Tools list response: {toolsListResponse}");
    }

    private async Task SendToServerAsync(string message)
    {
        if (_serverProcess?.StandardInput == null)
        {
            throw new InvalidOperationException("Server not running");
        }

        await _serverProcess.StandardInput.WriteLineAsync(message);
        await _serverProcess.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
    }

    private async Task<string?> ReadFromServerAsync()
    {
        if (_serverProcess?.StandardOutput == null)
        {
            throw new InvalidOperationException("Server not running");
        }

        // MCP protocol sends one message per line
        return await _serverProcess.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken);
    }
}
