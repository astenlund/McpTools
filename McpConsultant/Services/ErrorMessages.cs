namespace McpConsultant.Services;

/// <summary>
/// Single source of truth for every error class's user-facing text.
/// The stable substring per class is the test contract; surrounding prose may change freely.
/// </summary>
internal static class ErrorMessages
{
    internal static readonly IReadOnlyDictionary<string, string> StableSubstrings = new Dictionary<string, string>
    {
        ["empty-question"] = "question cannot be empty",
        ["missing-api-key"] = "no OpenRouter API key is configured (Consultant:ApiKey)",
        ["missing-default-model"] = "no model was given and Consultant:DefaultModel is not configured",
        ["invalid-effort"] = "effort must be one of: low, high, max",
        ["non-positive-setting"] = "must be a positive number",
        ["vpn-not-connected"] = "Consultation unavailable:",
        ["file-not-fully-qualified"] = "is not a fully qualified path",
        ["file-missing"] = "does not exist",
        ["file-directory"] = "is a directory, not a file",
        ["file-over-budget"] = "exceed the attachment budget",
        ["file-unreadable"] = "could not be read",
        ["file-binary"] = "appears to be binary (contains a NUL byte)",
        ["file-wrong-encoding"] = "is not valid UTF-8",
        ["http-non-success"] = "OpenRouter returned HTTP",
        ["unusable-response"] = "response was unusable",
        ["timeout"] = "timed out after",
        ["network-failure"] = "network failure calling OpenRouter",
        ["unexpected-error"] = "Unexpected error during consultation",
    };

    internal static string EmptyQuestion() =>
        $"Error: the {StableSubstrings["empty-question"]}.";

    internal static string MissingApiKey() =>
        $"Error: {StableSubstrings["missing-api-key"]}. Set it in appsettings.Development.json locally or the registration env block when deployed.";

    internal static string MissingDefaultModel() =>
        $"Error: {StableSubstrings["missing-default-model"]}.";

    internal static string InvalidEffort(string value) =>
        $"Error: {StableSubstrings["invalid-effort"]} (got '{value}').";

    internal static string NonPositiveSetting(string settingName, long value) =>
        $"Error: Consultant:{settingName} {StableSubstrings["non-positive-setting"]} (got {value}).";

    internal static string VpnNotConnected(string message) =>
        $"{StableSubstrings["vpn-not-connected"]} {message}";

    internal static string HttpNonSuccess(int statusCode, string bodyExcerpt) =>
        $"Error: {StableSubstrings["http-non-success"]} {statusCode}. Body excerpt: {bodyExcerpt}";

    internal static string UnusableResponse(string bodyExcerpt) =>
        $"Error: the OpenRouter {StableSubstrings["unusable-response"]} (unparseable, no choices, or empty content). Body excerpt: {bodyExcerpt}";

    internal static string Timeout(int timeoutSeconds) =>
        $"Error: the consultation {StableSubstrings["timeout"]} {timeoutSeconds} seconds (Consultant:TimeoutSeconds; raising it is an option).";

    internal static string NetworkFailure(string detail) =>
        $"Error: {StableSubstrings["network-failure"]}: {detail}";

    internal static string UnexpectedError(string detail) =>
        $"Error: {StableSubstrings["unexpected-error"]}: {detail}";
}
