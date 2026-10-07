# McpKeep

A small Google Keep MCP server for Hermes on Windows. It uses Python, the official MCP Python SDK, and the unofficial [gkeepapi client](https://github.com/kiwiz/gkeepapi). It is separate from the .NET solution.

## Tools

| Tool | Arguments | Result |
| --- | --- | --- |
| `list_notes` | `state`: `active` (default), `archived`, or `all`; optional `query`; `limit`: 1 to 200 (default 50); `offset`: nonnegative (default 0) | Note summaries (ID, title, type, archive and pin status, labels, and creation and last-edit times) and `next_offset`, or null when there are no more results. |
| `read_note` | `note_id` | The summary fields plus text, attachment counts (`images`, `drawings`, `audio`, `other`), web links, and ordered checklist items with checked state and parent item IDs. |
| `archive_note` | `note_id` | ID, archive status, and whether it changed. |
| `unarchive_note` | `note_id` | ID, archive status, and whether it changed. |

Use IDs returned by `list_notes`. Trashed and deleted notes are unavailable. There are no create, edit, delete, checklist-update, or bulk-mutation tools. Archive and unarchive leave content unchanged, and repeating the requested state is a successful no-op. Image, drawing, and audio downloads are not supported; `read_note` counts them so a caller can tell when the text alone is incomplete.

Times are ISO 8601 UTC with millisecond precision (`2019-05-04T03:02:01.123Z`), taken from Google's own metadata: `created` and `edited` (the last user edit). Either is null when Google reports it as unset. The SDK's locally generated update time is never returned.

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

An optional `--token-file` selects another absolute path outside a Git checkout. Existing files are never overwritten. To rotate credentials, create a token at a new path, update Hermes's configuration, and reload the MCP connection. The server never saves note data or pending mutations to disk; only the importer writes notes, into the vault you name.

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

The sync guard makes initial loading and confirmation receive-only. Archive payloads preserve original server metadata, including removed-label markers. A separate transport guard permits one mutation attempt and disables redirects for it, so SDK retries cannot resend a failed write. Read results take their times from captured server metadata rather than the hydrated SDK objects, and exclude removed label associations.

A write failure may mean Google applied the change before its reply was lost. In that case the error asks you to read the note's current state before deciding whether to retry. The server does not replay failed writes. Cancellation does not undo a change already accepted by Google.

## Importing into Obsidian

`mcp_keep.importer` copies notes into an Obsidian vault and archives them in Keep. It uses the server's worker and credentials but no model, so note text arrives unchanged.

For a manual run, set the account and token path in the current PowerShell session, using the same values as the Hermes configuration above:

```powershell
$env:KEEP_EMAIL = "a.stenlund@gmail.com"
$env:KEEP_MASTER_TOKEN_FILE = "C:/Users/asten/.config/mcp-keep/master.token"
C:/Git/McpTools/McpKeep/.venv/Scripts/python.exe -m mcp_keep.importer --vault C:/Sync/Obsidian/Personal --dry-run
```

Each run handles at most `--limit` notes (default 1), oldest creation time first. Pinned notes and notes carrying the `--skip-label` label (default `keep`, compared case-insensitively) stay in Keep. For each note the importer:

1. writes `<inbox>/<name>.md` (`--inbox`, default `Inbox`), named from the title, else the first line of content, without characters that Windows file names or Obsidian links reject, and with a ` (2)` suffix on a collision;
2. reads the file back and compares it byte for byte;
3. appends the Keep ID to `<vault>/.keep-import/ledger.jsonl`;
4. rechecks the current note against the copy, sends a fixed-size fingerprint to the archive worker, and records completion after a confirmed archive. The worker authorizes the mutation only when its own current snapshot has the same fingerprint.

The file opens with frontmatter: `keep_id`; `keep_title`; `keep_created` and `keep_edited` (Keep's UTC times); `tags` from the labels (spaces become hyphens, and characters a tag cannot hold are dropped); and `imported` (the local import time). Text is copied unchanged apart from line endings, which become LF. Checklists become Markdown tasks, nested with tabs. Web links not already in the text are listed under `## Links`.

A note with images, drawings, audio, or other attachments is imported with a warning callout and a `keep_not_copied` property, but left in Keep. Its warning is recorded as `held_pending_report`, printed and flushed immediately, then acknowledged as `held`. A later import failure cannot discard an earlier warning. An interrupted delivery is retried without rereading or rewriting the held note; a crash after output but before acknowledgement can repeat the warning. Save the media yourself, then archive the note in Keep; the importer never mutates it again. To deliberately import a held or completed note afresh, move its prior copy outside the inbox and remove every ledger line for its Keep ID, after reconciling any changes you want to keep.

Output is empty when there is nothing to report, so a scheduler can stay silent. A held note prints one line. A failure prints `Keep import failed: ...` and exits with 1. `--dry-run` prints what would be written and leaves Keep, vault notes, and the ledger unchanged; it can create the lock directory and file. An OS file lock (`--lock-file`, default `~/.config/mcp-keep/import.lock`) makes an overlapping run exit quietly.

An interrupted run resumes on the next one. A file written before its ledger line is found by its frontmatter `keep_id` and reused without rewriting it, after comparing it with the current note. Recovery streams each inbox Markdown file's frontmatter through its closing delimiter, retaining only the identity and import time; incomplete or ambiguous provenance stops the run. Those values may be double-quoted, single-quoted, or plain, with optional trailing YAML comments. Their text is preserved without converting timestamps to date objects. Collections, block scalars, indented continuations, non-string tags, multiple documents, and malformed YAML in Keep provenance stop the run rather than becoming another note identity. A `keep_id` example in ordinary note text is ignored.

An `imported` ledger entry means archive confirmation is pending. Such entries are checked before new imports, within the run's limit. If Keep already reports the note archived, the importer records completion without another mutation. Otherwise, it verifies the current note against the recorded file before attempting archive. Missing, unreadable, or changed copies and notes gone from Keep stop the run with a message naming the copy and the cause; changed content or new attachments are left in Keep. See "Resolving a stuck note" below. Pinned notes and notes with the skip label remain pending and do not block other eligible notes.

A confirmed archive appends a `completed` entry. Held and completed IDs are skipped forever, including after a completed note is restored or edited in Keep or its vault copy is moved or removed. Old ledgers have no completion entries: their `imported` IDs receive the same safe recovery checks once before completion is recorded. Old `held` entries are treated as already reported, since they retain no delivery evidence. A ledger line cut short mid-write is dropped; a complete but unreadable line stops the run. For repeated IDs, the last complete line determines the state.

### Resolving a stuck note

Persistent reconciliation failures must be addressed before the importer can finish the affected note or scan past unreadable provenance. Use the reported filename or Keep ID, when present, to locate the problem. Transient Keep read failures can instead return a generic error.

- The copy no longer matches its Keep note, or the note has new attachments: bring the copy up to date (save any media next to it), then archive the note in Keep yourself. Once the note is archived, the next run moves on.
- The copy is missing: restore it, for example from Resilio's archive or File History, or archive the note in Keep yourself if you no longer want it.
- The copy's `imported` time is missing or unreadable: restore that frontmatter line, or archive the note in Keep yourself to clear its pending archive. If the field also contains malformed or unsupported YAML, repair it or move the file outside the inbox; archiving alone does not prevent future inbox scans from stopping at that file.
- An inbox file's frontmatter is never closed or repeats `keep_id` or `imported`: repair the named file or move it outside the inbox.
- Several inbox files share a `keep_id`: locate the files bearing the reported ID and reconcile them so at most one copy remains in the inbox.
- The Keep note is in Keep's trash or deleted: restore it in Keep, and the next run archives it. If it is gone for good, mark it done by appending its last ledger line to `.keep-import/ledger.jsonl` again, with `imported` changed to `completed`:

```json
{"keep_id": "18c0ffee123.0123456789abcdef", "status": "completed", "path": "Inbox/Example.md", "at": "2026-10-07T04:15:30+02:00"}
```

### Hermes schedule

Hermes runs the importer as a script-only cron job, without a model. Hermes gives cron scripts a sanitized environment, so the launcher at `%LOCALAPPDATA%/hermes/scripts/keep-import.py` sets `KEEP_EMAIL`, `KEEP_MASTER_TOKEN_FILE`, and the importer options itself:

```powershell
hermes cron create "every 15m" --no-agent --script keep-import.py --interpreter C:/Git/McpTools/McpKeep/.venv/Scripts/python.exe --deliver signal --name keep-import --paused
```

This creates a paused job. After checking the launcher and a dry run, activate it with `hermes cron resume <job_id>`, using the ID returned by the create command.

Hermes delivers the importer's output, so only held notes and failures send a message.

## Verification

```powershell
uv run --project C:/Git/McpTools/McpKeep --frozen pytest C:/Git/McpTools/McpKeep/tests -q
uv run --project C:/Git/McpTools/McpKeep --frozen ruff check C:/Git/McpTools/McpKeep
uv run --project C:/Git/McpTools/McpKeep --frozen ruff format --check C:/Git/McpTools/McpKeep
```

The offline suite uses real gkeepapi note and checklist representations and exercises the stdio MCP protocol with a fake Google backend. It checks filtering, pagination, unavailable notes, server-sourced times, attachment counts, archive round trips, content preservation, credential errors, secret suppression, concurrency, and worker termination. It makes no Google requests. Live Google authentication and archive behavior remain separate verification steps after setup.

For a live check, ask Hermes to list and read notes first. Choose a note you want to archive, confirm it appears in the archived listing, then unarchive it and confirm it returns to the active listing with its content intact. Live verification has not been performed as part of this delivery.
