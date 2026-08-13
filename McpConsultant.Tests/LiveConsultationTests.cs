using Microsoft.Extensions.Configuration;

namespace McpConsultant.Tests;

/// <summary>
/// Live probes against the real OpenRouter API. Gated on both the MCPCONSULTANT_LIVE=1
/// environment opt-in and a resolvable API key, so an unfiltered `dotnet test` on a
/// developer machine never silently issues paid calls. Each test resolves the key itself
/// (see <see cref="ResolveApiKey"/>) rather than relying on the server's own configuration
/// hierarchy, because the harness never spawns the child with DOTNET_ENVIRONMENT=Development.
/// </summary>
public sealed class LiveConsultationTests(ITestOutputHelper output) : McpConsultantHarness(output)
{
    // Recorded from the OpenRouter catalog (GET /api/v1/models) on 2026-08-14:
    // openai/gpt-4o has no "reasoning" entry in supported_parameters, so it is documented
    // as a non-reasoning model.
    private const string NonReasoningModel = "openai/gpt-4o";

    // google/gemini-3.7-flash is reasoning-capable (reasoning.mandatory: true) but its
    // reasoning.supported_efforts is ["high", "medium", "low"], documented without a "max"
    // tier; OpenRouter's reasoning-tokens docs confirm requests for an unsupported effort
    // are mapped down to the nearest supported level rather than rejected.
    private const string LadderLimitedReasoningModel = "google/gemini-3.7-flash";

    [Fact]
    public async Task Consult_EndToEnd_ReturnsAnswerWithHeader()
    {
        // Arrange
        await StartLiveServerAsync();

        // Act
        var response = await CallToolAsync("consult", new { question = "In one sentence, what is the capital of France?" });
        var text = ExtractToolText(response);

        // Assert
        Assert.NotNull(text);
        Assert.StartsWith("Consulted ", text);
        Assert.False(string.IsNullOrEmpty(text.Split("\n\n", 2)[1]));
    }

    [Fact]
    public async Task Effort_LowHighMax_AllAccepted()
    {
        // Arrange
        await StartLiveServerAsync();

        // Act / Assert
        foreach (var effort in new[] { "low", "high", "max" })
        {
            var response = await CallToolAsync(
                "consult",
                new { question = "In one sentence, what is the capital of France?", effort });
            var text = ExtractToolText(response);

            Assert.NotNull(text);
            Assert.Contains($"(effort: {effort})", text);
            Assert.DoesNotContain("Error:", text);
        }
    }

    [Fact]
    public async Task ReasoningObject_OnNonReasoningModel_Succeeds()
    {
        // Arrange
        await StartLiveServerAsync();

        // Act
        var response = await CallToolAsync(
            "consult",
            new { question = "In one sentence, what is the capital of France?", model = NonReasoningModel });
        var text = ExtractToolText(response);

        // Assert
        Assert.NotNull(text);
        Assert.DoesNotContain("Error:", text);
        var completed = await WaitForStderrLineAsync("Consultation completed", TimeSpan.FromSeconds(10));
        Assert.NotNull(completed);
        Assert.DoesNotMatch(@"reasoning=\d", completed);
    }

    [Fact]
    public async Task EffortMax_OnModelWithoutMaxTier_Degrades()
    {
        // Arrange
        await StartLiveServerAsync();

        // Act
        var response = await CallToolAsync(
            "consult",
            new
            {
                question = "In one sentence, what is the capital of France?",
                model = LadderLimitedReasoningModel,
                effort = "max",
            });
        var text = ExtractToolText(response);

        // Assert
        Assert.NotNull(text);
        Assert.DoesNotContain("Error:", text);
    }

    [Fact]
    public async Task Usage_OnReasoningModel_ReportsReasoningTokens()
    {
        // Arrange
        await StartLiveServerAsync();

        // Act
        await CallToolAsync("consult", new { question = "In one sentence, what is the capital of France?" });

        // Assert
        var completed = await WaitForStderrLineAsync("Consultation completed", TimeSpan.FromSeconds(10));
        Assert.NotNull(completed);
        Assert.Matches(@"prompt=[1-9]\d* completion=[1-9]\d* reasoning=\d+", completed);
    }

    [Fact]
    public async Task Cancellation_AbortsPromptly_LogsCancelledLine()
    {
        // Arrange
        await StartLiveServerAsync();

        // Act
        var id = await SendToolCallAsync(
            "consult",
            new
            {
                question = "Compare optimistic and pessimistic concurrency control for distributed databases in " +
                    "exhaustive depth, covering at least ten distinct tradeoff dimensions.",
                effort = "max",
            });
        var started = await WaitForStderrLineAsync("Consultation started", TimeSpan.FromSeconds(15));
        Assert.NotNull(started);

        await SendNotificationAsync("notifications/cancelled", new { requestId = id, reason = "test" });

        // Assert
        var cancelled = await WaitForStderrLineAsync("Consultation cancelled", TimeSpan.FromSeconds(5));
        Assert.NotNull(cancelled);
        var completed = await WaitForStderrLineAsync("Consultation completed", TimeSpan.FromSeconds(10));
        Assert.Null(completed);
    }

    /// <summary>
    /// Resolves the OpenRouter API key from the same configuration hierarchy the server uses
    /// (McpConsultant project directory as the base path), but in-process, so the key never
    /// needs DOTNET_ENVIRONMENT=Development on the spawned child.
    /// </summary>
    private static string? ResolveApiKey()
    {
        var projectDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "..", "McpConsultant"));
        var config = new ConfigurationBuilder()
            .SetBasePath(projectDir)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var key = config["Consultant:ApiKey"];

        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    /// <summary>
    /// Shared skip-and-start prologue for every live test: opts in on MCPCONSULTANT_LIVE=1,
    /// skips when no key resolves, then starts the server with the resolved key.
    /// </summary>
    private async Task StartLiveServerAsync()
    {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("MCPCONSULTANT_LIVE") != "1",
            "Set MCPCONSULTANT_LIVE=1 to opt in to paid live tests.");
        var apiKey = ResolveApiKey();
        Assert.SkipWhen(apiKey is null, "No OpenRouter API key resolves; live tests skipped.");

        await StartAndInitializeServerAsync(apiKey!);
    }
}
