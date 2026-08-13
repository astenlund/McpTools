using System.Net;
using System.Text.Json;

using McpConsultant.Models;
using McpConsultant.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpConsultant.Tests;

public class OpenRouterClientTests
{
    private const string SuccessBody = """
        {
          "choices": [{"message": {"content": "The answer."}, "finish_reason": "stop"}],
          "usage": {"prompt_tokens": 10, "completion_tokens": 5,
                    "completion_tokens_details": {"reasoning_tokens": 3}}
        }
        """;

    private static OpenRouterCall Call(TimeSpan? timeout = null) => new()
    {
        ApiKey = "test-key",
        Model = "test/model",
        Effort = "high",
        SystemPrompt = "persona",
        UserMessage = "hello",
        Timeout = timeout ?? TimeSpan.FromSeconds(30),
    };

    private static OpenRouterClient Client(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, NullLogger<OpenRouterClient>.Instance);

    [Fact]
    public async Task Success_ParsesAnswerFinishReasonAndUsage()
    {
        // Arrange
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, SuccessBody);

        // Act
        var result = await Client(handler).ConsultAsync(Call(), CancellationToken.None);

        // Assert
        Assert.Equal("The answer.", result.Answer);
        Assert.Equal("stop", result.FinishReason);
        Assert.NotNull(result.Usage);
        Assert.Equal(10, result.Usage.PromptTokens);
        Assert.Equal(5, result.Usage.CompletionTokens);
        Assert.Equal(3, result.Usage.ReasoningTokens);
    }

    [Fact]
    public async Task Request_CarriesAuthEffortAndTitleHeader()
    {
        // Arrange
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, SuccessBody);

        // Act
        await Client(handler).ConsultAsync(Call(), CancellationToken.None);

        // Assert
        Assert.Equal("Bearer test-key", handler.LastRequest!.Headers.Authorization!.ToString());
        Assert.Equal("McpConsultant", handler.LastRequest.Headers.GetValues("X-Title").Single());
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("test/model", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("high", body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal(2, body.RootElement.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task NonSuccessStatus_ThrowsApiExceptionWithStatusAndExcerpt()
    {
        // Arrange
        var handler = new StubHttpMessageHandler(HttpStatusCode.TooManyRequests, new string('e', 600));

        // Act / Assert
        var ex = await Assert.ThrowsAsync<OpenRouterApiException>(
            () => Client(handler).ConsultAsync(Call(), CancellationToken.None));
        Assert.Equal(429, ex.StatusCode);
        Assert.Equal(500, ex.BodyExcerpt.Length);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"choices\": []}")]
    [InlineData("{\"choices\": [{\"message\": {\"content\": \"\"}}]}")]
    public async Task Unusable2xxBody_ThrowsApiExceptionWithoutStatus(string body)
    {
        // Arrange
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, body);

        // Act / Assert
        var ex = await Assert.ThrowsAsync<OpenRouterApiException>(
            () => Client(handler).ConsultAsync(Call(), CancellationToken.None));
        Assert.Null(ex.StatusCode);
    }

    [Fact]
    public async Task Timeout_ThrowsConsultationTimeoutException()
    {
        // Arrange - responder waits far longer than the 1-second call timeout
        var handler = new StubHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        // Act / Assert
        await Assert.ThrowsAsync<ConsultationTimeoutException>(
            () => Client(handler).ConsultAsync(Call(TimeSpan.FromSeconds(1)), CancellationToken.None));
    }

    [Fact]
    public async Task ClientCancellation_RethrowsOperationCanceled_NotTimeout()
    {
        // Arrange
        using var clientCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var handler = new StubHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        // Act / Assert
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Client(handler).ConsultAsync(Call(TimeSpan.FromSeconds(30)), clientCts.Token));
        Assert.IsNotType<ConsultationTimeoutException>(ex);
    }

    [Fact]
    public async Task NetworkFailure_PropagatesHttpRequestException()
    {
        // Arrange
        var handler = new StubHttpMessageHandler((_, _) =>
            throw new HttpRequestException("connection refused"));

        // Act / Assert
        await Assert.ThrowsAsync<HttpRequestException>(
            () => Client(handler).ConsultAsync(Call(), CancellationToken.None));
    }
}
