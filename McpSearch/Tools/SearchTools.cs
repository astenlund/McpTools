using System.ComponentModel;
using System.Text.Json;
using McpSearch.Services;
using ModelContextProtocol.Server;

namespace McpSearch.Tools;

/// <summary>
/// MCP tools for web search functionality.
/// </summary>
internal class SearchTools
{
    private readonly SearchService _searchService;

    public SearchTools(SearchService searchService)
    {
        _searchService = searchService;
    }

    /// <summary>
    /// Searches the web using DuckDuckGo and returns results with full content.
    /// </summary>
    /// <param name="query">The search query string.</param>
    /// <param name="maxResults">Maximum number of results to return (default: 10, max: 20).</param>
    /// <param name="fetchContent">Whether to fetch full HTML content from result URLs (default: true).</param>
    /// <returns>JSON array of search results with title, URL, snippet, and optional full content.</returns>
    [McpServerTool]
    [Description("Searches DuckDuckGo and returns web results with titles, URLs, snippets, and optionally full page content.")]
    public async Task<string> SearchWeb(
        [Description("The search query (e.g., 'C# async programming')")] string query,
        [Description("Maximum number of results to return (default: 10, max: 20)")] int maxResults = 10,
        [Description("Whether to fetch full HTML content from each result URL (default: true)")] bool fetchContent = true)
    {
        // Validate inputs
        if (string.IsNullOrWhiteSpace(query))
        {
            return JsonSerializer.Serialize(new
            {
                error = "Query cannot be empty",
                results = Array.Empty<object>()
            });
        }

        // Limit maxResults to reasonable range
        maxResults = Math.Clamp(maxResults, 1, 20);

        try
        {
            var results = await _searchService.SearchAsync(query, maxResults, fetchContent);

            // Convert to a simple serializable format
            var output = new
            {
                query = query,
                resultCount = results.Count,
                results = results.Select(r => new
                {
                    title = r.Title,
                    url = r.Url,
                    snippet = r.Snippet,
                    fullContent = fetchContent ? r.FullContent : null,
                    hasContent = r.FullContent != null
                }).ToList()
            };

            return JsonSerializer.Serialize(output, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                error = $"Search failed: {ex.Message}",
                query = query,
                results = Array.Empty<object>()
            });
        }
    }
}
