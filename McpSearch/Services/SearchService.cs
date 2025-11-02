using McpSearch.Models;
using Microsoft.Extensions.Logging;

namespace McpSearch.Services;

/// <summary>
/// Combined service that coordinates searching and content fetching.
/// </summary>
public class SearchService
{
    private readonly DuckDuckGoSearcher _searcher;
    private readonly ContentFetcher _contentFetcher;
    private readonly ILogger<SearchService> _logger;

    public SearchService(
        DuckDuckGoSearcher searcher,
        ContentFetcher contentFetcher,
        ILogger<SearchService> logger)
    {
        _searcher = searcher;
        _contentFetcher = contentFetcher;
        _logger = logger;
    }

    /// <summary>
    /// Performs a search and optionally fetches full content from results.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="maxResults">Maximum number of results (default: 10).</param>
    /// <param name="fetchContent">Whether to fetch full content from URLs (default: true).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of search results with optional full content.</returns>
    public async Task<List<SearchResult>> SearchAsync(
        string query,
        int maxResults = 10,
        bool fetchContent = true,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting search for: {Query} (maxResults: {MaxResults}, fetchContent: {FetchContent})",
            query, maxResults, fetchContent);

        // Step 1: Get search results
        var results = await _searcher.SearchAsync(query, maxResults, cancellationToken);

        if (results.Count == 0)
        {
            _logger.LogWarning("No results found for query: {Query}", query);
            return results;
        }

        // Step 2: Optionally fetch full content
        if (fetchContent)
        {
            _logger.LogInformation("Fetching full content for {Count} results", results.Count);

            var urls = results.Select(r => r.Url).ToList();
            var contents = await _contentFetcher.FetchMultipleAsync(urls, cancellationToken: cancellationToken);

            // Populate FullContent for each result
            foreach (var result in results)
            {
                if (contents.TryGetValue(result.Url, out var content))
                {
                    result.FullContent = content;
                }
            }

            var successCount = results.Count(r => r.FullContent != null);
            _logger.LogInformation("Successfully fetched full content for {SuccessCount}/{TotalCount} results",
                successCount, results.Count);
        }

        return results;
    }
}
