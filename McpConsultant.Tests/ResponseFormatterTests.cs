using McpConsultant.Services;

namespace McpConsultant.Tests;

public class ResponseFormatterTests
{
    [Fact]
    public void Stop_ProducesHeaderBlankAnswer_NoWarning()
    {
        // Arrange / Act
        var output = ResponseFormatter.FormatSuccess("openai/gpt-5.2", "high", "stop", "The answer.");

        // Assert
        Assert.Equal("Consulted openai/gpt-5.2 (effort: high)\n\nThe answer.", output);
    }

    [Fact]
    public void AbsentFinishReason_TreatedAsStop()
    {
        // Arrange / Act
        var output = ResponseFormatter.FormatSuccess("m", "low", null, "A");

        // Assert
        Assert.DoesNotContain("Warning:", output);
    }

    [Fact]
    public void NonStopFinishReason_EmitsLiteralWarningTemplate()
    {
        // Arrange / Act
        var output = ResponseFormatter.FormatSuccess("m", "max", "length", "Partial answer");

        // Assert
        Assert.Equal("Consulted m (effort: max)\nWarning: answer incomplete (finish_reason: length)\n\nPartial answer", output);
    }
}
