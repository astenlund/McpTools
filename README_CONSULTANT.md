# McpConsultant

## What it is

McpConsultant is a Model Context Protocol (MCP) server that gives Claude Code (or any MCP client) a single `consult` tool for getting a second opinion from an external model via the OpenRouter API. Typical uses: reviewing a spec draft, sanity-checking a proposed solution to a difficult problem, or getting an independent take on a design decision. Every consultation is a stateless one-shot request: file attachments are read from disk and sent directly to OpenRouter, so their contents never enter the client's conversation context; the client spends tokens only on the file paths and on the consultant's answer.

## Configuration

Section name: `Consultant`. Seven settings:

| Setting | Default | Notes |
|---|---|---|
| `ApiKey` | none | Required at call time; your OpenRouter API key. Missing, empty, or whitespace-only counts as absent. |
| `DefaultModel` | `openai/gpt-5.2` | OpenRouter model ID used when the `model` tool parameter is omitted. |
| `SystemPrompt` | built-in persona | Override to change the consultant's character. An empty or whitespace value falls back to the built-in persona. |
| `DefaultEffort` | `high` | Reasoning effort used when the `effort` tool parameter is omitted. One of `low`, `high`, `max`. |
| `RequireVpn` | `false` | Require Mullvad VPN connectivity before a consultation runs. |
| `TimeoutSeconds` | `300` | Per-request timeout for the OpenRouter call. Reasoning-heavy models can take minutes. |
| `MaxAttachmentBytes` | `1048576` | Total byte budget across all attached files. |

For local development, create `McpConsultant/appsettings.Development.json` (this file is git-ignored) with your API key:

```json
{
  "Consultant": {
    "ApiKey": "your-api-key-here"
  }
}
```

**Security:** Never commit API keys to git. `appsettings.Development.json` is automatically ignored by git.

## Building and publishing

```bash
dotnet build
```

Publish a self-contained single-file executable:

```bash
dotnet publish McpConsultant/McpConsultant.csproj -c Release -r win-x64
# Creates single-file executable in McpConsultant/bin/Release/net10.0/win-x64/publish/
```

## Registering in Claude Code

The registered executable runs in the default (Production) environment: nothing in an MCP stdio registration sets `DOTNET_ENVIRONMENT`, so `appsettings.Development.json` is never loaded on the deployed path. Supply the API key in the registration itself, via an `env` block carrying `Consultant__ApiKey`:

```json
{
  "mcpServers": {
    "consultant": {
      "command": "/path/to/McpConsultant/bin/Release/net10.0/win-x64/publish/McpConsultant.exe",
      "args": [],
      "env": {
        "Consultant__ApiKey": "your-api-key-here"
      }
    }
  }
}
```

Without this, a consultation registered without the `env` block gets the missing-key structured error on every call, because the local-development `appsettings.Development.json` convention only helps `dotnet run` and tests, not the deployed exe.

### Client tool timeout

The 300-second default `TimeoutSeconds` only helps if the registered client itself waits that long for a `tools/call` response, so the effective ceiling is the smaller of `TimeoutSeconds` and the client's own MCP tool timeout. In Claude Code that ceiling is governed by `MCP_TOOL_TIMEOUT` (the tool-execution timeout; the similarly named `MCP_TIMEOUT` bounds server startup and is irrelevant to a running `tools/call`). `MCP_TOOL_TIMEOUT` is read by the Claude Code client process itself, so set it in the client's own environment (the shell or system environment Claude Code starts under, or the top-level `env` of its settings) and explicitly not in the registration's per-server `env` block above, which configures only the spawned server process, where nothing reads it. Claude Code's unit for this variable is milliseconds, so for the default 300-second `TimeoutSeconds`:

```bash
MCP_TOOL_TIMEOUT=300000
```

### Registration-time check

Run this once at registration, with no other consultation in flight. The single-in-flight precondition matters because the check pairs stderr log lines by timestamp rather than by a correlation identifier, so an overlapping consultation makes the pairing ambiguous.

**Part one, configuration inspection:** confirm `MCP_TOOL_TIMEOUT`, interpreted in milliseconds, is greater than or equal to `TimeoutSeconds` converted to milliseconds (`300000` for the default `TimeoutSeconds` of 300). If the variable is unset, Claude Code's own built-in default governs instead of `TimeoutSeconds`, so set it explicitly per the instruction above rather than relying on that default.

**Part two, a smoke call:** run one consultation through the registered client, pinned to a reasoning model at `effort: max` with a deliberately heavy prompt, and read its elapsed time from the timestamps on the `Consultation started` and `Consultation completed` stderr lines. Elapsed time must exceed 100 seconds to establish that the call actually exercised the long-running path; a run that finishes at or under 100 seconds is inconclusive, not a pass, and should be re-run with a heavier prompt. If that precondition is met and the answer arrives through the client, the check passes.

To find the server's stderr: Claude Code persists each registered server's stderr to its per-server MCP log files, which is where the `Consultation started` and `Consultation completed` timestamped lines are read from.

## Upgrading

While a Claude Code session is live, the registered server process holds the published single-file executable open, so publishing over the same output path fails on a locked file on Windows. Upgrade order:

1. Disconnect or exit the MCP client session.
2. Publish (`dotnet publish McpConsultant/McpConsultant.csproj -c Release -r win-x64`).
3. Reconnect.

A running server keeps serving the old binary until that restart.

## Documentation

- **Full Docs:** [CLAUDE.md](CLAUDE.md) - Complete architecture and development guide
- **Design Spec:** [.claude/features/mcp-consultant.md](.claude/features/mcp-consultant.md) - Review-graduated design record
- **MCP Spec:** [modelcontextprotocol.io](https://modelcontextprotocol.io/)
