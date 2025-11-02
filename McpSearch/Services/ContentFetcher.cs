using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace McpSearch.Services;

/// <summary>
/// Service for fetching full content from URLs with retry logic and rate limiting.
/// </summary>
public class ContentFetcher
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ContentFetcher> _logger;
    private readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;

    public ContentFetcher(HttpClient httpClient, ILogger<ContentFetcher> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        // Configure retry policy: 3 attempts with exponential backoff (1s, 2s, 4s)
        _retryPolicy = Policy
            .HandleResult<HttpResponseMessage>(r => !r.IsSuccessStatusCode)
            .Or<HttpRequestException>()
            .Or<TaskCanceledException>()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt - 1)),
                onRetry: (outcome, timespan, retryCount, context) =>
                {
                    _logger.LogWarning(
                        "Retry {RetryCount} after {Delay}s due to: {Reason}",
                        retryCount,
                        timespan.TotalSeconds,
                        outcome.Exception?.Message ?? outcome.Result?.StatusCode.ToString() ?? "Unknown");
                });

        // Set timeout for individual requests
        _httpClient.Timeout = TimeSpan.FromSeconds(30);

        // Set User-Agent if not already set
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }
    }

    /// <summary>
    /// Fetches the full HTML content from a URL.
    /// </summary>
    /// <param name="url">The URL to fetch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The HTML content, or null if fetch failed.</returns>
    public async Task<string?> FetchContentAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            _logger.LogWarning("Cannot fetch content: URL is empty");
            return null;
        }

        _logger.LogDebug("Fetching content from: {Url}", url);

        try
        {
            // Execute with retry policy
            var response = await _retryPolicy.ExecuteAsync(async () =>
            {
                return await _httpClient.GetAsync(url, cancellationToken);
            });

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogDebug("Successfully fetched {Length} characters from {Url}", content.Length, url);
                return content;
            }
            else
            {
                _logger.LogWarning("Failed to fetch {Url}: Status {StatusCode}", url, response.StatusCode);
                return null;
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error fetching content from {Url}", url);
            return null;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogError(ex, "Timeout fetching content from {Url}", url);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching content from {Url}", url);
            return null;
        }
    }

    /// <summary>
    /// Fetches full content for multiple URLs with rate limiting.
    /// </summary>
    /// <param name="urls">List of URLs to fetch.</param>
    /// <param name="delayBetweenRequests">Delay between requests (default: 1 second).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Dictionary mapping URLs to their content (null if fetch failed).</returns>
    public async Task<Dictionary<string, string?>> FetchMultipleAsync(
        IEnumerable<string> urls,
        TimeSpan? delayBetweenRequests = null,
        CancellationToken cancellationToken = default)
    {
        var delay = delayBetweenRequests ?? TimeSpan.FromSeconds(1);
        var results = new Dictionary<string, string?>();
        var urlList = urls.ToList();

        _logger.LogInformation("Fetching content from {Count} URLs with {Delay}s delay between requests",
            urlList.Count, delay.TotalSeconds);

        for (int i = 0; i < urlList.Count; i++)
        {
            var url = urlList[i];

            // Add rate limiting delay before each request (except the first)
            if (i > 0)
            {
                await Task.Delay(delay, cancellationToken);
            }

            var content = await FetchContentAsync(url, cancellationToken);
            results[url] = content;
        }

        var successCount = results.Values.Count(c => c != null);
        _logger.LogInformation("Successfully fetched content from {SuccessCount}/{TotalCount} URLs",
            successCount, urlList.Count);

        return results;
    }
}
