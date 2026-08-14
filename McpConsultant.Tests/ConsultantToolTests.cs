using System.Net;

using McpCommon.Services;
using McpConsultant.Models;
using McpConsultant.Services;
using McpConsultant.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace McpConsultant.Tests;

public class ConsultantToolTests
{
    private const string SuccessBody = """
        {
          "choices": [{"message": {"content": "Answer text"}, "finish_reason": "stop"}],
          "usage": {"prompt_tokens": 1, "completion_tokens": 1}
        }
        """;

    private static (Consultant Tool, StubHttpMessageHandler Handler) Build(Action<ConsultantSettings>? mutate = null)
    {
        var settings = new ConsultantSettings { ApiKey = "dummy-key" };
        mutate?.Invoke(settings);
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, SuccessBody);
        var client = new OpenRouterClient(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            NullLogger<OpenRouterClient>.Instance);
        var tool = new Consultant(
            client,
            new VpnDetectionService(NullLogger<VpnDetectionService>.Instance),
            Options.Create(settings),
            NullLogger<Consultant>.Instance);

        return (tool, handler);
    }

    [Fact]
    public async Task EmptyQuestion_ReturnsError_WithoutHttp()
    {
        // Arrange
        var (tool, handler) = Build();

        // Act
        var result = await tool.Consult("   ", null, null, null, null, CancellationToken.None);

        // Assert
        Assert.Contains("question cannot be empty", result);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task InvalidEffort_ReturnsAllowedValuesError_BeforeApiKeyCheck()
    {
        // Arrange - no key configured, invalid effort: step 2 must win over step 5
        var (tool, handler) = Build(s => s.ApiKey = null);

        // Act
        var result = await tool.Consult("Q", null, null, null, "medium", CancellationToken.None);

        // Assert
        Assert.Contains("effort must be one of: low, high, max", result);
        Assert.DoesNotContain("no OpenRouter API key", result);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task InvalidDefaultEffort_ReturnsAllowedValuesError()
    {
        // Arrange
        var (tool, handler) = Build(s => s.DefaultEffort = "medium");

        // Act
        var result = await tool.Consult("Q", null, null, null, null, CancellationToken.None);

        // Assert
        Assert.Contains("effort must be one of: low, high, max", result);
        Assert.Null(handler.LastRequest);
    }

    [Theory]
    [InlineData("LOW", "low")]
    [InlineData("High", "high")]
    [InlineData("MAX", "max")]
    public async Task Effort_AcceptedCaseInsensitively_ForwardedLowercase(string given, string forwarded)
    {
        // Arrange
        var (tool, handler) = Build();

        // Act
        var result = await tool.Consult("Q", null, null, null, given, CancellationToken.None);

        // Assert
        Assert.Contains($"(effort: {forwarded})", result);
        Assert.Contains($"\"effort\":\"{forwarded}\"", handler.LastRequestBody!.Replace(" ", string.Empty));
    }

    [Fact]
    public async Task BlankModelParameter_FallsBackToDefaultModel()
    {
        // Arrange
        var (tool, handler) = Build();

        // Act
        var result = await tool.Consult("Q", null, null, "   ", null, CancellationToken.None);

        // Assert
        Assert.Contains("Consulted openai/gpt-5.2", result);
    }

    [Fact]
    public async Task BlankDefaultModel_ReturnsMissingModelError()
    {
        // Arrange
        var (tool, handler) = Build(s => s.DefaultModel = "  ");

        // Act
        var result = await tool.Consult("Q", null, null, null, null, CancellationToken.None);

        // Assert
        Assert.Contains("Consultant:DefaultModel is not configured", result);
        Assert.Null(handler.LastRequest);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task NonPositiveTimeout_ReturnsSettingError(int timeout)
    {
        // Arrange
        var (tool, handler) = Build(s => s.TimeoutSeconds = timeout);

        // Act
        var result = await tool.Consult("Q", null, null, null, null, CancellationToken.None);

        // Assert
        Assert.Contains("must be a positive number", result);
        Assert.Contains("TimeoutSeconds", result);
        Assert.Null(handler.LastRequest);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NonPositiveMaxAttachmentBytes_ReturnsSettingError(long maxBytes)
    {
        // Arrange
        var (tool, handler) = Build(s => s.MaxAttachmentBytes = maxBytes);

        // Act
        var result = await tool.Consult("Q", null, null, null, null, CancellationToken.None);

        // Assert
        Assert.Contains("must be a positive number", result);
        Assert.Contains("MaxAttachmentBytes", result);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task EmptyApiKey_ReturnsMissingKeyError()
    {
        // Arrange
        var (tool, handler) = Build(s => s.ApiKey = "  ");

        // Act
        var result = await tool.Consult("Q", null, null, null, null, CancellationToken.None);

        // Assert
        Assert.Contains("no OpenRouter API key is configured", result);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task MissingFile_ReturnsFileError_WithoutHttp()
    {
        // Arrange - dummy key present; file validation (step 7) fails before the request (step 8)
        var (tool, handler) = Build();
        var missing = Path.Combine(Path.GetTempPath(), $"consultant-missing-{Guid.NewGuid():N}.txt");

        // Act
        var result = await tool.Consult("Q", null, [missing], null, null, CancellationToken.None);

        // Assert
        Assert.Contains("does not exist", result);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task Success_FormatsHeaderAndAnswer()
    {
        // Arrange
        var (tool, _) = Build();

        // Act
        var result = await tool.Consult("Q", null, null, null, null, CancellationToken.None);

        // Assert
        Assert.StartsWith("Consulted openai/gpt-5.2 (effort: high)", result);
        Assert.EndsWith("Answer text", result);
    }

    // The four response-handling error classes, driven through the stub seam so the tool's
    // catch clauses and their class substrings are exercised end to end (the spec's
    // stubbed-handler requirement). The VPN class stays covered by the template-table layer
    // alone, since it depends on live adapter state.
    private static Consultant BuildWithHandler(StubHttpMessageHandler handler, int timeoutSeconds = 30)
    {
        var settings = new ConsultantSettings { ApiKey = "dummy-key", TimeoutSeconds = timeoutSeconds };
        var client = new OpenRouterClient(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            NullLogger<OpenRouterClient>.Instance);

        return new Consultant(
            client,
            new VpnDetectionService(NullLogger<VpnDetectionService>.Instance),
            Options.Create(settings),
            NullLogger<Consultant>.Instance);
    }

    [Fact]
    public async Task NonSuccessStatus_ReturnsHttpErrorClass()
    {
        // Arrange
        var tool = BuildWithHandler(new StubHttpMessageHandler(System.Net.HttpStatusCode.InternalServerError, "boom"));

        // Act
        var result = await tool.Consult("Q", null, null, null, null, CancellationToken.None);

        // Assert
        Assert.Contains("OpenRouter returned HTTP 500", result);
    }

    [Fact]
    public async Task UnparseableBody_ReturnsUnusableResponseClass()
    {
        // Arrange
        var tool = BuildWithHandler(new StubHttpMessageHandler(System.Net.HttpStatusCode.OK, "not json"));

        // Act
        var result = await tool.Consult("Q", null, null, null, null, CancellationToken.None);

        // Assert
        Assert.Contains("response was unusable", result);
    }

    [Fact]
    public async Task Timeout_ReturnsTimeoutClassNamingConfiguredSeconds()
    {
        // Arrange - delayed handler against a 1-second configured timeout
        var handler = new StubHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        });
        var tool = BuildWithHandler(handler, timeoutSeconds: 1);

        // Act
        var result = await tool.Consult("Q", null, null, null, null, CancellationToken.None);

        // Assert
        Assert.Contains("timed out after 1 seconds", result);
    }

    [Fact]
    public async Task NetworkFailure_ReturnsNetworkFailureClass()
    {
        // Arrange
        var tool = BuildWithHandler(new StubHttpMessageHandler((_, _) =>
            throw new HttpRequestException("connection refused")));

        // Act
        var result = await tool.Consult("Q", null, null, null, null, CancellationToken.None);

        // Assert
        Assert.Contains("network failure calling OpenRouter", result);
        Assert.Contains("connection refused", result);
    }
}
