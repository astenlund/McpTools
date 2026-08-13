namespace McpConsultant.Tests;

/// <summary>
/// Integration tests that start the actual McpConsultant server as a child process and
/// communicate with it over stdio. Tests the consult tool's validation error paths and
/// confirms the tools/list handshake advertises consult.
/// </summary>
public sealed class McpConsultantIntegrationTests(ITestOutputHelper output) : McpConsultantHarness(output)
{
    [Fact]
    public async Task ToolsList_IncludesConsult()
    {
        // Arrange / Act
        await StartAndInitializeServerAsync(apiKey: "dummy-key");

        // Assert
        Assert.NotNull(ToolsListResponse);
        Assert.Contains("\"consult\"", ToolsListResponse);
    }

    [Fact]
    public async Task Consult_WithEmptyApiKey_ReturnsMissingKeyError()
    {
        // Arrange
        await StartAndInitializeServerAsync(apiKey: string.Empty);

        // Act
        var response = await CallToolAsync("consult", new { question = "test question" });
        var text = ExtractToolText(response);

        // Assert
        Assert.NotNull(text);
        Assert.Contains("no OpenRouter API key is configured", text);
        Assert.Null(await WaitForStderrLineAsync("Consultation started", TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Consult_WithInvalidEffort_ReturnsAllowedValuesError()
    {
        // Arrange
        await StartAndInitializeServerAsync(apiKey: "dummy-key");

        // Act
        var response = await CallToolAsync("consult", new { question = "test", effort = "medium" });
        var text = ExtractToolText(response);

        // Assert
        Assert.NotNull(text);
        Assert.Contains("effort must be one of: low, high, max", text);
        Assert.Null(await WaitForStderrLineAsync("Consultation started", TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Consult_WithMissingFile_ReturnsFileError()
    {
        // Arrange - raw path; CallToolAsync serializes with JsonSerializer, which handles escaping
        await StartAndInitializeServerAsync(apiKey: "dummy-key");
        var missing = Path.Combine(Path.GetTempPath(), $"consultant-it-missing-{Guid.NewGuid():N}.txt");

        // Act
        var response = await CallToolAsync("consult", new { question = "test", files = new[] { missing } });
        var text = ExtractToolText(response);

        // Assert
        Assert.NotNull(text);
        Assert.Contains("does not exist", text);
        Assert.Null(await WaitForStderrLineAsync("Consultation started", TimeSpan.FromSeconds(1)));
    }
}
