using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

namespace McpConsultant.Tests;

/// <summary>
/// Records each formatted log message so tests can assert on lifecycle lines.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<string> messages = new();

    public IReadOnlyList<string> Messages => [.. messages];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        messages.Enqueue(formatter(state, exception));
    }
}
