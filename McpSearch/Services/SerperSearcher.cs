using System.Text.Json;
using System.Text.Json.Serialization;
using McpSearch.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace McpSearch.Services;

/// <summary>
/// Service for searching the web using Serper.dev API.
/// </summary>
public class SerperSearcher
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<SerperSearcher> _logger;
    private readonly SearchSettings _searchSettings;
    private const string SerperApiUrl = "https://google.serper.dev/search";

    public SerperSearcher(
        HttpClient httpClient,
        ILogger<SerperSearcher> logger,
        IOptions<SearchSettings> searchSettings)
    {
        _httpClient = httpClient;
        _logger = logger;
        _searchSettings = searchSettings.Value;
    }

    // Serper.dev JSON response models
    private class SerperResponse
    {
        [JsonPropertyName("organic")]
        public List<SerperResult>? Organic { get; set; }
    }

    private class SerperResult
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("link")]
        public string? Link { get; set; }

        [JsonPropertyName("snippet")]
        public string? Snippet { get; set; }
    }

    /// <summary>
    /// Searches the web using Serper.dev API and returns search results.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="maxResults">Maximum number of results to return (default: 10).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of search results.</returns>
    public async Task<List<SearchResult>> SearchAsync(
        string query,
        int maxResults = 10,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("Query cannot be empty", nameof(query));
        }

        if (string.IsNullOrWhiteSpace(_searchSettings.SerperApiKey))
        {
            throw new InvalidOperationException(
                "Serper API key is not configured. Please set Search:SerperApiKey in appsettings.json " +
                "or use environment variable Search__SerperApiKey. " +
                "Get a free API key at https://serper.dev (2,500 queries/month free).");
        }

        _logger.LogInformation("Searching web for: {Query}", query);

        try
        {
            // Prepare Serper.dev request
            var requestBody = new
            {
                q = query,
                num = maxResults
            };

            var request = new HttpRequestMessage(HttpMethod.Post, SerperApiUrl)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(requestBody),
                    System.Text.Encoding.UTF8,
                    "application/json")
            };

            // Add API key header
            request.Headers.Add("X-API-KEY", _searchSettings.SerperApiKey);

            _logger.LogDebug("Requesting: {Url}", SerperApiUrl);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            // Parse JSON response
            var serperResponse = JsonSerializer.Deserialize<SerperResponse>(json);

            if (serperResponse?.Organic == null || serperResponse.Organic.Count == 0)
            {
                _logger.LogWarning("No search results found");
                return new List<SearchResult>();
            }

            // Convert to our SearchResult model
            var results = serperResponse.Organic
                .Take(maxResults)
                .Where(r => !string.IsNullOrEmpty(r.Title) && !string.IsNullOrEmpty(r.Link))
                .Select(r => new SearchResult
                {
                    Title = r.Title ?? string.Empty,
                    Url = r.Link ?? string.Empty,
                    Snippet = r.Snippet ?? string.Empty
                })
                .ToList();

            _logger.LogInformation("Found {Count} results for query: {Query}", results.Count, query);

            // Add rate limiting delay (1 second for Serper)
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);

            return results;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error while searching web via Serper.dev");
            throw;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Error parsing Serper.dev search results JSON");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching web for query: {Query}", query);
            throw;
        }
    }

}
