# AGENTS.md

This file provides guidance to coding agents (Claude Code, Codex, and others) when working with code in this repository.

## Project Overview

This repository contains Model Context Protocol (MCP) servers built on .NET 10, plus the Python-based McpKeep server:

### McpKeep

Google Keep integration for Hermes on Windows, using the unofficial gkeepapi client and the Python MCP SDK over stdio. Its four tools are `list_notes`, `read_note`, `archive_note`, and `unarchive_note`. Each call uses a fresh backend process with a bounded lifetime; no note cache or pending writes are persisted. Google credentials live in a token file outside Git and are configured through `KEEP_EMAIL` and `KEEP_MASTER_TOKEN_FILE`.

McpKeep is a standalone Python package under `McpKeep/`, separate from the .NET solution. Set up dependencies with `uv sync --project McpKeep --frozen`, run scoped tests with `uv run --project McpKeep pytest McpKeep/tests`, and check Python style with `uv run --project McpKeep ruff check McpKeep`. Setup and Hermes configuration are documented in `README_KEEP.md`; the governing spec is `.nightshift/specs/mcp-keep.md`.

### McpWeb (Active)
MCP server that provides web search, URL fetching, and temporal context capabilities to LLM clients. Uses Serper.dev API (Google search) and optionally enforces Mullvad VPN connectivity before allowing searches or fetches. Provides current date/time/timezone to help LLMs use accurate dates in queries.

**Technology Stack**: .NET 10, ModelContextProtocol SDK, Polly (resilience), runs as stdio MCP server

**Tools Provided** (McpWeb):
- `search_web` - Search the web and return results with titles, URLs, and snippets
- `fetch_url` - Fetch full HTML content from a specific URL
- `get_context` - Get current date, time, timezone, and week information

**Status**: Production-ready, tested, actively used

### McpImageGen (In Development)
MCP server for RP-focused image generation with session-consistent character portraits and environments. Uses ComfyUI as rendering backend with InstantID for character consistency.

**Technology Stack**: .NET 10, ModelContextProtocol SDK, ComfyUI HTTP API, InstantID/IP-Adapter

**Status**: Design phase - see README_IMAGE_GEN.md for full specification. Currently experimenting with ComfyUI workflows before implementing MCP server.

**Key Concept**: Optimized for visual storytelling across RP conversations, not one-off image generation. Session-locked styles ensure all images feel like they're from the same illustrated story.

### McpConsultant (Active)
MCP server giving Claude Code a single `consult` tool for second opinions from external models via OpenRouter. Every consultation is a stateless one-shot request; file attachments are read from disk and sent directly to OpenRouter, so their contents never enter the client's conversation context. Design spec (review-graduated): `.nightshift/features/mcp-consultant.md`.

**Technology Stack**: .NET 10, ModelContextProtocol SDK, OpenRouter API, runs as stdio MCP server

**Tools Provided**:
- `consult` - Get a second opinion from an external model on a spec, design, or hard problem, optionally attaching files by path

**Status**: Implemented, tested

**Follow-ups**: `.nightshift/features/mcp-consultant-followups.md` (an exploring draft indexed in `.nightshift/FEATURES.md`'s `## Exploring` section, findings not yet vetted) tracks findings from an external model review of the spec, explicitly gated on the first slice shipping; consult it before planning any second slice.

## Common Development Commands

### Building
```bash
dotnet build
```

### Running McpWeb locally
```bash
dotnet run --project McpWeb/McpWeb.csproj
```

### Running McpConsultant locally
```bash
dotnet run --project McpConsultant/McpConsultant.csproj
```

### Publishing McpWeb as a self-contained executable
```bash
dotnet publish McpWeb/McpWeb.csproj -c Release -r win-x64
# Creates single-file executable in McpWeb/bin/Release/net10.0/win-x64/publish/
```

### Publishing McpConsultant as a self-contained executable
```bash
dotnet publish McpConsultant/McpConsultant.csproj -c Release -r win-x64
# Creates single-file executable in McpConsultant/bin/Release/net10.0/win-x64/publish/
```

### Running tests
```bash
dotnet test
```

### Running a single McpWeb test
```bash
dotnet test --filter "FullyQualifiedName~McpWebIntegrationTests.SearchWeb_WithVpnConnected_ReturnsResults"
```

### Running a single McpConsultant test
```bash
dotnet test --filter "FullyQualifiedName~McpConsultantIntegrationTests"
```

## Architecture

### MCP Protocol Communication
- Uses **stdio transport** - JSON-RPC messages over stdin/stdout
- All application logs go to **stderr** to avoid interfering with MCP protocol on stdout (see Program.cs logging configuration)
- MCP protocol initialization sequence: `initialize` → server responds → client sends `initialized` notification → `tools/list` to discover tools

### Service Layers (Dependency Flow)

**McpWeb**:
1. **MCP Tool Layer**:
   - **Search** - Exposes `search_web` tool, handles VPN check for searches, serializes responses
   - **Fetch** - Exposes `fetch_url` tool, validates URLs (SSRF prevention), handles VPN check for fetches
2. **SearchService** - Coordinates search and content fetching
3. **SerperSearcher** - Uses Serper.dev API to get search results
4. **ContentFetcher** - Fetches full HTML from URLs (used by both SearchService and Fetch)

**McpConsultant**:
1. **Consultant tool** - Exposes `consult` tool, resolves and validates parameters, handles VPN check for consultations
2. **OpenRouterClient** - Sends the assembled prompt to the OpenRouter API and parses the response

**Shared**: `VpnDetectionService` lives in McpCommon and is shared by both servers.

### Configuration System
- The hierarchy below applies to the .NET servers. McpKeep reads its `KEEP_EMAIL`, `KEEP_MASTER_TOKEN_FILE`, and `KEEP_TIMEOUT_SECONDS` environment settings; see `README_KEEP.md` for its separate Python setup.
- Configuration hierarchy: `appsettings.json` → `appsettings.{Environment}.json` → environment variables → command-line arguments (see Program.cs)
- Configuration sections: `Search`, `Fetch`, `Logging`, `Consultant`
- Environment variable format: `Search__RequireVpn=false`, `Fetch__RequireVpn=true`, `Consultant__ApiKey=your-api-key-here` (double underscore for nested config)
- Command-line format: `--Search:RequireVpn=false`, `--Fetch:RequireVpn=true`, `--Consultant:ApiKey=your-api-key-here`
- Each feature (Search, Fetch, Consultant) has independent VPN requirement setting

### VPN Detection Mechanism
- Checks for active network adapters with "mullvad", "wireguard", or "wintun" in name/description
- Filters out AdGuard VPN to avoid false positives (see McpCommon/Services/VpnDetectionService.cs)
- Only checks adapters with `OperationalStatus.Up`
- Can be disabled per-feature:
  - Search: `Search:RequireVpn=false` (default: true)
  - Fetch: `Fetch:RequireVpn=false` (default: false)
  - Consultant: `Consultant:RequireVpn=false` (default: false)

### Serper.dev Integration
- Requires API key configured via environment variable `Search__SerperApiKey` or `appsettings.Development.json`
- **Never commit API keys** - use `appsettings.Development.json` (git-ignored) for local development
- Free tier: 2,500 queries/month
- Built-in 1-second rate limiting after each search (see SerperSearcher.cs)
- Get free API key at https://serper.dev

## Key Implementation Details

### MCP Tool Naming
C# method names are automatically converted to snake_case for MCP clients (e.g., `SearchWeb` → `search_web`)

### Integration Testing
Integration tests in `McpWebIntegrationTests.cs` start the actual MCP server as a child process and communicate via stdin/stdout. The tests:
- Start server with `dotnet run --no-build`
- Send MCP protocol messages (initialize, tools/call)
- Can override config via environment variables (e.g., `Search__RequireVpn`, `Fetch__RequireVpn`)
- Read responses from stdout and server logs from stderr
- Test both `search_web` and `fetch_url` tools
- Validate SSRF prevention (localhost/private IP blocking) for `fetch_url`

### Error Handling
- VPN errors return structured error messages instead of throwing exceptions
- Search and fetch errors return structured error responses
- Fetch validates URLs and blocks SSRF attacks (localhost/private IPs)
- All errors logged to stderr for debugging

## Project Structure Notes

### McpWeb Project
- `McpWeb/` - Main server project
- `McpWeb.Tests/` - Integration and unit tests
- `Models/` - Data transfer objects (SearchResult, settings)
- `Services/` - Business logic (search, content fetching)
- `Tools/` - MCP tool definitions

### McpCommon
- `McpCommon/` - Shared class library referenced by both McpWeb and McpConsultant
- `Services/` - VPN detection (`VpnDetectionService`)
- `Exceptions/` - Custom exceptions (`VpnNotConnectedException`)

### McpConsultant Project
- `McpConsultant/` - Main server project
- `McpConsultant.Tests/` - Unit, integration, and live tests
- `Models/` - Data transfer objects (settings, attached files, OpenRouter request/response)
- `Services/` - Business logic (prompt assembly, file attachment validation, OpenRouter client, error messages, response formatting)
- `Tools/` - MCP tool definitions

### McpImageGen Project (Planned)
- `McpImageGen/` - Main server project (not yet created)
  - `Services/` - ComfyUI client, session management, character enrollment
  - `Models/` - Session, Character, GenerationRequest/Result
  - `Tools/` - MCP tool definitions (start_session, enroll_character, generate_portrait, generate_environment)
  - `Data/` - Persisted sessions and character data
- `ComfyUI/` - Managed by Stability Matrix (external to solution)
  - `workflows/` - JSON workflow templates
  - `output/` - Generated images

See README_IMAGE_GEN.md for detailed architecture and implementation plan.

## Configuration Files

### Development Settings Pattern
- `appsettings.json` - Base configuration template (committed to git, no secrets)
- `appsettings.Development.json` - Local overrides with API keys (git-ignored)
- Both files are copied to output directory via `CopyToOutputDirectory="PreserveNewest"` in .csproj
- .NET automatically merges Development settings over base when `DOTNET_ENVIRONMENT=Development`

**Security:** Never commit API keys to git. Use `appsettings.Development.json` for local development.

---

## McpImageGen Development Notes

**Current Phase**: ComfyUI workflow experimentation (Phase 1A)

### ComfyUI Environment
- **Manager**: Stability Matrix is used to manage ComfyUI installation
- **Typical Installation Path**: `%APPDATA%\StabilityMatrix\Data\Packages\ComfyUI\` (Windows)
- **Common Paths**:
  - Models: `ComfyUI\models\` (checkpoints, LoRAs, VAE, etc.)
  - Custom Nodes: `ComfyUI\custom_nodes\`
  - Workflows: `ComfyUI\workflows\` (user-created)
  - Output: `ComfyUI\output\` (generated images)
  - Input: `ComfyUI\input\` (reference images)
- **API Access**: ComfyUI HTTP API typically runs on `http://127.0.0.1:8188` when started via Stability Matrix
- **Model Organization**:
  - `models/checkpoints/` - Base models (SDXL, SD 1.5, etc.)
  - `models/loras/` - LoRA files
  - `models/vae/` - VAE models
  - `models/instantid/` - InstantID models (if installed)
  - `models/ipadapter/` - IP-Adapter models

**Before implementing the MCP server:**
1. ~~Install InstantID nodes in ComfyUI via Stability Matrix~~ (tested, ruled out)
2. **[CURRENT]** Test IP-Adapter for face consistency with pixel art style
3. Test HD pixel art LoRA with SDXL 1.0 base model
4. Build and test portrait workflow manually (with IP-Adapter)
5. Build and test environment workflow manually
6. Verify character consistency across multiple generations
7. Export working workflows as JSON templates

**Key Design Decisions:**
- CRPG-style composition: portraits and environments generated separately, composited by MCP tool
- Session-locked styles: all images in a session use the same LoRA for visual consistency
- **IP-Adapter approach**: InstantID tested and ruled out, now testing IP-Adapter for face consistency
- Auto-selection: system picks best character reference from 3-5 candidates using quality heuristics

**Testing Status:**
- InstantID: ❌ Ruled out (issues with pixel art style compatibility)
- IP-Adapter: 🔄 Currently testing

**Reference Workflows:**
- `ComfyUI/inspiration/workflow-flux-consistent-character-sheet-oSEKBwDLvkt9rHMfdU1b-reverentelusarca-openart.ai.json`
  - Source: https://openart.ai/workflows/oSEKBwDLvkt9rHMfdU1b
  - Demonstrates: SDXL + IP-Adapter + ControlNet (OpenPose) integration
  - Useful for: Node wiring patterns, KSampler settings, upscaling pipeline
  - Adaptation needed: Strip character sheet logic, add pixel art LoRA, single portrait output
  - Consider: Adding ControlNet for consistent portrait framing/composition

**Integration Points:**
- ComfyUI HTTP API for workflow submission and output retrieval
- Face detection library for candidate scoring (OpenCV or similar)
- Image compositing for CRPG-style scene assembly

**Why separate from McpWeb:**
- Different domain (image generation vs web search)
- ComfyUI dependency (external service)
- Stateful (sessions, enrolled characters) vs stateless
- Can be split into separate repository later if desired

## Backlogs and indexes

Four repo-local indexes live under `.nightshift/`. A `SessionStart` hook in `.claude/settings.json` injects a directive so Claude reads them on the first turn of every session; any task the user raises may already be queued, designed, diagnosed, or covered by an existing pattern:

- `.nightshift/QUICK_WINS.md`: refactors ready to land when time allows. Shipped entries are appended to `.nightshift/QUICK_WINS_HISTORY.md` (described below).
- `.nightshift/FEATURES.md`: product-level feature ideas, with one file per feature under `.nightshift/features/`. Shipped entries are appended to `.nightshift/FEATURES_HISTORY.md` (described below). When sibling feature files start duplicating shared concerns (machinery, patterns, conventions), promote an umbrella file that hosts the shared content and trim the siblings to deltas; cross-references through an umbrella scale better than pairwise cross-references.
- `.nightshift/BUGS.md`: known bugs awaiting fix, with one file per bug under `.nightshift/bugs/` when more than a few lines of description is needed. Fixed entries are appended to `.nightshift/BUGS_HISTORY.md` (described below).
- `.nightshift/PATTERNS.md`: cross-cutting design patterns that span multiple features, with one file per pattern under `.nightshift/patterns/`. Complementary to the umbrella-promotion heuristic above: umbrellas cluster children of one family; patterns cluster concerns that span families. A pattern graduates here when the same structure would otherwise be re-described in two or more feature files.

Four further locations are not read at session start; consult them when relevant work is in flight:

- `.claude/plans/<date>-<slug>.md`: implementation plans produced by the writing-plans workflow. **Ephemeral**: a plan exists while the implementation is in flight and is deleted once the work lands. The code, tests, and commits are the durable record. Plans are purely mechanical step-by-step instructions for the agent doing the work. There is no "implemented plans" archive.
- `.nightshift/QUICK_WINS_HISTORY.md`: archive of shipped quick wins, split out from `QUICK_WINS.md` so the active backlog stays scannable on session start. Append entries here as soon as the quick win lands; the file itself is consulted only when something pulls it in (a pattern-doc cross-reference, an archaeological lookup, a negative-knowledge sweep). Negative-knowledge entries (approaches attempted and reverted) are first-class promotion candidates into the relevant `.nightshift/patterns/<slug>.md` Cautionary tales sections.
- `.nightshift/FEATURES_HISTORY.md`: archive of shipped features and shipped slices, split out from `FEATURES.md` so the active backlog stays scannable on session start. Append entries here as soon as a feature or slice lands.
- `.nightshift/BUGS_HISTORY.md`: archive of fixed bugs, split out from `BUGS.md`. Append entries here as soon as a bug is fixed.

**Walk-and-remove convention.** When a feature, slice, quick win, or bug-fix ships, the same change set that appends its entry to the relevant history archive ALSO walks every other `**Requires:**` line in `FEATURES.md` / `BUGS.md` and drops references to the just-shipped item; if the dropped reference was the only one on the line, the line becomes `Requires: none.`. Active `Requires:` lines therefore describe what is *currently* blocking, and `/nightshift:ready` never has to consult the history archives to resolve dependencies; the dependency graph settles as work ships.

Brainstorming output lives in feature files (or in patterns when cross-cutting / in bugs when diagnostic) rather than as separate dated specs. Pre-feature exploratory brainstorms land as draft features with `status: exploring` frontmatter and an entry in `FEATURES.md`'s `## Exploring` section; `/nightshift:ready` skips them. They graduate to a themed `##` section with a `**Requires:**` line once the design firms up.

The `/nightshift:ready` command parses each entry's `**Requires:**` line in `FEATURES.md` and `BUGS.md` and reports the unblocked work set. Run it when picking what to work on next.
