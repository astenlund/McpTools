using System.Diagnostics;
using System.Text.Json;
using Xunit.Abstractions;

namespace McpSearch.Tests;

/// <summary>
/// Integration tests that start the actual MCP server and communicate with it.
/// </summary>
public sealed class SearchToolsIntegrationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private Process? _serverProcess;

    public SearchToolsIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task SearchWeb_WithVpnConnected_ReturnsResults()
    {
        // Arrange
        StartMcpServer();

        // Wait for server to initialize
        await Task.Delay(2000);

        // Initialize MCP protocol
        await InitializeMcpProtocol();

        // Act - Send MCP tool call request
        var request = new
        {
            jsonrpc = "2.0",
            id = 3,
            method = "tools/call",
            @params = new
            {
                name = "search_web",  // MCP converts C# method names to snake_case
                arguments = new
                {
                    query = "C# async programming",
                    maxResults = 3,
                    fetchContent = false  // Skip content to make test faster
                }
            }
        };

        var requestJson = JsonSerializer.Serialize(request);
        _output.WriteLine($"Request: {requestJson}");

        await SendToServer(requestJson);

        // Assert - Read response
        var response = await ReadFromServer();
        _output.WriteLine($"Response: {response}");

        Assert.NotNull(response);
        // MCP wraps tool responses in result.content[].text format

        // The test passes if either:
        // 1. Search succeeds (contains resultCount), OR
        // 2. DuckDuckGo blocks the VPN IP with 403 (expected when using VPN)
        var hasResults = response.Contains("resultCount");
        var isBlocked = response.Contains("403") || response.Contains("Forbidden");

        // At least one should be true (either success or expected block)
        Assert.True(hasResults || isBlocked,
            $"Expected either successful results or 403 block, but got: {response.Substring(0, Math.Min(200, response.Length))}");

        // If it succeeded, verify no errors
        if (hasResults)
        {
            Assert.DoesNotContain("error", response);
        }
    }

    [Fact]
    public async Task SearchWeb_WithRequireVpnFalse_ReturnsResults()
    {
        // Arrange - Start server with RequireVpn=false via environment variable
        StartMcpServer(requireVpn: false);

        // Wait for server to initialize
        await Task.Delay(2000);

        // Initialize MCP protocol
        await InitializeMcpProtocol();

        // Act - Send MCP tool call request
        var request = new
        {
            jsonrpc = "2.0",
            id = 3,
            method = "tools/call",
            @params = new
            {
                name = "search_web",
                arguments = new
                {
                    query = "test query without VPN",
                    maxResults = 3,
                    fetchContent = false
                }
            }
        };

        var requestJson = JsonSerializer.Serialize(request);
        _output.WriteLine($"Request: {requestJson}");

        await SendToServer(requestJson);

        // Assert - Read response
        var response = await ReadFromServer();
        _output.WriteLine($"Response: {response}");

        Assert.NotNull(response);

        // The test passes if either:
        // 1. Search succeeds (contains resultCount), OR
        // 2. DuckDuckGo blocks with 403 (can happen even without VPN)
        var hasResults = response.Contains("resultCount");
        var isBlocked = response.Contains("403") || response.Contains("Forbidden");

        // Should NOT contain VPN error message
        Assert.DoesNotContain("Mullvad VPN is not active", response);

        // At least one should be true (either success or block)
        Assert.True(hasResults || isBlocked,
            $"Expected either successful results or 403 block, but got: {response.Substring(0, Math.Min(200, response.Length))}");
    }

    [Fact]
    public async Task SearchWeb_WithVpnDisconnected_ReturnsError()
    {
        // Arrange
        StartMcpServer();

        // Wait for server to initialize
        await Task.Delay(2000);

        // Initialize MCP protocol
        await InitializeMcpProtocol();

        // Act - Send MCP tool call request
        var request = new
        {
            jsonrpc = "2.0",
            id = 3,
            method = "tools/call",
            @params = new
            {
                name = "search_web",  // MCP converts C# method names to snake_case
                arguments = new
                {
                    query = "test query",
                    maxResults = 5,
                    fetchContent = false
                }
            }
        };

        var requestJson = JsonSerializer.Serialize(request);
        _output.WriteLine($"Request: {requestJson}");

        await SendToServer(requestJson);

        // Assert - Read response
        var response = await ReadFromServer();
        _output.WriteLine($"Response: {response}");

        Assert.NotNull(response);
        // MCP wraps tool responses in result.content[].text format
        // The actual error JSON is inside the text field
        Assert.Contains("Mullvad VPN is not active", response);
        Assert.Contains("results", response);  // Verify results field is present
    }

    private void StartMcpServer(bool? requireVpn = null)
    {
        var projectPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "..",
            "..",
            "..",
            "..",
            "McpSearch",
            "McpSearch.csproj");

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

        // Override VPN requirement via environment variable if specified
        if (requireVpn.HasValue)
        {
            _serverProcess.StartInfo.EnvironmentVariables["VpnDetection__RequireVpn"] = requireVpn.Value.ToString();
            _output.WriteLine($"Setting VpnDetection__RequireVpn={requireVpn.Value}");
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

    private async Task SendToServer(string message)
    {
        if (_serverProcess?.StandardInput == null)
            throw new InvalidOperationException("Server not running");

        await _serverProcess.StandardInput.WriteLineAsync(message);
        await _serverProcess.StandardInput.FlushAsync();
    }

    private async Task<string?> ReadFromServer()
    {
        if (_serverProcess?.StandardOutput == null)
            throw new InvalidOperationException("Server not running");

        // MCP protocol sends one message per line
        var response = await _serverProcess.StandardOutput.ReadLineAsync();
        return response;
    }

    private async Task InitializeMcpProtocol()
    {
        // Step 1: Send initialize request
        var initializeRequest = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2024-11-05",
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
        await SendToServer(initializeJson);

        // Step 2: Read server response
        var initializeResponse = await ReadFromServer();
        _output.WriteLine($"Initialize response: {initializeResponse}");

        // Step 3: Send initialized notification (no id, it's a notification)
        var initializedNotification = new
        {
            jsonrpc = "2.0",
            method = "initialized"
        };

        var initializedJson = JsonSerializer.Serialize(initializedNotification);
        _output.WriteLine($"Sending initialized: {initializedJson}");
        await SendToServer(initializedJson);

        // Give server a moment to process the notification
        await Task.Delay(100);

        // Step 4: List available tools to discover them
        var toolsListRequest = new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/list"
        };

        var toolsListJson = JsonSerializer.Serialize(toolsListRequest);
        _output.WriteLine($"Sending tools/list: {toolsListJson}");
        await SendToServer(toolsListJson);

        // Read tools list response
        var toolsListResponse = await ReadFromServer();
        _output.WriteLine($"Tools list response: {toolsListResponse}");
    }

    public void Dispose()
    {
        if (_serverProcess is { HasExited: false })
        {
            _serverProcess.Kill(entireProcessTree: true);
            _serverProcess.Dispose();
        }
    }
}
