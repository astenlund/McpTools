using McpConsultant.Services;

namespace McpConsultant.Tests;

public class ErrorMessagesTests
{
    [Fact]
    public void StableSubstrings_CoverEveryClass_AndAreUnique()
    {
        // Arrange
        string[] expectedClasses =
        [
            "empty-question", "missing-api-key", "missing-default-model", "invalid-effort",
            "non-positive-setting", "vpn-not-connected", "file-not-fully-qualified", "file-missing",
            "file-directory", "file-over-budget", "file-unreadable", "file-binary",
            "file-wrong-encoding", "http-non-success", "unusable-response", "timeout",
            "network-failure", "unexpected-error",
        ];

        // Act
        var substrings = ErrorMessages.StableSubstrings;

        // Assert
        Assert.Equal(expectedClasses.Order().ToArray(), substrings.Keys.Order().ToArray());
        Assert.Equal(substrings.Count, substrings.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(substrings.Values, s => Assert.False(string.IsNullOrWhiteSpace(s)));
    }
}
