using System.Diagnostics;
using System.Text.Json;

namespace McpConsultant.Tests;

/// <summary>
/// Shared base class for child-process integration tests against the McpConsultant server.
/// Starts the server via <c>dotnet run --no-build</c>, speaks the MCP handshake over its
/// stdio pipes, and exposes helpers for sending tool calls and notifications and for
/// asserting on the server's stderr output.
/// </summary>
public abstract class McpConsultantHarness(ITestOutputHelper output) : IDisposable
{
    private readonly List<string> _stderrLines = [];
    private readonly SemaphoreSlim _stderrSignal = new(0);
    private Process? _serverProcess;
    private int _nextRequestId = 3;

    /// <summary>
    /// Raw JSON-RPC response line for the tools/list request sent during handshake.
    /// </summary>
    public string? ToolsListResponse { get; private set; }

    public void Dispose()
    {
        if (_serverProcess is { HasExited: false })
        {
            _serverProcess.Kill(entireProcessTree: true);
            _serverProcess.Dispose();
        }

        _stderrSignal.Dispose();
    }

    /// <summary>
    /// Starts the MCP server process, waits for it to come up, and performs the MCP handshake.
    /// </summary>
    protected async Task StartAndInitializeServerAsync(string apiKey)
    {
        StartMcpServer(apiKey);

        // Wait for the server process to start before speaking the protocol
        await Task.Delay(2000, TestContext.Current.CancellationToken);

        await InitializeMcpProtocolAsync();
    }

    /// <summary>
    /// Sends a tools/call request and reads the raw JSON-RPC response line.
    /// </summary>
    protected async Task<string?> CallToolAsync(string name, object arguments)
    {
        await SendToolCallAsync(name, arguments);

        var response = await ReadFromServerAsync();
        output.WriteLine($"Response: {response}");

        return response;
    }

    /// <summary>
    /// Sends a tools/call request without reading the response, so a caller can interleave
    /// a notification with an in-flight request. Returns the request id used.
    /// </summary>
    protected async Task<int> SendToolCallAsync(string name, object arguments)
    {
        var id = _nextRequestId++;
        var request = new { jsonrpc = "2.0", id, method = "tools/call", @params = new { name, arguments } };
        var requestJson = JsonSerializer.Serialize(request);
        output.WriteLine($"Request: {requestJson}");
        await SendToServerAsync(requestJson);

        return id;
    }

    /// <summary>
    /// Sends a JSON-RPC notification (no id) to the server.
    /// </summary>
    protected async Task SendNotificationAsync(string method, object? parameters)
    {
        var notification = parameters is null
            ? (object)new { jsonrpc = "2.0", method }
            : new { jsonrpc = "2.0", method, @params = parameters };
        var notificationJson = JsonSerializer.Serialize(notification);
        output.WriteLine($"Notification: {notificationJson}");
        await SendToServerAsync(notificationJson);
    }

    /// <summary>
    /// Extracts the tool's plain text from a raw JSON-RPC response's result.content[0].text.
    /// Returns null when the response is absent or does not contain that shape.
    /// </summary>
    protected static string? ExtractToolText(string? rawResponse)
    {
        if (string.IsNullOrEmpty(rawResponse))
        {
            return null;
        }

        using var json = JsonDocument.Parse(rawResponse);
        if (!json.RootElement.TryGetProperty("result", out var result)
            || !result.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array
            || content.GetArrayLength() == 0)
        {
            return null;
        }

        return content[0].TryGetProperty("text", out var text) ? text.GetString() : null;
    }

    /// <summary>
    /// Scans the buffered stderr lines (and awaits new ones) for a line containing the given
    /// substring, returning the matching line or null once the timeout elapses.
    /// </summary>
    protected async Task<string?> WaitForStderrLineAsync(string substring, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var scanned = 0;
        while (true)
        {
            lock (_stderrLines)
            {
                for (; scanned < _stderrLines.Count; scanned++)
                {
                    if (_stderrLines[scanned].Contains(substring, StringComparison.Ordinal))
                    {
                        return _stderrLines[scanned];
                    }
                }
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero || !await _stderrSignal.WaitAsync(remaining))
            {
                return null;
            }
        }
    }

    private void StartMcpServer(string apiKey)
    {
        var projectPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "..",
            "..",
            "..",
            "..",
            "McpConsultant",
            "McpConsultant.csproj");

        output.WriteLine($"Starting MCP server from: {projectPath}");

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

        // Always set the API key env var (never DOTNET_ENVIRONMENT): empty string forces the
        // missing-key branch, "dummy-key" satisfies presence without exercising the network.
        _serverProcess.StartInfo.EnvironmentVariables["Consultant__ApiKey"] = apiKey;
        output.WriteLine("Setting Consultant__ApiKey");

        // Capture stderr for debugging and for the assertable buffer used by WaitForStderrLineAsync
        _serverProcess.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data))
            {
                return;
            }

            output.WriteLine($"[Server stderr]: {e.Data}");

            lock (_stderrLines)
            {
                _stderrLines.Add(e.Data);
            }

            _stderrSignal.Release();
        };

        _serverProcess.Start();
        _serverProcess.BeginErrorReadLine();

        output.WriteLine("MCP server started");
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
        output.WriteLine($"Sending initialize: {initializeJson}");
        await SendToServerAsync(initializeJson);

        // Step 2: Read server response
        var initializeResponse = await ReadFromServerAsync();
        output.WriteLine($"Initialize response: {initializeResponse}");

        // Step 3: Send initialized notification (no id, it's a notification)
        var initializedNotification = new
        {
            jsonrpc = "2.0",
            method = "notifications/initialized"
        };

        var initializedJson = JsonSerializer.Serialize(initializedNotification);
        output.WriteLine($"Sending initialized: {initializedJson}");
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
        output.WriteLine($"Sending tools/list: {toolsListJson}");
        await SendToServerAsync(toolsListJson);

        // Read tools list response
        ToolsListResponse = await ReadFromServerAsync();
        output.WriteLine($"Tools list response: {ToolsListResponse}");
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
