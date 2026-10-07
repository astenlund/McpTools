---
status: exploring
---

# McpConsultant Follow-ups (post first slice)

Source: external second opinion on the graduated design spec ([`mcp-consultant.md`](mcp-consultant.md), stamp content e992c53c), obtained 2026-08-13 from openai/gpt-5.6-sol via OpenRouter using the spike script that dry-ran the spec's own request shape.

Gating: none of these entries block or modify the first slice. They are picked up only after the first slice has shipped. Entries that change the spec's design decisions go through a spec revision and re-graduation at that point.

## Structural (challenge a spec decision)

- Agent-aware attachment trust model. The spec's no-path-restriction anti-goal assumes a deliberate human caller, but the caller is an LLM agent that repository content can steer; the tool combines filesystem authority, an external network channel, and a paid credential (confused-deputy shape). Direction: restrict `files` to workspace or MCP-advertised roots by default, reject UNC and device paths, add an explicit `AllowFilesOutsideRoots` opt-in, surface MCP tool annotations (external-interaction, non-idempotent), and document that auto-approving the tool is unsafe. Supersedes the path-scope anti-goal when adopted.
- Streamed budget enforcement. The accepted metadata-to-read window lets normally-growing files (logs, build output) exceed `MaxAttachmentBytes` in memory and spend. Direction: keep metadata for early rejection, then stream reads with a hard cumulative cap (budget plus one byte detects overrun); consider extending the cap to the whole assembled input (question and context included) and lowering the 1 MiB default.
- OpenRouter data-governance requirements. The spec treats OpenRouter as an endpoint; it is a router to upstream providers with differing retention and training policies, plus fallback routing. Direction: specify provider-routing and retention (ZDR) requirements, whether fallback is permitted, and surface the resolved provider and model from the response where available.
- MCP-native error semantics. Errors are plain success-shaped strings; MCP supports `CallToolResult` with `isError: true`, which keeps failures distinguishable to clients and middleware. Direction: adopt `isError` if the .NET SDK's attributed tool surface permits; otherwise document the limitation in the spec.

## Hardening (additive, no decision reversed)

- Prompt-injection posture: the system persona instructs the consultant to treat context and file contents as untrusted evidence, not instructions, unless the question says otherwise.
- `CopyToPublishDirectory="Never"` on `appsettings.Development.json`, plus a publish-artifact check that the file is absent; Production-environment selection alone is not the protection.
- Consultation deadline covering file I/O: `TimeoutSeconds` guards only the HTTP call, but UNC metadata access, slow shares, and large reads can hang before HTTP begins. Direction: an overall deadline starting at tool entry, or rename the setting `ProviderTimeoutSeconds` and document the gap.
- Narrow the Overview claim that attachment contents "never enter the client's conversation context": the consultant's answer can quote them; the guarantee is only that the server does not place them in the request or transcript.

## Rebalancing (over-engineering identified relative to medium tier)

- Test strategy: replace most live provider-conformance probes with a fake HTTP endpoint behind an injectable base URI (deterministically covers timeout, cancellation, parsing, malformed responses, logging); keep one opt-in live canary. Several current probes do not establish what they claim (single-model results generalized to provider contract).
- Demote the eight-step validation order from public contract to implementation guidance; the testable invariant is only that local failures never produce a network request.
- Reconsider the VPN gating and hence the McpCommon extraction's message-parameter design: gating defaults to off and guards authenticated TLS traffic; if kept, a neutral shared exception with per-tool message formatting is cleaner than an optional presentation-text parameter on the shared service.
- Simplify multi-error file reporting if it drives any architecture: first-bad-path fail-fast is acceptable at this tier.
