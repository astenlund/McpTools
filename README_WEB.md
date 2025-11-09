# McpWeb

🔍 A Model Context Protocol (MCP) server that brings web search, URL fetching, and temporal context capabilities to LM Studio and other MCP clients.

## What is this?

McpWeb is an MCP server that allows your local LLM (like those running in LM Studio) to search the web, fetch content from specific URLs, and access current date/time information. It provides search results with full page content and temporal context, enabling your AI assistant to access current information from the internet with accurate date awareness.

## Features

✅ **Web Search** - Search the web directly from your LLM using Serper.dev
✅ **URL Fetching** - Fetch full HTML content from specific URLs
✅ **Temporal Context** - Get current date, time, timezone, and week information
✅ **Full Content** - Optionally fetch complete HTML from search result pages
✅ **Security** - SSRF prevention (blocks localhost/private IPs)
✅ **Optional VPN** - Configurable VPN requirement per tool (search/fetch)
✅ **Resilient** - Automatic retries with exponential backoff
✅ **Rate Limited** - Respectful delays to avoid overwhelming servers
✅ **Easy Setup** - Works with LM Studio, VS Code, and Visual Studio

## Quick Start

**Full documentation:** See [CLAUDE.md](CLAUDE.md)

## Example Usage

Once configured, you can ask your LLM:

**Web Search:**
> *"Search the web for the latest C# features"*

> *"Find information about async/await patterns"*

> *"What are the best practices for Model Context Protocol?"*

**URL Fetching:**
> *"Fetch the content from https://example.com"*

> *"Get me the HTML from this documentation page: https://docs.microsoft.com/..."*

**Temporal Context:**
> *"What's today's date?"*

> *"What year is it right now?"*

> *"Search for tech news from this week"* (LLM will use get_context to know the current date)

Your assistant will use the `search_web`, `fetch_url`, and `get_context` tools to access real-time web content with accurate date awareness!

## Installation

### 1. Build

```bash
cd McpWeb
dotnet build
```

### 2. Configure

**Important:** You'll need a Serper.dev API key (free tier: 2,500 queries/month). Get one at https://serper.dev

**Option A: Environment Variable (Production)**
```bash
export Search__SerperApiKey="your-api-key-here"
```

**Option B: Development File (Local Development - Recommended)**

Create `McpWeb/appsettings.Development.json` (this file is git-ignored):
```json
{
  "Search": {
    "SerperApiKey": "your-api-key-here"
  }
}
```

**⚠️ Security:** Never commit API keys to git! The `appsettings.Development.json` file is automatically ignored by git.

**VPN Settings (Optional):**

You can also configure VPN requirements in your Development file:
```json
{
  "Search": {
    "SerperApiKey": "your-api-key-here",
    "RequireVpn": false
  },
  "Fetch": {
    "RequireVpn": false
  }
}
```

- `Search:RequireVpn` - Require VPN for web searches (default: `true`)
- `Fetch:RequireVpn` - Require VPN for URL fetching (default: `false`)

Add to your MCP client's configuration file:

```json
{
  "mcpServers": {
    "web": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/McpWeb/McpWeb.csproj"]
    }
  }
}
```

Or use the published executable for better performance:

```json
{
  "mcpServers": {
    "web": {
      "command": "/path/to/McpWeb/bin/Release/net9.0/win-x64/publish/McpWeb.exe",
      "args": [],
      "env": {
        "Search__SerperApiKey": "your-api-key-here"
      }
    }
  }
}
```

### 3. Restart

Restart your MCP client (LM Studio, VS Code, etc.)

## Project Structure

```
McpTools/
├── McpWeb/                 # Main C# project
│   ├── Models/            # Data models and settings
│   ├── Services/          # Search, VPN detection, content fetching
│   ├── Tools/             # MCP tool definitions (Search, Fetch, Context)
│   └── Program.cs         # Entry point and DI configuration
├── McpWeb.Tests/          # Integration tests
├── CLAUDE.md              # Detailed documentation and architecture
└── README_WEB.md          # This file
```

## Requirements

- .NET 9.0 SDK or later
- LM Studio, VS Code with Copilot, or Visual Studio with Copilot
- Internet connection

## How It Works

**Three MCP Tools Provided:**

1. **`search_web`** - Web search via Serper.dev API
   - Queries search engine for results
   - Optionally fetches full HTML from result URLs
   - Returns titles, URLs, snippets, and content

2. **`fetch_url`** - Direct URL content fetching
   - Fetches full HTML from a specific URL
   - SSRF protection (blocks localhost/private IPs)
   - Returns complete HTML content

3. **`get_context`** - Current date/time/timezone information
   - Returns current date and time
   - Provides timezone name and UTC offset
   - Includes day of week and week number
   - Helps LLMs use accurate dates in queries and responses

Rate limiting, retry logic, and security validation ensure reliable, safe operation.

## Documentation

- **Full Docs:** [CLAUDE.md](CLAUDE.md) - Complete architecture and development guide
- **MCP Spec:** [modelcontextprotocol.io](https://modelcontextprotocol.io/)

## Technology Stack

- **C# / .NET 9.0** - Modern, cross-platform framework
- **Model Context Protocol** - Open standard for LLM integrations
- **Serper.dev API** - Web search provider
- **Polly** - Resilience and retry policies

## Contributing

Contributions welcome! Please open an issue or pull request.

## License

See LICENSE file for details.

---

**Note:** Requires a Serper.dev API key. Free tier includes 2,500 queries per month.
