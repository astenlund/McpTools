using McpWeb.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace McpWeb.Tests;

public class ContextTests
{
    [Fact]
    public async Task GetContext_ReportsDstAdjustedUtcOffset()
    {
        // Arrange
        var context = new Context(NullLogger<Context>.Instance);
        var offset = TimeZoneInfo.Local.GetUtcOffset(DateTime.Now);
        var abs = offset.Duration();
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var expected = abs.Minutes == 0
            ? $"(UTC{sign}{abs.Hours})"
            : $"(UTC{sign}{abs.Hours}:{abs.Minutes:D2})";

        // Act
        var result = await context.GetContext();

        // Assert
        Assert.Contains(expected, result);
    }
}
