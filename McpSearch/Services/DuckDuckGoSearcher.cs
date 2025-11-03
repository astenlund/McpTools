using HtmlAgilityPack;
using McpSearch.Models;
using Microsoft.Extensions.Logging;

namespace McpSearch.Services;

/// <summary>
/// Service for searching DuckDuckGo and parsing results.
/// </summary>
public class DuckDuckGoSearcher
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<DuckDuckGoSearcher> _logger;

    public DuckDuckGoSearcher(HttpClient httpClient, ILogger<DuckDuckGoSearcher> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        // Set browser-like headers to avoid being blocked by DuckDuckGo
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }
        if (!_httpClient.DefaultRequestHeaders.Contains("Accept"))
        {
            _httpClient.DefaultRequestHeaders.Add("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
        }
        if (!_httpClient.DefaultRequestHeaders.Contains("Accept-Language"))
        {
            _httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        }
        if (!_httpClient.DefaultRequestHeaders.Contains("Accept-Encoding"))
        {
            _httpClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
        }
        if (!_httpClient.DefaultRequestHeaders.Contains("DNT"))
        {
            _httpClient.DefaultRequestHeaders.Add("DNT", "1");
        }
        if (!_httpClient.DefaultRequestHeaders.Contains("Connection"))
        {
            _httpClient.DefaultRequestHeaders.Add("Connection", "keep-alive");
        }
        if (!_httpClient.DefaultRequestHeaders.Contains("Upgrade-Insecure-Requests"))
        {
            _httpClient.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1");
        }
    }

    /// <summary>
    /// Searches DuckDuckGo and returns search results.
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

        _logger.LogInformation("Searching DuckDuckGo for: {Query}", query);

        try
        {
            // Use POST instead of GET (less likely to be blocked)
            var searchUrl = "https://html.duckduckgo.com/html/";

            var formContent = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("q", query),
                new KeyValuePair<string, string>("b", ""), // Empty "next" parameter
                new KeyValuePair<string, string>("kl", "us-en") // Language/region
            });

            var response = await _httpClient.PostAsync(searchUrl, formContent, cancellationToken);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(cancellationToken);

            // Parse the results
            var results = ParseSearchResults(html, maxResults);

            _logger.LogInformation("Found {Count} results for query: {Query}", results.Count, query);

            // Add rate limiting delay (2 seconds)
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

            return results;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error while searching DuckDuckGo");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching DuckDuckGo for query: {Query}", query);
            throw;
        }
    }

    /// <summary>
    /// Parses HTML to extract search results.
    /// </summary>
    private List<SearchResult> ParseSearchResults(string html, int maxResults)
    {
        var results = new List<SearchResult>();
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // DuckDuckGo HTML uses <div class="result"> for each search result
        var resultNodes = doc.DocumentNode.SelectNodes("//div[contains(@class, 'result') and contains(@class, 'results_links')]");

        if (resultNodes == null || resultNodes.Count == 0)
        {
            _logger.LogWarning("No search results found in HTML");
            return results;
        }

        foreach (var resultNode in resultNodes.Take(maxResults))
        {
            try
            {
                // Extract the title and URL from the link
                var linkNode = resultNode.SelectSingleNode(".//a[contains(@class, 'result__a')]");
                if (linkNode == null)
                {
                    _logger.LogDebug("Skipping result node without link");
                    continue;
                }

                var title = HtmlEntity.DeEntitize(linkNode.InnerText?.Trim() ?? string.Empty);
                var url = linkNode.GetAttributeValue("href", string.Empty);

                // DuckDuckGo sometimes uses redirect URLs, extract the actual URL
                if (url.Contains("uddg="))
                {
                    var uddgIndex = url.IndexOf("uddg=");
                    if (uddgIndex >= 0)
                    {
                        var uddgStart = uddgIndex + 5; // Length of "uddg="
                        var uddgEnd = url.IndexOf('&', uddgStart);
                        var uddgValue = uddgEnd > 0
                            ? url.Substring(uddgStart, uddgEnd - uddgStart)
                            : url.Substring(uddgStart);
                        url = Uri.UnescapeDataString(uddgValue);
                    }
                }

                // Ensure URL is absolute
                if (url.StartsWith("//"))
                {
                    url = "https:" + url;
                }
                else if (!url.StartsWith("http"))
                {
                    url = "https://" + url;
                }

                // Extract the snippet/description
                var snippetNode = resultNode.SelectSingleNode(".//a[contains(@class, 'result__snippet')]");
                var snippet = snippetNode != null
                    ? HtmlEntity.DeEntitize(snippetNode.InnerText?.Trim() ?? string.Empty)
                    : string.Empty;

                // Only add if we have at least a title and URL
                if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(url))
                {
                    results.Add(new SearchResult
                    {
                        Title = title,
                        Url = url,
                        Snippet = snippet
                    });

                    _logger.LogDebug("Parsed result: {Title} - {Url}", title, url);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error parsing individual search result");
                // Continue processing other results
            }
        }

        return results;
    }
}
