# McpKeep

A small Google Keep MCP server for Hermes on Windows. It uses Python, the official MCP Python SDK, and the unofficial [gkeepapi client](https://github.com/kiwiz/gkeepapi). It is separate from the .NET solution.

## Tools

| Tool | Arguments | Result |
| --- | --- | --- |
| `list_notes` | `state`: `active` (default), `archived`, or `all`; optional `query`; `limit`: 1 to 200 (default 50); `offset`: nonnegative (default 0) | Note summaries and `next_offset`, or null when there are no more results. |
| `read_note` | `note_id` | Text, labels, archive and pin status, and ordered checklist items with checked state and parent item IDs. |
| `archive_note` | `note_id` | ID, archive status, and whether it changed. |
| `unarchive_note` | `note_id` | ID, archive status, and whether it changed. |

Use IDs returned by `list_notes`. Trashed and deleted notes are unavailable. There are no create, edit, delete, checklist-update, or bulk-mutation tools. Archive and unarchive leave content unchanged, and repeating the requested state is a successful no-op. Image, drawing, and audio downloads are not supported.

The list query matches a case-insensitive substring of title or text. Results are ordered by note ID. Pass `next_offset` back as `offset` while retaining the same filters. Each call fetches current data; additions, removals, and archive changes between pages can cause skips or repeats. Pagination is not a frozen snapshot.

## Windows setup

Install [uv](https://docs.astral.sh/uv/getting-started/installation/), then prepare the locked environment:

```powershell
uv sync --project C:/Git/McpTools/McpKeep --frozen
```

Python 3.10 or later is required. uv can select an installed compatible Python or download a managed one. The checked-in lockfile pins runtime dependencies, including MCP 2.3.0 and gkeepapi 0.17.1.

## Authentication

gkeepapi uses Google's private mobile API and requires a master token with broad account access. The four-operation restriction belongs to this server; it is not a Google permission scope. Protect the token as a password. See [gkeepapi authentication](https://gkeepapi.readthedocs.io/en/latest/#authenticating) and [gpsoauth's alternative login flow](https://github.com/simon-weber/gpsoauth#alternative-flow).

1. Open [Google EmbeddedSetup](https://accounts.google.com/EmbeddedSetup), sign in, and agree when prompted. A loading screen may remain visible.
2. Use the browser's developer tools to obtain the `oauth_token` cookie for that page.
3. Run the local setup command and paste the cookie into its hidden prompt:

```powershell
uv run --project C:/Git/McpTools/McpKeep --frozen mcp-keep-auth --email a.stenlund@gmail.com
```

Use your Google account email if it differs from the example. The setup command stores the resulting master token at `C:/Users/asten/.config/mcp-keep/master.token` for this Windows user. It removes inherited file permissions and grants the current Windows username full control before writing the token. On other systems it creates the file with mode 0600. The token and pasted cookie are never printed.

An optional `--token-file` selects another absolute path outside a Git checkout. Existing files are never overwritten. To rotate credentials, create a token at a new path, update Hermes's configuration, and reload the MCP connection. Note data and pending mutations are never saved to disk.

## Hermes configuration

Add this entry under `mcp_servers` in `~/.hermes/config.yaml`:

```yaml
mcp_servers:
  keep:
    command: "C:/Git/McpTools/McpKeep/.venv/Scripts/python.exe"
    args: ["-m", "mcp_keep.server"]
    env:
      KEEP_EMAIL: "a.stenlund@gmail.com"
      KEEP_MASTER_TOKEN_FILE: "C:/Users/asten/.config/mcp-keep/master.token"
      KEEP_TIMEOUT_SECONDS: "120"
    timeout: 180
    connect_timeout: 30
    supports_parallel_tool_calls: false
    tools:
      include: [list_notes, read_note, archive_note, unarchive_note]
      prompts: false
      resources: false
```

Set the account and token path to your chosen values. Hermes filters subprocess environment variables, so pass these settings explicitly. Reload with `/reload-mcp` or restart Hermes. Hermes supports local stdio servers, so this setup needs no HTTP listener. [Hermes MCP documentation](https://hermes-agent.nousresearch.com/docs/user-guide/features/mcp/)

Tools remain discoverable before credentials are configured. Missing credentials, invalid configuration, and backend failures become MCP tool errors. `KEEP_TIMEOUT_SECONDS` must be an integer from 10 to 300; keep Hermes's tool timeout longer than that value.

Each operation authenticates a fresh backend process and synchronizes current notes, so large accounts can take longer to read. Calls are serialized and share a deadline covering queueing and worker execution. Timed-out workers are terminated; each worker also enforces its own deadline if its parent disappears. Failed local changes cannot be uploaded by a later read.

The sync guard makes initial loading and confirmation receive-only. Archive payloads preserve original server metadata, including removed-label markers. A separate transport guard permits one mutation attempt and disables redirects for it, so SDK retries cannot resend a failed write. Read results omit SDK-generated update timestamps and exclude removed label associations.

A write failure may mean Google applied the change before its reply was lost. In that case the error asks you to read the note's current state before deciding whether to retry. The server does not replay failed writes. Cancellation does not undo a change already accepted by Google.

## Verification

```powershell
uv run --project C:/Git/McpTools/McpKeep --frozen pytest C:/Git/McpTools/McpKeep/tests -q
uv run --project C:/Git/McpTools/McpKeep --frozen ruff check C:/Git/McpTools/McpKeep
uv run --project C:/Git/McpTools/McpKeep --frozen ruff format --check C:/Git/McpTools/McpKeep
```

The offline suite uses real gkeepapi note and checklist representations and exercises the stdio MCP protocol with a fake Google backend. It checks filtering, pagination, unavailable notes, archive round trips, content preservation, credential errors, secret suppression, concurrency, and worker termination. It makes no Google requests. Live Google authentication and archive behavior remain separate verification steps after setup.

For a live check, ask Hermes to list and read notes first. Choose a note you want to archive, confirm it appears in the archived listing, then unarchive it and confirm it returns to the active listing with its content intact. Live verification has not been performed as part of this delivery.
