namespace McpSearch.Models;

/// <summary>
/// Represents a single search result from DuckDuckGo.
/// </summary>
public class SearchResult
{
    /// <summary>
    /// The title of the search result.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// The URL of the search result.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// A snippet/description of the search result.
    /// </summary>
    public string Snippet { get; set; } = string.Empty;

    /// <summary>
    /// The full HTML content from the URL (populated later).
    /// </summary>
    public string? FullContent { get; set; }
}
