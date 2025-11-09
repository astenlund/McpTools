using System.Diagnostics;
using System.Text.Json;
using Xunit.Abstractions;

namespace McpWeb.Tests;

/// <summary>
/// Integration tests that start the actual MCP server and communicate with it.
/// Tests both search_web and fetch_url tools.
/// </summary>
public sealed class McpWebIntegrationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private Process? _serverProcess;

    public McpWebIntegrationTests(ITestOutputHelper output)
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

        // Search should succeed with human-readable text
        Assert.Contains("Search completed successfully", response);
        Assert.Contains("results for", response);

        // Should not have any errors
        Assert.DoesNotContain("Search unavailable", response);
        Assert.DoesNotContain("Search failed", response);
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

        // Search should succeed even without VPN
        Assert.Contains("Search completed successfully", response);
        Assert.Contains("results for", response);

        // Should NOT contain VPN error message
        Assert.DoesNotContain("connect to Mullvad VPN", response);

        // Should not have any errors
        Assert.DoesNotContain("Search unavailable", response);
        Assert.DoesNotContain("Search failed", response);
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
        // Should contain VPN error message
        Assert.Contains("connect to Mullvad VPN", response);
        Assert.Contains("Search unavailable", response);
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

    [Fact]
    public async Task FetchUrl_WithValidUrl_ReturnsContent()
    {
        // Arrange
        StartMcpServer();

        // Wait for server to initialize
        await Task.Delay(2000);

        // Initialize MCP protocol
        await InitializeMcpProtocol();

        // Act - Send MCP tool call request to fetch example.com
        var request = new
        {
            jsonrpc = "2.0",
            id = 4,
            method = "tools/call",
            @params = new
            {
                name = "fetch_url",  // MCP converts C# method names to snake_case
                arguments = new
                {
                    url = "https://example.com"
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

        // Should contain HTML content from example.com
        Assert.Contains("Example Domain", response);
        Assert.DoesNotContain("Error:", response);
    }

    [Fact]
    public async Task FetchUrl_WithInvalidUrl_ReturnsError()
    {
        // Arrange
        StartMcpServer();

        // Wait for server to initialize
        await Task.Delay(2000);

        // Initialize MCP protocol
        await InitializeMcpProtocol();

        // Act - Send MCP tool call request with invalid URL
        var request = new
        {
            jsonrpc = "2.0",
            id = 4,
            method = "tools/call",
            @params = new
            {
                name = "fetch_url",
                arguments = new
                {
                    url = "not a valid url"
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
        Assert.Contains("Error: Invalid URL format", response);
    }

    [Fact]
    public async Task FetchUrl_WithLocalhostUrl_ReturnsError()
    {
        // Arrange
        StartMcpServer();

        // Wait for server to initialize
        await Task.Delay(2000);

        // Initialize MCP protocol
        await InitializeMcpProtocol();

        // Act - Send MCP tool call request with localhost URL (SSRF prevention test)
        var request = new
        {
            jsonrpc = "2.0",
            id = 4,
            method = "tools/call",
            @params = new
            {
                name = "fetch_url",
                arguments = new
                {
                    url = "http://localhost:8080/admin"
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
        Assert.Contains("Error: Access to local or private network addresses is not allowed", response);
    }

    [Fact]
    public async Task FetchUrl_WithPrivateIpUrl_ReturnsError()
    {
        // Arrange
        StartMcpServer();

        // Wait for server to initialize
        await Task.Delay(2000);

        // Initialize MCP protocol
        await InitializeMcpProtocol();

        // Act - Send MCP tool call request with private IP (SSRF prevention test)
        var request = new
        {
            jsonrpc = "2.0",
            id = 4,
            method = "tools/call",
            @params = new
            {
                name = "fetch_url",
                arguments = new
                {
                    url = "http://192.168.1.1/router"
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
        Assert.Contains("Error: Access to local or private network addresses is not allowed", response);
    }

    [Fact]
    public async Task GetContext_ReturnsCurrentDateTimeAndTimezone()
    {
        // Arrange
        StartMcpServer();

        // Wait for server to initialize
        await Task.Delay(2000);

        // Initialize MCP protocol
        await InitializeMcpProtocol();

        // Act - Send MCP tool call request for get_context
        var request = new
        {
            jsonrpc = "2.0",
            id = 5,
            method = "tools/call",
            @params = new
            {
                name = "get_context",  // MCP converts C# method names to snake_case
                arguments = new { }  // No arguments needed
            }
        };

        var requestJson = JsonSerializer.Serialize(request);
        _output.WriteLine($"Request: {requestJson}");

        await SendToServer(requestJson);

        // Assert - Read response
        var response = await ReadFromServer();
        _output.WriteLine($"Response: {response}");

        Assert.NotNull(response);

        // Should contain context information in concise format
        Assert.Contains("Current date and time:", response);
        Assert.Contains("Timezone:", response);
        Assert.Contains("week", response);  // lowercase in "This is week X of YYYY"
        Assert.Contains("of 2025", response);  // Current year

        // Should not have any errors
        Assert.DoesNotContain("Error retrieving context", response);
    }

    [Fact]
    public async Task SearchWeb_FailedSearch_DoesNotTriggerDuplicateDetection()
    {
        // Arrange - This test verifies that failed searches don't count toward rate limiting
        StartMcpServer(requireVpn: false);

        // Wait for server to initialize
        await Task.Delay(2000);

        // Initialize MCP protocol
        await InitializeMcpProtocol();

        // Act 1 - Send invalid search (empty query) - should fail
        var request1 = new
        {
            jsonrpc = "2.0",
            id = 10,
            method = "tools/call",
            @params = new
            {
                name = "search_web",
                arguments = new
                {
                    query = "",  // Empty query - will fail validation
                    maxResults = 5,
                    fetchContent = false
                }
            }
        };

        var requestJson1 = JsonSerializer.Serialize(request1);
        _output.WriteLine($"First request (should fail): {requestJson1}");

        await SendToServer(requestJson1);

        var response1 = await ReadFromServer();
        _output.WriteLine($"First response: {response1}");

        Assert.NotNull(response1);
        Assert.Contains("Error: Search query cannot be empty", response1);

        // Act 2 - Send the same invalid search again immediately
        var request2 = new
        {
            jsonrpc = "2.0",
            id = 11,
            method = "tools/call",
            @params = new
            {
                name = "search_web",
                arguments = new
                {
                    query = "",  // Same empty query
                    maxResults = 5,
                    fetchContent = false
                }
            }
        };

        var requestJson2 = JsonSerializer.Serialize(request2);
        _output.WriteLine($"Second request (should also fail, NOT be blocked by duplicate detection): {requestJson2}");

        await SendToServer(requestJson2);

        var response2 = await ReadFromServer();
        _output.WriteLine($"Second response: {response2}");

        // Assert - Second request should get the same error, NOT "DUPLICATE SEARCH DETECTED"
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
}
