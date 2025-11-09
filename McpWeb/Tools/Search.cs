using System.ComponentModel;
using McpWeb.Exceptions;
using McpWeb.Models;
using McpWeb.Services;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace McpWeb.Tools;

/// <summary>
/// MCP tool for web search functionality.
/// </summary>
internal class Search
{
    private readonly SearchService _searchService;
    private readonly VpnDetectionService _vpnDetectionService;
    private readonly SearchSettings _searchSettings;

    // Track recent searches to prevent loops (query -> timestamp)
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _recentSearches = new();
    private static readonly TimeSpan _duplicateWindow = TimeSpan.FromSeconds(30);

    public Search(
        SearchService searchService,
        VpnDetectionService vpnDetectionService,
        IOptions<SearchSettings> searchSettings)
    {
        _searchService = searchService;
        _vpnDetectionService = vpnDetectionService;
        _searchSettings = searchSettings.Value;
    }

    /// <summary>
    /// Searches the web and returns results with full content.
    /// </summary>
    /// <param name="query">The search query string.</param>
    /// <param name="maxResults">Maximum number of results to return (default: 10, max: 20).</param>
    /// <param name="fetchContent">Whether to fetch full HTML content from result URLs (default: false).</param>
    /// <returns>JSON array of search results with title, URL, snippet, and optional full content.</returns>
    [McpServerTool]
    [Description("Searches the web and returns complete results with titles, URLs, and snippets. Call this tool ONCE per query - it returns all needed information. Do not call again unless answering a new user question.")]
    public async Task<string> SearchWeb(
        [Description("The search query (e.g., 'C# async programming')")] string query,
        [Description("Maximum number of results to return (default: 10, max: 20)")] int maxResults = 10,
        [Description("Whether to fetch full HTML content from each result URL (default: false)")] bool fetchContent = false)
    {
        // Validate inputs
        if (string.IsNullOrWhiteSpace(query))
        {
            return "Error: Search query cannot be empty.";
        }

        // Check for duplicate search within time window
        var normalizedQuery = query.Trim().ToLowerInvariant();
        var now = DateTime.UtcNow;

        if (_recentSearches.TryGetValue(normalizedQuery, out var lastSearchTime))
        {
            var timeSinceLastSearch = now - lastSearchTime;
            if (timeSinceLastSearch < _duplicateWindow)
            {
                return $"DUPLICATE SEARCH DETECTED: This exact query \"{query}\" was already searched {timeSinceLastSearch.TotalSeconds:F0} seconds ago. " +
                       $"The results are already available above. DO NOT search again. Use the previous search results to answer the user's question.";
            }
        }

        // Cleanup old entries (older than 5 minutes)
        var cutoffTime = now.Subtract(TimeSpan.FromMinutes(5));
        var oldEntries = _recentSearches.Where(kvp => kvp.Value < cutoffTime).Select(kvp => kvp.Key).ToList();
        foreach (var oldEntry in oldEntries)
        {
            _recentSearches.TryRemove(oldEntry, out _);
        }

        // Limit maxResults to reasonable range
        maxResults = Math.Clamp(maxResults, 1, 20);

        try
        {
            // Check VPN if required
            if (_searchSettings.RequireVpn)
            {
                _vpnDetectionService.EnsureVpnConnected();
            }

            var results = await _searchService.SearchAsync(query, maxResults, fetchContent);

            // Update last search time ONLY after successful search
            _recentSearches[normalizedQuery] = now;

            // Format as human-readable text for better LLM comprehension
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Search completed successfully. Found {results.Count} results for \"{query}\":");
            sb.AppendLine();

            for (int i = 0; i < results.Count; i++)
            {
                var result = results[i];
                sb.AppendLine($"{i + 1}. {result.Title}");
                sb.AppendLine($"   URL: {result.Url}");
                sb.AppendLine($"   {result.Snippet}");

                if (fetchContent && !string.IsNullOrEmpty(result.FullContent))
                {
                    // Truncate full content to avoid overwhelming the LLM
                    var content = result.FullContent.Length > 1000
                        ? result.FullContent.Substring(0, 1000) + "..."
                        : result.FullContent;
                    sb.AppendLine($"   Content preview: {content}");
                }

                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine($"Search complete. Use the information above to answer the user's question. Do not search again unless the user asks a new question.");

            return sb.ToString();
        }
        catch (VpnNotConnectedException ex)
        {
            return $"Search unavailable: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Search failed for \"{query}\": {ex.Message}";
        }
    }
}
