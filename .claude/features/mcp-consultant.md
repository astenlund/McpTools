# McpConsultant Design Spec

Date: 2026-08-13 Status: Implemented (first slice shipped; see FEATURES_HISTORY.md)

## Overview

McpConsultant is a new stdio MCP server in the McpTools solution. It gives an LLM client (primarily Claude Code) a single `consult` tool for obtaining a second opinion from an external model via the OpenRouter API. Typical uses: reviewing a spec draft, sanity-checking a proposed solution to a difficult problem, or getting an independent take on a design decision.

McpConsultant is a separate server rather than a fourth tool inside McpWeb. The fourth-tool alternative was considered and rejected: it is less code (McpWeb already hosts three tools in one process), but those three serve one web-centric purpose (search, fetch, and the temporal context that keeps search queries accurate), and a consultant would mix an unrelated domain into that server, grow the tool list clients see even when they only want web tools, and couple consultant releases to search-server releases. The one-domain-per-server principle is already established in this repository by McpImageGen being planned as its own server. The accepted cost of the separate server is the McpCommon extraction below and a copied test harness.

OpenRouter is the provider gateway rather than a direct provider API (Anthropic's or OpenAI's, the rejected alternative) because the tool's `model` override is meant to reach any current frontier model: OpenRouter gives one integration, one API key, and one request shape across hundreds of models from every major provider, including a unified `reasoning` contract that it translates per provider. Direct provider APIs would mean one integration and one key per provider and no cross-provider override. The accepted costs are an intermediary in the request path and OpenRouter's small fee margin on inference.

The server is stateless: every consultation is a one-shot request. Multi-turn behavior, when needed, is achieved by the client including prior discussion in the `context` parameter of a new call.

A key data-flow property: file attachments are read by the server from disk and sent directly to OpenRouter. Their contents never enter the client's conversation context; the client spends tokens only on file paths and on the consultant's answer.

## Operating context

The six operating-context inputs, with each judgment boundary recorded as a deviation entry:

- Deployment environment and operational criticality: local stdio process on the author's development machine, launched on demand by Claude Code. Not operationally critical; an outage costs a missed second opinion. Deviation entry: uplift `deployment_criticality` judged not fired on this basis.
- Audience: the repository is personal tooling (the author's own MCP servers, single developer). Deviation entry: the four audience components map to category `personal use`; no external consumers exist.
- Failure consequence and data or security sensitivity: answers are advisory, so a wrong or failed consultation wastes one paid API call at worst. File contents are sent to OpenRouter only on deliberate, per-call user invocation, and the API key follows the established git-ignored `appsettings.Development.json` convention. Deviation entry: uplift `failure_consequence` judged not fired on this basis.
- Concurrency and compatibility risk: stateless one-shot calls, a single client, no shared mutable state; MCP SDK surface identical to the already-working McpWeb. Deviation entry: uplift `concurrency_compatibility` judged not fired on this basis.
- Reversibility and recovery cost: no persisted state anywhere; any defect is fixed by editing code and republishing. Deviation entry: uplift `reversibility_recovery` judged not fired on this basis.
- Expected feature lifetime: long-lived; modeled on McpWeb, which the repository describes as production-ready and actively used, and expected to become a durable part of the daily workflow. Deviation entry: uplift `expected_lifetime` judged fired on this basis.

Derivation rule (stated here so the derivation is reproducible from this document alone): the audience category fixes a baseline tier (personal use and trusted circle: low; paying customers: medium; organization and public: high), and each fired uplift raises the tier one step, capped at high. Applied: baseline `low` from audience, one fired uplift, settled tier `medium`. Per-dimension effort: validation `medium`, recovery `medium`, compatibility `medium`, observability `medium`, proof effort `medium`.

## Goals

- One `consult` tool: question in, expert answer out.
- File attachment by path, so large material bypasses the client's context window.
- Reasoning effort control with a condensed three-value ladder.
- Same operational conventions as McpWeb: stdio transport, stderr logging, layered configuration, structured error strings instead of exceptions. One deliberate logging deviation: the console formatter sets a `TimestampFormat` (McpWeb's does not), so stderr lines are timestamped; the registration-time smoke probe under Testing reads elapsed time from those timestamps, and they cost nothing in ordinary use.

## Non-goals (explicit anti-goals)

- Streaming responses.
- Conversation state or sessions on the server.
- Multi-model panels, consensus, or model routing logic.
- Cost budgeting or spend limits (token usage is logged, not enforced).
- Binary file attachments (attachments are UTF-8 text only).
- A model-listing tool (the client is expected to know OpenRouter model IDs).
- Automatic retry or backoff for transient OpenRouter failures (429 rate limits, upstream 5xx). Consultations are stateless and cheap to re-issue, so the caller retries manually; these statuses surface through the ordinary non-2xx structured error. This deliberately diverges from `ContentFetcher`'s Polly retry policy: a consultation is a paid, minutes-long call, so a silent 3x retry would multiply spend and overrun the client's tool timeout, whereas the page fetches `ContentFetcher` retries are free and take seconds; `SerperSearcher`, the repo's other paid-API client, already follows the no-retry convention.
- Restricting which absolute paths `files` may name (no allowlist, sandbox, or repository boundary). The server trusts its single local caller, which already has full disk access of its own; any readable absolute path may be attached deliberately, and the deliberate-invocation point in the Operating context section is the security rationale.
- A duplicate-invocation guard (McpWeb's `search_web` suppresses identical queries within a 30-second window). Consultations are deliberate, expensive, and infrequent, and an identical-looking repeat is often legitimate (the attached files changed on disk between calls), so repeat calls are always honored.

## Solution structure

Two new projects added to `McpTools.sln`, plus one new shared library:

```
McpCommon/                        new class library (net10.0)
  Services/VpnDetectionService.cs   moved from McpWeb
  Exceptions/VpnNotConnectedException.cs  moved from McpWeb
McpConsultant/                    new stdio MCP server (net10.0)
  Program.cs
  Models/ConsultantSettings.cs
  Services/OpenRouterClient.cs
  Tools/Consultant.cs
  appsettings.json                committed, no secrets
  appsettings.Development.json    git-ignored, created locally, holds the API key
McpConsultant.Tests/              xunit v3 test project
```

`McpConsultant.csproj` mirrors McpWeb's whole publishing and visibility posture, not only its copy items: `RuntimeIdentifiers` including `win-x64`; `PublishSelfContained` and `PublishSingleFile` scoped to publish so regular builds stay framework-dependent (a build-time self-contained setting breaks the test project's `ProjectReference` to the server project, the regression this repository already hit and fixed once; framework-dependent builds are also what let the integration tests spawn the server with `dotnet run --no-build`); tool classes `internal` with `InternalsVisibleTo` for `McpConsultant.Tests`, and service classes public, matching McpWeb exactly (its tools are internal, its services public).

### McpCommon extraction

`VpnDetectionService` and `VpnNotConnectedException` move from McpWeb to McpCommon so both servers share one implementation. Namespaces change from `McpWeb.Services` / `McpWeb.Exceptions` to `McpCommon.Services` / `McpCommon.Exceptions`.

The move is behavior-preserving for McpWeb, with one deliberate addition rather than a pure move: the parameterless `VpnNotConnectedException` constructor hardcodes search-specific wording ("SEARCH UNAVAILABLE: ... before retrying the search."), and `VpnDetectionService.EnsureVpnConnected()` is its only throw site, so a consultation refused through the unchanged path would carry a self-contradictory search message. `EnsureVpnConnected` therefore gains an optional failure-message parameter; when the parameter is null or omitted the current search-specific text is thrown verbatim, so every existing McpWeb call site keeps byte-identical behavior without edits, and McpConsultant passes its own consultation-specific message. Two alternatives that leave the shared signature untouched were rejected: having McpConsultant call the boolean `IsMullvadVpnActive()` probe directly and produce its own refusal (rejected because the gate-plus-throw semantics would then live in two shapes, the exception path for McpWeb and an ad-hoc boolean path for McpConsultant, and future gate changes such as logging or additional checks would have to be made twice), and catching `VpnNotConnectedException` to substitute the message text (rejected because it hardcodes the knowledge that the default message is search-specific into a second server and silently breaks if that text is ever reworded).

Complete list of existing code consumers to update:

- `McpWeb/Program.cs`: DI registration of `VpnDetectionService` (using update only).
- `McpWeb/Tools/Search.cs`: injects the service, catches `VpnNotConnectedException` (using updates only).
- `McpWeb/Tools/Fetch.cs`: injects the service, catches `VpnNotConnectedException` (using updates only).
- `McpWeb.Tests/VpnDetectionTests.cs`: tests the service directly (using update only).
- `McpWeb/McpWeb.csproj`: gains a project reference to McpCommon.
- `McpWeb/appsettings.json`: its `Logging.LogLevel` block carries the key `"McpWeb": "Debug"`, which is a logger-category prefix rule, and `VpnDetectionService`'s category changes from `McpWeb.Services...` to `McpCommon.Services...` with the move; the block gains `"McpCommon": "Debug"` so the service's Debug lines keep flowing. This is a configuration consumer the McpWeb.Tests pass condition is structurally blind to (those tests build their own logger factory and never load appsettings.json), which is why it is enumerated here rather than left to the test gate. McpConsultant's own `appsettings.json` carries the complete Logging block, mirroring McpWeb's shape with both category roots: `Default` Information, `Microsoft` Warning, `McpConsultant` Debug, and `McpCommon` Debug, the last entry present from the start so the shared service's Debug lines are reachable for the identical reason.

Complete list of documentation updates. The first three entries are consumers of the moved types (they currently place those types inside McpWeb and become wrong after the move); the rest are the places where a second active server must appear:

- `CLAUDE.md`, "Service Layers (Dependency Flow)": the `VpnDetectionService` entry moves to a note that the service lives in McpCommon and is shared by both servers, and the numbered dependency-flow enumeration itself is scoped as McpWeb's (gaining a sibling McpConsultant flow: Consult tool, OpenRouterClient), so the list does not silently read as full repo coverage once a second active server exists. Any other CLAUDE.md list naming the repo's tools exhaustively (the "Tools Provided" list) is likewise either scoped to McpWeb explicitly or extended with `consult`.
- `CLAUDE.md`, "Project Structure Notes", McpWeb section: the `Services/` and `Exceptions/` descriptions drop VPN detection and `VpnNotConnectedException`; a McpCommon entry is added, and a McpConsultant subsection is added alongside the existing per-project subsections (the section carries one per project, including the not-yet-implemented McpImageGen).
- `README_WEB.md`, project-structure listing: the `Services/` line drops VPN detection or points at McpCommon.
- `CLAUDE.md`, repository overview: gains a McpConsultant section alongside McpWeb and McpImageGen.
- `CLAUDE.md`, "Common Development Commands": every server-specific command there names only McpWeb (`dotnet run --project McpWeb/...`, the publish line, the single-test filter naming `McpWebIntegrationTests`); the section gains the McpConsultant equivalents.
- `CLAUDE.md`, "Configuration System": the closed enumeration "Configuration sections: `Search`, `Fetch`, `Logging`" and the env-var/command-line format examples gain the `Consultant` section and its `Consultant__ApiKey` / `--Consultant:ApiKey` forms, and the subsection's closing bullet "Each feature (Search, Fetch) has independent VPN requirement setting" gains Consultant.
- `CLAUDE.md`, "VPN Detection Mechanism": the per-feature disable list (`Search:RequireVpn`, `Fetch:RequireVpn`) gains `Consultant:RequireVpn` with its default of false.
- A new `README_CONSULTANT.md` documents setup, configuration, and usage, matching the role `README_WEB.md` plays for McpWeb.

## Tool surface

One tool, exposed as `consult` (from C# method `Consult`, hosted in class `Consultant`; the class name deliberately differs from the method because C# forbids a member sharing its enclosing type's name).

| Parameter | Required | Type | Meaning |
|---|---|---|---|
| `question` | yes | string | The thing the caller wants a second opinion on. |
| `context` | no | string | Inline supporting material: spec excerpts, prior analysis, constraints. |
| `files` | no | string[] | Absolute paths to UTF-8 text files the server reads and includes. |
| `model` | no | string | OpenRouter model ID overriding `DefaultModel`. |
| `effort` | no | string | Reasoning effort: one of `low`, `high`, `max`. Overrides `DefaultEffort`. |

The table's Meaning column seeds the client-facing `[Description]` attribute strings (the text an LLM client reads in `tools/list` when deciding whether and how to call the tool); exact wording is finalized during implementation, like the persona text. The tool-level description must convey the two behaviors the design relies on the client knowing: when consulting is worthwhile (a second opinion on a spec, design, or hard problem, not routine lookups), and that `files` sends content server-side so the caller should pass paths instead of inlining large material.

### Parameter resolution and validation, all branches

Optional string parameters use one uniform rule: omitted, empty, and whitespace-only are all treated as absent (the `IsNullOrWhiteSpace` idiom).

- `question` empty or whitespace: structured error, no request sent.
- `context` absent: the assembled message contains no context section.
- `files` omitted or an empty array: the assembled message contains no file sections; this is a valid call.
- `model` absent: `DefaultModel` from configuration is used. If `DefaultModel` is also missing or whitespace, structured error.
- `effort` absent: `DefaultEffort` from configuration is used. Effort values (from either source) are validated case-insensitively (ordinal ignore case) against exactly the set {`low`, `high`, `max`}; any other value, including a misconfigured `DefaultEffort`, yields a structured error listing the allowed values. The validated value is forwarded to OpenRouter verbatim in lowercase. No mapping is performed: all three are native OpenRouter effort values, and OpenRouter owns per-provider translation (for example `max` degrading on models without that tier).
- `TimeoutSeconds` and `MaxAttachmentBytes` must be positive; zero or negative values yield a structured error naming the setting, the same treatment as a misconfigured `DefaultEffort`. A value that fails type binding altogether (a non-numeric string in the JSON) throws from .NET options binding when `IOptions<ConsultantSettings>.Value` is first read; the terminal catch-all returns it as the unexpected-error class, stderr-logged like every error. That mapping carries a stated precondition, which deliberately deviates from McpWeb's tool convention: the `Consultant` tool class keeps the injected `IOptions<ConsultantSettings>` as a field and reads `.Value` as the first statement inside `Consult`'s try block, never in the constructor. McpWeb's tools read `.Value` in their constructors, where a binding exception would be thrown during tool activation, before any catch exists, and would surface as a raw SDK-level error instead. Binding stays lazy (no `ValidateOnStart` machinery), so no startup-time failure exists for this class.

Why the effort ladder is condensed: OpenRouter's native ladder is `none`, `minimal`, `low`, `medium`, `high`, `xhigh`, `max`. The tool accepts only `low`, `high`, `max` because the caller is an LLM choosing under uncertainty, and three clearly separated tiers (sanity check, workhorse, hardest problems) produce better choices than seven near-neighbors; the dropped values add granularity without changing any decision the caller actually faces. `DefaultEffort` is `high` rather than `max` because the tool exists for hard questions (so the default should think hard) but the top tier's token cost is reserved for explicit escalation.

### Validation order

Checks run in this fixed order, and the first failure produces the call's structured error (earlier classes shadow later ones):

1. `question` non-blank.
2. `effort` resolution and membership in {`low`, `high`, `max`}.
3. `model` resolution (parameter else `DefaultModel`).
4. `TimeoutSeconds` and `MaxAttachmentBytes` positivity.
5. `ApiKey` presence.
6. VPN connectivity, only when `RequireVpn` is true.
7. File validation as specified under File attachment rules (metadata checks and budget before any content is read, then content checks).
8. The OpenRouter request.

This order is normative: it makes cheap local-input validation independent of network preconditions, and the integration tests below rely on it (the invalid-effort and missing-file tests supply a dummy key that is never used, because their failing steps precede the request at step 8; no network access can occur).

### File attachment rules

Validation runs over the complete `files` array before any request is sent, and any failure fails the whole call. Rationale: a second opinion silently based on partial material is worse than no answer.

Validation proceeds in two stages, and the stage order is normative: all metadata checks complete, including the budget, before any file content is read, so an over-budget call is rejected without allocating a byte of content. One residual window is accepted rather than eliminated: a file that grows or is replaced between the metadata pass and the content read is read at its new size, because re-checking the budget during reads would add machinery the declared threat model (a single trusted local caller, no adversary) does not warrant. The neighboring exists-to-metadata window is handled by the metadata-fault rule below.

Duplicate elements are deduplicated among the elements that pass the fully-qualified check: the qualified paths are compared case-insensitively (ordinal ignore case, matching Windows file system semantics on the published `win-x64` target), keeping the first occurrence, so a repeated path is validated once, counted once against the budget, and emitted once in the assembled message. Elements that fail the fully-qualified check are never qualified or deduplicated; each such value fails its branch, and the single error response lists each distinct failing value once. Deduplication is silent (no error class), because a duplicate is harmless caller sloppiness rather than a defect in the material.

Stage 1, metadata checks (per path, then the budget over all paths):

- Path is not fully qualified: error naming the path. The normative predicate is `Path.IsPathFullyQualified`, chosen over `Path.IsPathRooted` because on Windows the rooted check accepts drive-relative (`C:foo`) and root-relative (`\foo`) forms, which are exactly the working-directory-dependent shapes this branch exists to exclude. A null, empty, or whitespace element inside a non-empty `files` array fails this same branch, short-circuited by an `IsNullOrWhiteSpace` guard that runs before the predicate is invoked: the guard is load-bearing, not stylistic, because `Path.IsPathFullyQualified` throws `ArgumentNullException` on null rather than returning false, so an unguarded per-element predicate call would misroute a null element to the unexpected-error class instead of this branch. (Stated explicitly so no reader has to derive it from the parameter-level whitespace rule, which covers only scalar optional parameters.)
- Path does not exist, or is a directory: error naming the path.
- Combined size of the attached files, taken from file metadata (`FileInfo.Length`), exceeds `MaxAttachmentBytes`: error listing each file with its size in bytes plus the configured limit, so the caller can trim the list. The sum ranges over only the paths that passed the per-path checks above; a path that already failed contributes nothing (its size may be unobtainable), and its own error is what reports it. If metadata access itself faults on a path that passed the existence check (the file vanished in between, an unreachable share), that path is reported under the unreadable class, in stage 1, rather than throwing.

Stage 2, content checks (only reached when stage 1 passes in full):

- File cannot be read (IO or permission error): error naming the path and the underlying message.
- File content contains a NUL byte: error naming the path, treated as binary. Attachments are UTF-8 text only.
- File bytes are not valid UTF-8 (decoded strictly, with a throwing decoder rather than replacement characters): error naming the path, treated as wrong encoding. This closes the gap left by the NUL heuristic alone: a legacy Windows-1252 file with accented characters contains no NUL byte but must not be silently sent with U+FFFD substitutions.

Within each stage the error message reports every failing path in one response, not just the first, so the caller can fix the list in one round trip.

### Message assembly

The user message sent to OpenRouter is assembled in this order, supporting material first and the question last:

1. If `context` is present: the line `===== BEGIN CONTEXT =====`, the context text, then the line `===== END CONTEXT =====`.
2. For each file, in the order given: a blank line, the line `===== BEGIN FILE: <path> =====`, the file content, the line `===== END FILE: <path> =====`.
3. A blank line, the line `===== QUESTION =====`, then the `question` text.

Every part carries explicit framing, symmetric where it has an end to mark: context is bracketed exactly like the files, and the trailing question is announced by its own delimiter line, so the boundary between supporting material and the ask never depends on incidental whitespace. The rejected alternative, an unlabeled trailing question separated from an unterminated context block by a blank line alone, is fragile precisely when the context itself ends in blank-line-separated prose.

The part order is deliberate, and question-first was considered and rejected: attachments can approach `MaxAttachmentBytes`, so the supporting material can dwarf the question by orders of magnitude, and long-context prompting guidance consistently places instructions after large documents because models attend most reliably to the end of the prompt. A question buried far above its material risks being underweighted exactly on the large-attachment calls this tool exists for.

Delimiter lines rather than Markdown fences are used deliberately: attached source files routinely contain backtick fences, so fenced framing would break on ordinary Markdown and source attachments, while equals-sign delimiter lines are rare in real content. The residual collision is accepted, not eliminated: a file that itself contains a line matching the delimiter format (this spec is one such file) can make the consultant misread a file boundary. No escaping or content scanning is performed, because the assembled message is prose read by a model, not a format parsed back by machine, and a misread boundary degrades one advisory answer at worst.

The system message is `SystemPrompt` from configuration. Its built-in default (used when the setting is absent) is a short persona along the lines of: "You are an independent expert consultant. Give a candid, critical second opinion. Disagree plainly when warranted, state uncertainty, and do not defer to the asker's framing." Exact wording finalized during implementation.

### Response format

On success the tool returns plain text:

1. A header line: `Consulted <model> (effort: <effort>)`, stating the model and effort actually used after defaulting, so transcripts are self-describing.
2. Only when `finish_reason` is present and not `stop`: the literal template `Warning: answer incomplete (finish_reason: <reason>)`, per the finish_reason branch under Response handling; on the ordinary `stop` path this element is absent.
3. A blank line.
4. The consultant's answer verbatim (`choices[0].message.content`).

## OpenRouter integration

`OpenRouterClient` is a typed `HttpClient` service (registered via `AddHttpClient`, matching the `SerperSearcher` pattern) that POSTs to `https://openrouter.ai/api/v1/chat/completions`. Unlike `SerperSearcher`, which dereferences its options in its constructor, `OpenRouterClient` holds no `IOptions` dependency at all: the `Consultant` tool class reads `IOptions<ConsultantSettings>.Value` inside `Consult`'s try block (per Parameter resolution) and passes the resolved values (API key, timeout, model, effort, system prompt) to `OpenRouterClient` as call arguments. This keeps every settings dereference inside the terminal catch-all's reach, so the binding-failure-to-unexpected-error mapping does not depend on an implementer's constructor choices.

Request:

- Header `Authorization: Bearer <ApiKey>`.
- Header `X-Title: McpConsultant` (OpenRouter attribution, optional but harmless).
- Body: `{ "model": <model>, "messages": [ { "role": "system", ... }, { "role": "user", ... } ], "reasoning": { "effort": <effort> } }`.
- The `reasoning` object is always present, because effort always resolves to a value (parameter or `DefaultEffort`). Models without reasoning support ignore it per OpenRouter's contract.

Timeout mechanism: `TimeoutSeconds` is enforced per request, not via `HttpClient.Timeout`. The client's `Timeout` property is set to `Timeout.InfiniteTimeSpan` at construction, and each request runs under a `CancellationTokenSource` created from the configured timeout and linked to the MCP-supplied token. This choice does two jobs at once. First, it makes timeout and client cancellation distinguishable: both otherwise surface from `SendAsync` as the same `TaskCanceledException`, and the repository's only precedent (`ContentFetcher`) labels every such exception a timeout; here the handler checks which source fired, routing a fired timeout source to the timeout error and a fired caller token to the cancellation path below. Second, it means the setting is read and validated at call time (validation order step 4) rather than applied in a constructor, so a non-positive value can never throw from the `HttpClient.Timeout` property setter during tool resolution.

Response handling, all branches:

- 2xx with parseable body, a non-empty `choices[0].message.content`, and `choices[0].finish_reason` equal to `stop` or absent: success.
- 2xx with non-empty content but a present `finish_reason` other than `stop` (`length`, `content_filter`, or any other value): the answer is still returned, with the warning element of the Response format (element 2) naming the finish reason, so a cut-off or filtered answer is never presented as a complete second opinion.
- 2xx but body fails to parse, `choices` is missing or empty, or content is null or empty: structured error stating the response was unusable, with a truncated body excerpt (500 characters) for diagnosis.
- Non-2xx status: structured error with the status code and a truncated body excerpt (500 characters). OpenRouter returns JSON error details in the body; the excerpt surfaces them without flooding the client.
- Timeout (the per-request timeout source fired): structured error stating the configured timeout, so the caller knows raising it is an option.
- Client cancellation (the MCP-supplied token fired): handled per Client cancellation and teardown below.
- Network failure (DNS, connection refused, TLS): structured error with the exception message.

A `Consultation started` line naming the resolved model and effort is logged to stderr at Information level immediately before the request is sent. On success a `Consultation completed` line is always logged at Information level; it carries exactly the token counts the response supplies: prompt and completion counts whenever a `usage` object is present with both counts (a partial or malformed `usage` shape is treated as absent, since observability must never fail a call that carried a good answer), plus a reasoning-token count only when `usage.completion_tokens_details.reasoning_tokens` is also present (routinely absent for non-reasoning models), and no counts at all when `usage` is absent, the call succeeding in every case. Together with the timestamped console format, the started line and the terminal line (completed, error, or cancellation) bracket every consultation, which is what the registration-time smoke check reads elapsed time from.

Concurrent `consult` invocations are supported: every call is independent and stateless, so nothing is shared beyond the process itself. The stderr lines deliberately carry no correlation identifier; when consultations overlap, pairing started lines with terminal lines is best-effort diagnostics, which is acceptable because the log is for a human reading their own single-user tool. The two checks whose pass conditions depend on that pairing (the cancellation probe and the registration-time smoke check) state a single-in-flight precondition.

### Client cancellation and teardown

A consultation can outlive the client's interest (the 300-second budget invites minutes-long calls), so the complementary branches are fixed here rather than left to the timeout story:

- Client-cancelled call: the tool method accepts the `CancellationToken` the MCP SDK supplies and propagates it to the HTTP request (through the linked source described under Timeout mechanism), so a cancelled consultation aborts the outbound call promptly instead of running to completion for a listener that is gone. The mechanical exit is stated explicitly because a `Task<string>` method cannot "return nothing": the tool logs `Consultation cancelled` to stderr at Information level and lets the `OperationCanceledException` propagate to the SDK, which owns the already-cancelled call; no structured error string is produced because there is no listener to read one. (The SDK delivering cancellation over the stdio transport is a runtime-owned claim; its probe is listed under Testing.)
- Transport teardown (the client exits or restarts, closing stdin): the host shuts down and the in-flight HTTP request is dropped with the process.

In both branches the server's statelessness means there is nothing to recover or resume; the only cost is OpenRouter-side tokens for an answer nobody reads, which is accepted.

## VPN gating

When `RequireVpn` is true, `VpnDetectionService.EnsureVpnConnected()` runs before any file reading or network call; if the VPN is down the tool returns `Consultation unavailable: <message>`, where `<message>` is the consultation-specific failure text McpConsultant passes through the optional parameter added in the McpCommon extraction (the exception's built-in default text is search-specific and never surfaces here). When `RequireVpn` is false (the default), no VPN probe runs at all. The traffic is authenticated TLS API traffic, so the default posture matches `fetch_url` (knob exists, off by default) rather than `search_web`.

## Configuration

Section name: `Consultant`. The defaults below are dual-homed by design: `ConsultantSettings` carries them as code defaults (which is what the settings-binding unit test asserts when the section is absent), and the committed `appsettings.json` spells out the same values explicitly in a `Consultant` section for discoverability, excluding `ApiKey`, which never appears in a committed file. Complete setting list:

| Setting | Default | Notes |
|---|---|---|
| `ApiKey` | none | Required at call time; absence yields a structured error naming the setting, and an empty or whitespace value counts as absent (the same `IsNullOrWhiteSpace` treatment as `DefaultModel`, which is what lets the integration tests force the missing-key branch with an empty env var). Source is lifecycle-bound: during local development and tests, git-ignored `appsettings.Development.json`; in the deployed registration, the `Consultant__ApiKey` environment variable (see Deployment and registration for why the Development file cannot serve there). Never committed. |
| `DefaultModel` | `openai/gpt-5.2` | Committed placeholder; the exact ID is verified against the live OpenRouter catalog during implementation and substituted with the closest current flagship if stale. |
| `SystemPrompt` | built-in persona | Override to change the consultant's character. An empty or whitespace value counts as absent and falls back to the built-in persona (the same treatment as `ApiKey`), so a blank placeholder can never silently strip the persona from every consultation. |
| `DefaultEffort` | `high` | One of `low`, `high`, `max`. |
| `RequireVpn` | `false` | Mullvad gate, same mechanism as McpWeb. |
| `TimeoutSeconds` | `300` | Per-request timeout via a linked cancellation source (see Timeout mechanism), not `HttpClient.Timeout`. Reasoning-heavy models can take minutes; the .NET default of 100 seconds is too low. |
| `MaxAttachmentBytes` | `1048576` | Total budget across all attached files. This is a cost-and-accident guard, not a fit guarantee: it bears no relation to any model's context window, so a within-budget message can still exceed the consulted model's input capacity, which surfaces as whatever error response the provider returns, reaching the caller through the non-2xx or unusable-response branch; that the provider rejects over-window input rather than silently compressing it is provider-owned and carries its own probe under Testing. Sizing the budget to the models actually used is the operator's configuration concern. |

Configuration follows McpWeb's source order exactly: `appsettings.json`, then `appsettings.{Environment}.json`, then environment variables (`Consultant__ApiKey` form), then command line (`--Consultant:ApiKey` form). One deliberate deviation from McpWeb's wiring: the configuration base path is `AppContext.BaseDirectory` (the executable's directory), not `Directory.GetCurrentDirectory()`. McpWeb's cwd-based base path means a client-spawned stdio server, whose working directory belongs to the client, never loads its own exe-adjacent `appsettings.json` and silently falls back to code defaults for every non-secret setting; anchoring to the executable's directory makes the committed file load on every launch path (dotnet run, tests, deployed registration). The csproj copy items also mirror McpWeb's exactly, including the asymmetry: `appsettings.json` is copied to the output directory unconditionally with `CopyToOutputDirectory="PreserveNewest"`, while `appsettings.Development.json` carries the same copy metadata plus `Condition="Exists('appsettings.Development.json')"` (as in `McpWeb/McpWeb.csproj`), because the Development file is git-ignored and absent on any fresh checkout; an unconditional item would break the build exactly on the keyless machines the test plan targets.

## Error handling summary

Every failure path returns a structured error string from the tool method; the tool never throws to the MCP layer, with one deliberate exception: `OperationCanceledException` propagates to the SDK when the client's own token cancelled the call, per Client cancellation and teardown. Complete list of error classes: empty question, missing API key, missing default model, invalid effort value, non-positive `TimeoutSeconds` or `MaxAttachmentBytes`, VPN required but not connected, file validation failures (not fully qualified, missing, directory, over budget, unreadable, binary, wrong encoding), HTTP non-success, unusable response body, timeout, network failure, unexpected error. Each class carries the stable substring described under Testing. All errors are also logged to stderr.

The unexpected-error class is the residual that makes the never-throws contract mechanically real rather than aspirational: the tool method's terminal handler catches any exception not mapped by an earlier branch (for example a defect in the tool's own code surfacing as `NullReferenceException`; anticipated faults such as stage 1 metadata access failing are mapped by their own branches and never reach here) and returns it as a structured unexpected-error string. That handler has exactly one exclusion, checked first: an `OperationCanceledException` whose cause is the client's own token is rethrown, never mapped, because a blanket `catch (Exception)` in the McpWeb style would otherwise swallow the cancellation carve-out and defeat the timeout-vs-cancellation discriminator.

One path deliberately sits outside the structured-error contract, named so the list above stays honest: a client-cancelled or torn-down call produces no structured error because there is no listener (the cancellation case exits by propagating `OperationCanceledException`, the teardown case by process shutdown), as specified under Client cancellation and teardown. A type-level options binding failure is not a second exception: the tool reads `IOptions<ConsultantSettings>.Value` inside `Consult`'s try block rather than in the constructor (the stated deviation from McpWeb's convention, per Parameter resolution), so the failure surfaces inside the terminal catch-all and the unexpected-error class covers it.

## Testing

Test project `McpConsultant.Tests` (xunit v3, matching the McpWeb.Tests migration). The MCP handshake and tool-call helpers currently private to `McpWebIntegrationTests` are adapted (copied) into the new test project; extracting a shared test kit is deferred until a third server exists. The asymmetry with the McpCommon extraction (production code shared at the second consumer, test helpers copied until a third) is deliberate: shared production code changes runtime behavior for every consumer and so must have exactly one implementation, and a class-library reference is cheap; the handshake helpers are a handful of private methods whose divergence risks nothing at runtime, and extracting them would mint a whole project for marginal duplication.

Error assertions are written against per-class stable substrings, and the contract has two layers so it stays decidable without demanding an end-to-end test per class. First, all error strings are built from one internal per-class template table, and a single unit test walks that table asserting every class's distinctive substring exists and is unique; that covers the substring contract for all classes, including ones no test can reach deterministically (the VPN refusal depends on live adapter state). Second, branch-reachability tests (the unit, integration, and stubbed-handler bullets below) additionally verify that reachable branches actually produce their class's substring. Exact full wording stays implementation-owned; the substring is the contract.

The four response-handling classes (HTTP non-success, unusable response body, timeout, network failure) are reachable without network via a test seam: `OpenRouterClient` is constructed over `HttpClient`, so unit tests build it directly with a stub `HttpMessageHandler` returning a canned non-2xx response, an unparseable 2xx body, a delayed response against a small `TimeoutSeconds`, and a thrown `HttpRequestException` respectively.

Unit tests (no network):

- Message assembly: question only; question plus context; question plus files; question plus context plus files; delimiter format; file ordering preserved; whitespace-only `context` and `model` treated as absent.
- Effort validation: each of `low`, `high`, `max` accepted case-insensitively; a rejected value's error lists the allowed set; misconfigured `DefaultEffort` rejected at call time.
- Numeric settings validation: zero and negative `TimeoutSeconds` and `MaxAttachmentBytes` each rejected with the setting named.
- File validation: each stage 1 and stage 2 failure branch above, exercised via temp files created with standard temp-path APIs; multiple failing paths reported in one error; an over-budget file is rejected without its content being read. The arrangement must discriminate: the over-budget file is held under an exclusive lock by the test (or otherwise made unreadable content-wise while its metadata stays readable), so an implementation that reads content before the budget check surfaces the unreadable error while a conforming one returns the budget error; asserting the budget error is what proves no content read occurred.
- Attachment budget arithmetic: under, at, and over the limit; a duplicated path is attached once and counted once.
- Settings binding: defaults applied when the section is absent; overrides honored.
- Response formatting (pure function over a parsed response): the header line matches its literal template; the warning line appears with its literal template for a non-`stop` finish reason and is absent for `stop` or an absent finish reason.
- Error-template table: every class in the Error handling summary has a template whose distinctive substring is present and unique across classes (the first contract layer described above).
- Stubbed-handler response classes: each of the four response-handling error classes produced through the stub `HttpMessageHandler` seam described above, asserting each class's substring.
- Empty question and blank `DefaultModel` (configured as whitespace): each returns its class's structured error.

Integration tests (child process with a controlled configuration): the harness spawns the server the way `McpWebIntegrationTests` does, but never with `DOTNET_ENVIRONMENT=Development`, and always sets `Consultant__ApiKey` explicitly, because the developer machine's git-ignored `appsettings.Development.json` would otherwise leak a real key into the child and silently change which validation branch fires.

- Handshake completes and `tools/list` includes `consult`.
- `consult` with `Consultant__ApiKey` set to the empty string returns the structured missing-key error.
- `consult` with an invalid effort and a dummy key returns the allowed-values error (validation order step 2 precedes the key check, so the dummy key is never used).
- `consult` with a missing file path and a dummy key returns the file error (file validation fails at step 7 before any request is attempted, so no network access occurs).

Live tests (real OpenRouter calls): the test process resolves the key itself, in-process, by building the same configuration hierarchy the server uses (base path at the McpConsultant project directory, Development environment, environment variables on top), then passes the resolved key to the child explicitly via `Consultant__ApiKey`. This reconciles two rules that would otherwise conflict: the child is still never spawned with `DOTNET_ENVIRONMENT=Development` (the harness rule above), yet the key is found where this machine actually keeps it (`appsettings.Development.json`). When no key resolves, `Assert.Skip`, so keyless machines and CI stay green. The suite is additionally gated on an explicit opt-in, the environment variable `MCPCONSULTANT_LIVE=1`, because once the git-ignored key file exists on a developer machine, key presence alone would let any unfiltered test run silently issue paid calls. The suite consists of one end-to-end consultation asserting a non-empty answer and the header line, plus probes for the OpenRouter-owned and SDK-owned claims this spec asserts but the repository cannot settle. The feature has one execution context (a direct chat-completions call from the server process), so one probe each suffices:

- Each of `low`, `high`, `max` is accepted by the chat-completions endpoint on the default model; pass condition: HTTP 2xx and a non-empty answer for all three (live-claim: probed 2026-08-14).
- A request with `effort: max` against a reasoning-capable model whose documented ladder lacks a `max` tier (selected from OpenRouter's catalog documentation at implementation time, ID recorded in the test) succeeds through degradation rather than returning a 400, which is the half of the "OpenRouter owns per-provider translation" claim the default-model probe cannot reach; pass condition: HTTP 2xx and a non-empty answer (live-claim: probed 2026-08-14). Probed against `google/gemini-3.7-flash`, whose catalog-documented `reasoning.supported_efforts` is `["high", "medium", "low"]` with no `max` tier.
- A request carrying the `reasoning` object against a model without reasoning support succeeds rather than erroring, and such a model may still report `usage.completion_tokens_details.reasoning_tokens` as `0`; field presence does not discriminate reasoning capability, only a positive count indicates reasoning occurred. The probe model is selected at implementation time from OpenRouter's catalog documentation as one lacking reasoning support, and its ID is recorded in the test; pass condition: HTTP 2xx, a non-empty answer, and no positive `reasoning_tokens` count in the response's `usage`, the last assertion being what proves the probe actually exercised a non-reasoning model rather than passing vacuously (live-claim: probed 2026-08-14). Probed against `openai/gpt-4o` (catalog-documented without a `reasoning` entry in `supported_parameters`): HTTP 200, a non-empty answer, and `completion_tokens_details.reasoning_tokens: 0`, a present but non-positive count. The original claim assumed field absence would discriminate non-reasoning models; this probe corrected that assumption to a non-positive-count check instead.
- The `usage` object, when present, parses with prompt and completion token counts, and `usage.completion_tokens_details.reasoning_tokens` is present for a reasoning model. The probe runs against a reasoning-capable model (the default model if it qualifies, otherwise one selected and recorded at implementation time); pass condition: the `Consultation completed` stderr line (whose format is fixed at implementation time and asserted literally by this test) appears with nonzero prompt and completion counts and includes a reasoning-token count, the last assertion being what settles the nested `completion_tokens_details` path the implementation depends on (live-claim: probed 2026-08-14).
- The MCP SDK delivers cancellation to the tool method over the stdio transport, so an aborted call cancels the outbound HTTP request; pass condition: with no other consultation in flight, after the harness cancels the in-flight `tools/call`, the `Consultation cancelled` stderr line (specified under Client cancellation and teardown) appears within 5 seconds, and no answer or `Consultation completed` line follows it (live-claim: probed 2026-08-14).
- The provider rejects an over-window request rather than silently compressing or truncating the input (the behavior the MaxAttachmentBytes note relies on). The probe attaches material deliberately exceeding the probe model's documented context window; pass condition: the call returns a structured error through the non-2xx or unusable-response branch, and no normal answer is produced (live-claim: provisional).

Registration-time check (a manual procedure run once at registration, deliberately outside the xunit live suite, whose harness mechanics cannot apply to it): the registered client waits at least the configured `TimeoutSeconds` for a `tools/call` response (the host-owned ceiling documented under Deployment and registration). Two parts, both run with no other consultation in flight. Part one is a configuration inspection and settles the claim when decidable: the client's tool-execution timeout (`MCP_TOOL_TIMEOUT` in Claude Code; not `MCP_TIMEOUT`, which bounds server startup) is read; pass condition: the configured value, interpreted in the client's documented unit (milliseconds in Claude Code, so `300000` corresponds to the default `TimeoutSeconds` of 300), is greater than or equal to `TimeoutSeconds` converted to that same unit. When the variable is unset, the host's built-in default (version-owned, unknowable from here) governs and part one is inconclusive rather than passing; the registration instruction under Deployment sets `MCP_TOOL_TIMEOUT` explicitly precisely so this branch does not arise. Part two is a smoke call guarding against an unadvertised lower effective ceiling: one consultation through the registered client, pinned to a reasoning model at `effort: max` with a deliberately heavy prompt (model ID and prompt chosen and recorded at registration time), whose elapsed time, taken from the timestamps of the `Consultation started` and `Consultation completed` stderr lines, must exceed 100 seconds to establish the precondition; pass condition: precondition met and the answer arrives through the client. A smoke run that finishes at or under 100 seconds is inconclusive, not a pass, and is re-run with a heavier prompt. The operator obtains the spawned server's stderr from the client's MCP log capture (Claude Code persists each registered server's stderr to its per-server MCP log files); that this capture preserves the timestamped lines is itself part of what this registration-time check verifies (live-claim: provisional).

The McpCommon extraction's pass condition: the existing McpWeb.Tests suite passes unchanged after the move (build both projects, then run McpWeb.Tests); this is what makes "behavior-preserving for McpWeb" a checkable claim rather than prose.

## Deployment and registration

Published the same way as McpWeb: `dotnet publish McpConsultant/McpConsultant.csproj -c Release -r win-x64` producing a single-file executable. The user registers it in the Claude Code MCP configuration as a stdio server pointing at the published executable.

Upgrades (second and subsequent publishes): while a Claude Code session is live, the registered server process holds the single-file executable open, so publishing over the same output path fails on a locked file on Windows. The update order is therefore: disconnect or exit the MCP client session, publish, reconnect; a running server keeps serving the old binary until that restart. `README_CONSULTANT.md` carries this procedure alongside the other registration instructions.

The registered executable runs in the default (Production) environment: nothing in an MCP stdio registration sets `DOTNET_ENVIRONMENT`, so `appsettings.Development.json` is never loaded on the deployed path. The API key must therefore be supplied in the registration itself, via an `env` block carrying `Consultant__ApiKey`, exactly as `README_WEB.md` prescribes for McpWeb's published registration; the Development file serves local development and the test harness only. Without this, a user following the local-development convention alone would get the missing-key structured error on every consultation.

Registration itself is a user action outside this repo, with one further documented assumption: the 300-second default only helps if the registered client itself waits that long for a `tools/call` response, so the effective ceiling is the smaller of `TimeoutSeconds` and the client's own MCP tool timeout. That host-owned ceiling is governed in Claude Code by `MCP_TOOL_TIMEOUT` (the tool-execution timeout; the similarly named `MCP_TIMEOUT` bounds server startup and is irrelevant to a running `tools/call`). The variable is read by the Claude Code client process, so it must be set in the client's own environment (the shell or system environment Claude Code starts under, or the top-level `env` of its settings), and explicitly NOT in the registration's per-server `env` block, which configures only the spawned server process where nothing reads it. Registration sets it to at least `TimeoutSeconds` expressed in the client's documented unit, which in Claude Code is milliseconds: `MCP_TOOL_TIMEOUT=300000` for the default 300 seconds. `README_CONSULTANT.md` carries the instruction. Its check is described under Testing.
## Hardening

- revise-spec graduated 2026-08-13 18:50 at 7483f17, scope: whole file, content: e992c53c
- stamp refreshed 2026-08-13 23:39 at 7483f17, reason: spec reconciliation, content: 1fbdce75
