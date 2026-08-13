namespace McpConsultant.Services;

/// <summary>Thrown when the per-request timeout source fired (as opposed to the client's own token).</summary>
public class ConsultationTimeoutException(TimeSpan timeout)
    : Exception($"The consultation exceeded the configured timeout of {(int)timeout.TotalSeconds} seconds")
{
    public TimeSpan Timeout { get; } = timeout;
}
