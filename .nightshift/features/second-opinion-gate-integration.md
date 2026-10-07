---
status: exploring
---

# Second-opinion gate integration

Source: fit assessment of McpConsultant against the Nightshift plugin's second-opinion-gates feature (`.nightshift/features/second-opinion-gates.md` in the Nightshift repository), 2026-08-15. That feature adds cheap holistic reads by a different-model-family agent at lifecycle checkpoints; `consult` is the natural cross-family channel, since a Claude Code session can otherwise only spawn Claude-family subagents. The fit is close (stateless one-shot, per-call `model`, file attachments carrying the artifact plus scoped context), but three friction points surfaced. Each needs vetting before it graduates to a themed section; the contract question also decides how much of the integration lands in this repository versus in the Nightshift plugin.

## The three friction points

- **Follow-up probing over a stateless one-shot.** The gates design lets the controller exchange a bounded few messages with the second opinion to probe motivation and evidence behind a recommendation. `consult` has no session memory, so each probe turn must be a fresh call replaying the prior exchange (question, answer, follow-up) in `context`. Directions: document transcript-replay as the sanctioned pattern (cheap to build, and the exchange is recorded by construction, which the gates design requires anyway), or add conversation continuation to the server (a `transcript` parameter, or a short-lived server-side conversation handle). Replay multiplies prompt tokens per probe turn; that cost bound belongs in whichever direction is chosen.
- **A frozen, feature-detectable tool contract.** Nightshift is a public plugin and cannot hard-require this personal server; its skill would detect a consult-capable tool at runtime and fall back to a same-family higher-tier read otherwise. This repository's share is to declare a minimal stable contract (tool name `consult`, parameter names and semantics, the response header shape produced by `ResponseFormatter`, the structured error strings) and document it as the surface clients may feature-detect against, so detection needs no version coupling. The detection and fallback logic itself is Nightshift-side work and is tracked there, not here.
- **Schema-validated structured output.** Gate findings enter Nightshift's skeptic/controller pipeline, but `consult` returns free prose; a schema requested in the question text is unenforced, so the caller must salvage-parse. Direction: an optional structured-output mode (for example a response-schema parameter mapped to OpenRouter's `response_format` structured outputs where the routed model supports it), validated server-side with a structured error on mismatch, so callers get parse-or-error instead of best-effort extraction. Interacts with the MCP-native error semantics entry in [mcp-consultant-followups](mcp-consultant-followups.md).

## Deployment precondition worth noting

The hardened-spec gate reads a full spec at `effort: max`, which is exactly the long-running call the README's registration-time check exists for. A correctly configured `MCP_TOOL_TIMEOUT` is therefore a hard precondition for gate use, and the two registration-check quick wins in [QUICK_WINS.md](../QUICK_WINS.md) (threshold rewording, correlation ID) make that check trustworthy.
