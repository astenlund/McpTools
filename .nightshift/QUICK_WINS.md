# Quick wins

Refactors ready to land when time allows; not blocking any feature, but would improve the codebase meaningfully.

This file is **one of four repo-local indexes** Claude reads on every session start (alongside `FEATURES.md`, `BUGS.md`, `PATTERNS.md`). Active entries are kept inline, organized under thematic `##` sections you invent as work emerges. When a quick win lands, append a shipped-note entry to [`QUICK_WINS_HISTORY.md`](QUICK_WINS_HISTORY.md); do not move it within this file. Negative-knowledge findings (approaches attempted and reverted) are first-class promotion candidates from the history into the relevant `.nightshift/patterns/<slug>.md` Cautionary tales sections.

Capture shorthand: name the refactor, describe the current smell in a sentence or two, sketch the preferred shape. A reader should be able to start work from the entry alone. Anchor entries on identifiers that survive refactors -- symbol names, entry titles, commit hashes, config keys -- never on line numbers, plan-phase ordinals, bullet positions, or temporal qualifiers ("new", "recent"): a precise locator that rots misleads harder than a coarse one that holds.

**After adding a new entry, run `/nightshift:ready`** from the repo root to confirm it parses as a quick-wins work item against the real grammar in `skills/ready/ready.js`. Quick wins carry no `**Requires:**` line; the failure mode to catch is an entry that doesn't parse as a `- ` bullet or `###` heading (ready reports it as a prose-only-section notice) while you can still fix it in the same session.

## Registration-time check

- **Reword or strengthen the smoke-call threshold in `README_CONSULTANT.md`.** Part two of the registration-time check treats an elapsed time over 100 seconds as establishing the long-running path, but exceeding 100 seconds only proves the client tolerates more than 100 seconds, not the full `TimeoutSeconds` ceiling (300 by default). Either present the criterion as the proxy it is (evidence that `MCP_TOOL_TIMEOUT` overrode Claude Code's shorter built-in default) or extend the procedure to drive a call near the configured ceiling.

## Consultation logging

- **Log validation errors as `Consultation failed` lines.** The `Consult` tool method in `McpConsultant/Tools/Consultant.cs` returns the errors it detects by its own validation before any request (empty question, invalid effort, missing default model, non-positive `TimeoutSeconds` or `MaxAttachmentBytes`, missing API key, file validation failures) without logging anything, while exception-mapped errors go through `LogAndReturn` and log `Consultation failed [id]: <error>`. An operator grepping stderr therefore finds no trace of a rejected call. Route those returns through `LogAndReturn` as well, add a `ConsultantToolTests` case asserting a validation error logs a failed line with the call's ID, and change the Error handling summary in `.nightshift/features/mcp-consultant.md` back to saying every structured error is logged.

## (add sections as work emerges)

## History

Implemented quick wins are archived in [`QUICK_WINS_HISTORY.md`](QUICK_WINS_HISTORY.md), read only when consulted (not at session start) so the active backlog above stays scannable. When a quick win lands, append its entry there rather than to this file.
