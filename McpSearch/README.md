# McpSearch - DuckDuckGo Web Search MCP Server

A Model Context Protocol (MCP) server that provides web search capabilities using DuckDuckGo. Built with C# and designed for use with LM Studio and other MCP-compatible clients.

## Features

- 🔍 **Web Search**: Search DuckDuckGo and retrieve up to 20 results
- 📄 **Full Content**: Optionally fetch complete HTML content from result URLs
- 🔄 **Resilient**: Automatic retry with exponential backoff for failed requests
- ⏱️ **Rate Limited**: Respectful delays between requests (2s for searches, 1s for content)
- 🛡️ **Error Handling**: Graceful degradation when URLs fail to fetch
- 📊 **Detailed Logging**: Comprehensive logging to stderr for debugging

## Installation

### Prerequisites

- .NET 9.0 SDK or later
- LM Studio or another MCP-compatible client

### Building from Source

1. Clone the repository:
```bash
git clone <repository-url>
cd McpSearch
```

2. Build the project:
```bash
cd McpSearch
dotnet build
```

3. Run locally for testing:
```bash
dotnet run
```

## Configuration

### For LM Studio

Add the following to LM Studio's MCP configuration file:

**Location:** `%APPDATA%\LM Studio\mcp_config.json` (Windows) or `~/.lmstudio/mcp_config.json` (macOS/Linux)

```json
{
  "mcpServers": {
    "McpSearch": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "C:\\path\\to\\McpSearch\\McpSearch.csproj"
      ]
    }
  }
}
```

**Note:** Update the path to match your actual project location.

### For VS Code (Copilot/GitHub Copilot)

Create or update `.vscode/mcp.json` in your workspace:

```json
{
  "servers": {
    "McpSearch": {
      "type": "stdio",
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "/absolute/path/to/McpSearch/McpSearch.csproj"
      ]
    }
  }
}
```

### For Visual Studio (Copilot)

Create or update `.mcp.json` in your solution directory:

```json
{
  "servers": {
    "McpSearch": {
      "type": "stdio",
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "C:\\absolute\\path\\to\\McpSearch\\McpSearch.csproj"
      ]
    }
  }
}
```

## Usage

Once configured, the MCP server exposes the following tool:

### `SearchWeb`

Searches DuckDuckGo and returns results with optional full content.

**Parameters:**
- `query` (string, required): The search query
- `maxResults` (int, optional): Maximum number of results (1-20, default: 10)
- `fetchContent` (bool, optional): Whether to fetch full HTML content (default: true)

**Returns:**
```json
{
  "query": "C# async programming",
  "resultCount": 10,
  "results": [
    {
      "title": "Async and Await in C# - Microsoft Learn",
      "url": "https://example.com/page",
      "snippet": "Learn about asynchronous programming...",
      "fullContent": "<html>...</html>",
      "hasContent": true
    }
  ]
}
```

### Example Prompts

Ask your LLM assistant:

- *"Search the web for the latest C# features"*
- *"Find information about async/await patterns"*
- *"Search for 'Model Context Protocol' and give me 5 results without full content"*

## How It Works

1. **Search Phase**:
   - Scrapes DuckDuckGo's HTML version (`html.duckduckgo.com`)
   - Parses search results using HtmlAgilityPack
   - Extracts titles, URLs, and snippets
   - 2-second delay after search (rate limiting)

2. **Content Fetch Phase** (if enabled):
   - Fetches full HTML from each result URL
   - Uses Polly for retry logic (3 attempts with exponential backoff)
   - 1-second delay between each fetch
   - 30-second timeout per request
   - Graceful handling of failed fetches

## Rate Limiting

To be respectful to DuckDuckGo's servers:

- **Searches**: 2-second delay after each search
- **Content Fetching**: 1-second delay between URL fetches
- **Retries**: Exponential backoff (1s, 2s, 4s)

## Architecture

```
McpSearch/
├── Models/
│   └── SearchResult.cs          # Data model for search results
├── Services/
│   ├── DuckDuckGoSearcher.cs    # Handles DuckDuckGo search & parsing
│   ├── ContentFetcher.cs        # Fetches full content with retries
│   └── SearchService.cs         # Orchestrates search + content fetching
├── Tools/
│   └── SearchTools.cs           # MCP tool definitions
└── Program.cs                   # Application entry point & DI setup
```

## Development

### Running Locally

```bash
cd McpSearch
dotnet run
```

The server will start and listen on stdio for MCP protocol messages.

### Building Release

```bash
dotnet build -c Release
```

### Publishing as Package

```bash
dotnet pack -c Release
```

The package will be created in `bin/Release/` and can be published to NuGet.org.

## Troubleshooting

### "No results found"

- DuckDuckGo may have changed their HTML structure
- Check stderr logs for parsing errors
- Try a different search query

### "Connection timeout"

- Check your internet connection
- Some URLs may be slow or unresponsive
- Results will include partial data (URLs that succeed)

### "Rate limit errors"

- Built-in delays should prevent this
- If you see rate limit warnings, increase delays in the code

## Logging

All logs are sent to stderr (not stdout, which is reserved for MCP protocol). Set log level in your MCP client configuration if needed.

## Limitations

- Uses web scraping (DuckDuckGo's official API doesn't provide search results)
- HTML structure changes may break parsing
- Rate limiting adds latency (necessary to be respectful)
- Full content fetching significantly increases response time

## Future Enhancements

- [ ] Content extraction (remove HTML tags, keep main text)
- [ ] Caching for repeated queries
- [ ] Support for other search engines
- [ ] Image search support
- [ ] Search result filtering

## License

See LICENSE file for details.

## Contributing

Contributions are welcome! Please open an issue or pull request.

## Resources

- [Model Context Protocol Documentation](https://modelcontextprotocol.io/)
- [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk)
- [LM Studio MCP Guide](https://lmstudio.ai/docs/mcp)
- [VS Code MCP Documentation](https://code.visualstudio.com/docs/copilot/chat/mcp-servers)
