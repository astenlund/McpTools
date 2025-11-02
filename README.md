# McpSearch

🔍 A Model Context Protocol (MCP) server that brings DuckDuckGo web search to LM Studio and other MCP clients.

## What is this?

McpSearch is an MCP server that allows your local LLM (like those running in LM Studio) to search the web using DuckDuckGo. It fetches search results along with the full page content, enabling your AI assistant to access current information from the internet.

## Features

✅ **Web Search** - Search DuckDuckGo directly from your LLM
✅ **Full Content** - Optionally fetch complete HTML from result pages
✅ **Resilient** - Automatic retries with exponential backoff
✅ **Rate Limited** - Respectful delays to avoid overwhelming servers
✅ **Easy Setup** - Works with LM Studio, VS Code, and Visual Studio

## Quick Start

**Get started in 5 minutes:** See [QUICKSTART.md](QUICKSTART.md)

**Full documentation:** See [McpSearch/README.md](McpSearch/README.md)

## Example Usage

Once configured, you can ask your LLM:

> *"Search the web for the latest C# features"*

> *"Find information about async/await patterns"*

> *"What are the best practices for Model Context Protocol?"*

Your assistant will use the `SearchWeb` tool to fetch real-time results from DuckDuckGo!

## Installation

### 1. Build

```bash
cd McpSearch
dotnet build
```

### 2. Configure

Add to your MCP client's configuration file:

```json
{
  "mcpServers": {
    "McpSearch": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/McpSearch/McpSearch.csproj"]
    }
  }
}
```

### 3. Restart

Restart your MCP client (LM Studio, VS Code, etc.)

## Project Structure

```
McpSearch/
├── McpSearch/              # Main C# project
│   ├── Models/            # Data models
│   ├── Services/          # Search and content fetching services
│   ├── Tools/             # MCP tool definitions
│   └── README.md          # Detailed documentation
├── QUICKSTART.md          # Quick setup guide
└── README.md              # This file
```

## Requirements

- .NET 9.0 SDK or later
- LM Studio, VS Code with Copilot, or Visual Studio with Copilot
- Internet connection

## How It Works

1. **Search**: Scrapes DuckDuckGo HTML and parses results
2. **Fetch**: Retrieves full content from result URLs (optional)
3. **Return**: Provides JSON with titles, URLs, snippets, and content

Rate limiting and retry logic ensure reliable, respectful operation.

## Documentation

- **Quick Start:** [QUICKSTART.md](QUICKSTART.md) - Get running in 5 minutes
- **Full Docs:** [McpSearch/README.md](McpSearch/README.md) - Complete guide
- **MCP Spec:** [modelcontextprotocol.io](https://modelcontextprotocol.io/)

## Technology Stack

- **C# / .NET 9.0** - Modern, cross-platform framework
- **Model Context Protocol** - Open standard for LLM integrations
- **HtmlAgilityPack** - HTML parsing
- **Polly** - Resilience and retry policies

## Contributing

Contributions welcome! Please open an issue or pull request.

## License

See LICENSE file for details.

---

**Note:** This project uses web scraping as DuckDuckGo's official API doesn't provide search results. Be respectful with usage - rate limiting is built in.
