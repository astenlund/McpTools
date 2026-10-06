# McpKeep executive spec

## Outcome

Build McpKeep, a small Python MCP server using gkeepapi, for one configured Google account. Hermes runs on Windows on the same machine and launches the server over stdio. Include a working Hermes configuration example. HTTP hosting is outside this scope.

## Tool surface

Expose exactly these four tools, with machine-readable results and stable Google Keep note IDs:

| Tool | Behavior |
| --- | --- |
| `list_notes` | List active, archived, or all notes, with bounded pages and optional text filtering. Default to active notes. Return IDs and useful summaries for selecting a note. |
| `read_note` | Read a note by ID, including its text, checklist items, labels, and archive status. Preserve checklist order, checked state, and nesting in the returned representation. |
| `archive_note` | Archive one note by ID. Already archived is a successful no-op. |
| `unarchive_note` | Restore one archived note by ID. Already active is a successful no-op. |

Trashed and deleted notes are excluded from every operation. Unknown, trashed, deleted, and non-note IDs produce clear errors. Empty listings succeed with an empty page. Invalid filter and pagination arguments fail without performing a mutation. Pagination must communicate whether more results exist; documentation states how changes between calls affect traversal.

No creation, content editing, deletion, checklist changes, or bulk mutations. Archive and unarchive preserve titles, bodies, labels, checklist contents, and other unrelated note data. Image, drawing, and audio download is outside this scope; reading their containing notes still returns available textual content.

## Connection and authentication

Use stdio MCP with all diagnostics on stderr. Hermes owns subprocess startup and shutdown. Tool discovery must work before Google credentials are configured, so setup failures appear on tool calls instead of hiding the server's tool surface.

Use the agreed unofficial gkeepapi backend. Its Google master token has broad account access; the four-operation restriction is enforced by this server. Store the token outside the repository and configure its file path rather than putting the secret in Hermes configuration. Provide a separate local authentication setup command. Secrets must stay out of tool results and logs, including failure paths. Do not accept the Google account password as a tool argument.

One account is configured per server process. Document credential changes and recovery so restarting the server picks up replacement credentials. Do not persist note data or pending mutations to disk. A failed mutation must not remain queued and be sent by a later read or unrelated operation.

## Failure and recovery

Return clear MCP tool errors for missing or unreadable credentials, unavailable notes, authentication failures, and Google failures. Read current server data before deciding what to archive or unarchive. Serialize overlapping operations against shared client state.

Report success only after the archive change has synchronized successfully, or after observing the requested state for a no-op. If a write may have reached Google but confirmation failed, report the uncertain outcome and direct the caller to read the note before deciding whether to retry. Never turn an uncertain outcome into a success or automatically replay a failed write. Later reads must rebuild trustworthy state rather than upload pending mutations.

Protect reads and writes from consequential hangs and unbounded retry behavior. Local credential or argument errors should be actionable without disclosing secrets. Preserve user note content across all normal and failure paths.

## Acceptance evidence

Offline tests exercise the actual MCP protocol and gkeepapi data structures without contacting Google. Verify exactly four advertised tools, active/archive/all filtering, text filtering, bounded pagination, empty results, invalid arguments, checklist reads, unavailable-note errors, idempotent archive/unarchive, archive round trips, preservation of unrelated data, overlapping calls, and recovery after authentication, read, and write failures. Verify that sensitive backend error text cannot escape through MCP responses or stderr.

Include dependency locking, local authentication instructions, Windows setup commands, and the Hermes stdio configuration. Use the supported Python SDK surface verified against the installed package. Run scoped tests and relevant static checks, and distinguish offline evidence from live Google verification. If credentials are unavailable, deliver the tested server and setup instructions while explicitly reporting that live Google verification remains unperformed.

Deliver locally under the repository's Git conventions. Publication and deployment require separate user direction.
