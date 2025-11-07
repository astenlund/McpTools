# RP Illustrator - Local Image Generation for Roleplaying

This project provides **session-consistent character portraits and environments** for roleplay scenarios. It uses **ComfyUI** as the rendering engine with an **MCP server wrapper** that maintains visual consistency across a conversation.

## The Core Idea

During an RP session, the LLM can automatically generate:
- **Character portraits** (enrolled characters with consistent faces)
- **Environment backdrops** (tavern, forest, mountain path, etc.)
- **Composed scenes** (CRPG-style: character portrait overlaid on environment)

All images maintain the same visual style throughout the session, creating a coherent "illustrated story" aesthetic.

## Visual Aesthetic

**HD Pixel Art** - Modern high-resolution pixel art style (think Octopath Traveler, not 8-bit sprites). This provides:
- Strong nostalgic CRPG feel
- Artistic consistency across generations
- Forgiving character identity maintenance (stylization helps consistency)
- Clean separation between portraits and environments

---

## Design Philosophy

**Why this exists:**

Most AI image generation tools optimize for single, standalone images. This project optimizes for **visual storytelling across a conversation**:

- **Session consistency** - All images feel like they're from the same illustrated story
- **Character memory** - Characters look the same across dozens of generations
- **Conversation-aware** - LLM uses rich context to make better generation decisions
- **RP-first design** - Built for narrative illustration, not one-off image generation
- **Graceful degradation** - CRPG-style composition works even when complex scene generation doesn't

**What makes it different:**

- Not trying to be Midjourney (no prompt engineering, no "try 50 seeds")
- Not trying to be generic (narrow use case, well-executed)
- Not trying to be automatic (user or LLM makes creative choices, system ensures consistency)
- Leverages conversation history that only LLM-integrated tools have access to

**Philosophy on complexity:**

Start simple, add features when they solve real problems encountered during use. The full vision (integrated multi-character scenes, dynamic LoRA selection, outfit continuity) is documented but not required for the MVP to be useful.

---

## Architecture Overview

```
LLM in RP conversation → MCP Tool Call → ComfyUI Workflow → Image + Metadata
                              ↓
                    Manages: Session style
                            Character references (InstantID)
                            Workflow templates
```

### Technology Stack
- **Base Model:** SDXL 1.0 (neutral base for heavy stylization)
- **Style:** HD pixel art LoRA (session-locked for consistency)
- **Character Identity:** InstantID (face consistency across generations)
- **Backend:** ComfyUI (via Stability Matrix)
- **API:** MCP server (integrated into McpTools solution)
- **Hardware:** RTX 4090, ~12GB VRAM budget

### CRPG-Style Composition

Rather than generating complex scenes with characters embedded, we use the classic CRPG approach:

**Character Layer (Portrait):**
- 1:1 or 2:3 aspect ratio
- Bust or half-body shot
- InstantID ensures same face across all portraits
- Session style applied

**Environment Layer (Backdrop):**
- 16:9 or 21:9 cinematic aspect
- Sets scene mood and location
- No character identity concerns
- Session style applied

**Composition:**
- Generated separately, composited by MCP tool (see `compose_scene` in MCP API section)
- OR returned as separate images for LLM to display inline
- Classic side-by-side or overlaid layouts

**Benefits:**
- Each layer can be optimized independently
- No conflict between character identity and environment LoRAs
- Reusable environments across multiple scenes
- Consistent quality for character faces

**Why separate generation works:**

In classic CRPGs, character portraits and backgrounds are separate art assets. This isn't a limitation - it's an aesthetic choice that:
- Emphasizes character expression (close-up portraits show emotion clearly)
- Allows rich, detailed environments without character interference
- Enables art reuse (same tavern backdrop for multiple scenes)
- Provides visual consistency through fixed framing

---

## Session Management

Sessions provide visual consistency across an entire RP conversation. When a session starts, the visual style is locked and applied to all subsequent generations.

### How Sessions Work

1. **Session creation** - User or LLM calls `start_session(style)` at conversation start
2. **Style locking** - Selected LoRAs are loaded and remain fixed for the session
3. **Character enrollment** - Characters are associated with the session
4. **Consistent generation** - All images (portraits, environments) use the same style

### Style Selection

At session start, choose the rendering aesthetic:

**Supported Styles (Phase 1):**
- `hd_pixel_art` - HD pixel art (Octopath Traveler aesthetic)

**Future Styles:**
- `oil_painting` - Classic painted illustration
- `ink_sketch` - Hand-drawn line art with subtle shading
- `watercolor` - Soft painted aesthetic
- `anime` - Modern anime illustration style

**Optional Enhancements:**
- Color grading (retro palette, CRT effect, film grain)
- Applied consistently across all session images

**Why session locking matters:**

The LLM has access to the full conversation history. This context allows it to:
- Understand which characters are present without re-description
- Match image tone to narrative mood (tense scene vs lighthearted)
- Generate appropriate environments for story locations
- Maintain consistent visual language across 50+ generated images

Without session locking, each generation would be stylistically independent, creating a disjointed visual experience.

---

## Character Enrollment

Character consistency is maintained through **InstantID**, which extracts facial features from reference images and applies them to new generations.

### Automatic Enrollment Flow

When a new character is introduced, the system:

1. **Generate initial candidates** (3-5 portraits from character description)
2. **Auto-select best reference**:
   - Face detection score (exactly 1 face, centered, good confidence)
   - No artifacts (extra limbs, cut-off faces, distortions)
   - Proper framing (portrait composition)
3. **Extract InstantID embedding** from selected image
4. **Store character**:
   - Name
   - Description (for prompt context)
   - InstantID reference
   - Reference image (for user review)

If all candidates fail quality checks → regenerate batch automatically.

### Future Generations

Once enrolled:
- Character name in prompt → InstantID applied automatically
- Same face across all emotions, poses, angles
- Works with any session style (pixel art, painted, etc.)

### Fallback: IP-Adapter Hybrid

If InstantID struggles with pixel art stylization:
- Use InstantID to generate 3-5 semi-realistic reference angles
- Store those as IP-Adapter reference set
- IP-Adapter may better "translate" faces across style boundaries
- (TBD: Test both approaches in ComfyUI first)

---

## Workflow Templates

These are the ComfyUI workflow structures used for generation. The MCP server patches these JSON templates with session-specific values.

### Portrait Workflow
```
prompt_text                         # Character description + emotion + context
negative_prompt                     # Pixel art adjusted negative prompt
InstantID reference                 # Face embedding from enrolled character
style_lora                          # Session-locked (e.g., pixel art)
seed                                # Optional for regeneration
steps, cfg, sampler                 # Generation parameters
width, height                       # Portrait ratio (1:1 or 2:3)
```

### Environment Workflow
```
prompt_text                         # Location description
negative_prompt                     # Pixel art adjusted negative prompt
style_lora                          # Same session-locked style
environment_lora                    # Optional scene-specific LoRA
seed                                # Optional for reproducibility
steps, cfg, sampler                 # Generation parameters
width, height                       # Widescreen ratio (16:9 or 21:9)
```

**Environment LoRAs (Optional):**

These add thematic elements to environments when generic prompts aren't enough:
- `medieval_interior` - Taverns, throne rooms, castles
- `fantasy_forest` - Enchanted woods, clearings
- `dungeon_atmosphere` - Stone corridors, crypts
- `mountain_vista` - Peaks, cliffs, alpine scenes

Phase 1 focuses on prompt-driven environments. LoRA support is Phase 2/3.

---

## MCP API Design

### Core Tools

**Session Management:**
```
start_session(style: "hd_pixel_art" | "oil_painting" | ..., color_grading?) → session_id
  - User or LLM selects rendering style for entire session
  - Phase 1: Only "hd_pixel_art" supported
  - Locks in style LoRAs for visual consistency
  - Optional color grading (retro palette, CRT effect, etc.)
  - Returns session_id for subsequent calls
```

**Character Enrollment:**
```
enroll_character(session_id, name, description) → character_id + reference_image_url
  - Generates 3-5 candidate portraits
  - Auto-selects best using quality heuristics
  - Extracts InstantID embedding
  - Returns selected reference for LLM to show user
  - Character available immediately for generation
```

**Portrait Generation:**
```
generate_portrait(session_id, character_name, emotion?, pose?, context?) → image_url + metadata
  - Uses enrolled character's InstantID
  - Applies session style
  - LLM provides context from conversation (e.g., "after battle, exhausted")
  - Pose control via prompt (Phase 1) - "arms crossed", "hand on hip", etc.
  - ControlNet pose support in future phases
  - Returns portrait image
```

**Environment Generation:**
```
generate_environment(session_id, location_description) → image_url + metadata
  - No character identity
  - Applies session style + optional environment LoRA
  - Widescreen aspect ratio
  - Can cache/reuse (e.g., "tavern interior" used multiple times)
```

**Scene Composition (Phase 2):**
```
compose_scene(portrait_url, environment_url, layout?) → composed_image_url
  - CRPG-style composition
  - Layout: "side_by_side", "portrait_overlay_left", "portrait_overlay_right"
  - Returns composited image
```

### Metadata Output

Each image returns:
```json
{
  "image_url": "file:///path/to/image.png",
  "seed": 12345,
  "timestamp": "2025-01-07T...",
  "prompt": "full prompt used",
  "workflow": "portrait" | "environment",
  "character_name": "Alice" (if portrait),
  "session_id": "abc123",
  "style_loras": ["pixel_art_xl.safetensors"],
  "instantid_used": true
}
```

---

## Generation Defaults & Guardrails

### Negative Prompt (Pixel Art Adjusted)
```
bad anatomy, extra limbs, distorted hands, melted skin,
low quality, blurry, artifacts, deformed pupils, warped face,
inconsistent pixel size, mixed art styles, messy dithering
```

### Stable Defaults
- sampler: `DPM++ 2M Karras` or `Euler a` (test with pixel art LoRA)
- steps: 24-32 (pixel art may need fewer)
- cfg: 6.0-7.0 (pixel LoRAs can be sensitive to CFG)

### Resolution

**Portraits:**
- 1:1 → 1024×1024 (square portrait)
- 2:3 → 896×1344 (classic portrait ratio)
- Must be multiples of 64

**Environments:**
- 16:9 → 1344×768 (widescreen)
- 21:9 → 1536×640 (ultrawide cinematic)

**VRAM budget:** ~12GB allows SDXL + InstantID + LoRAs comfortably

### Seed Handling
- Default: random (metadata always records for reproducibility)
- User can request regeneration with same seed for variants
- Character enrollment uses different seeds per candidate

---

## Implementation Phases

### Phase 1: Foundation (MVP)
**Goal:** Generate consistent character portraits and environments

**Core Features:**
- ComfyUI workflow: Portrait (with InstantID)
- ComfyUI workflow: Environment (no character)
- Session style locking
- Character enrollment (auto-select best from 3-5)
- Basic MCP tools: `start_session`, `enroll_character`, `generate_portrait`, `generate_environment`
- Return image URLs (file:// paths or base64)
- Metadata JSON output

**Success Criteria:**
- Enroll 2-3 characters in pixel art style
- Generate multiple portraits with different emotions
- Verify face consistency across generations
- Generate environment backdrops (tavern, forest, etc.)
- Confirm session style consistency

### Phase 2: Polish & Composition
**Goal:** Improve quality and add scene composition

- Automatic scene composition (portrait + environment with layout options)
- ControlNet for pose control (optional character poses)
- Environment caching (reuse common locations by description hash)
- Quality improvements (better face detection, artifact filtering)
- Alternative styles (see "Additional Style Support" in Future Work)

### Phase 3: Advanced Features
**Goal:** Multi-character scenes and customization

- Multi-character portraits (Alice + Bob in same image)
- Regional prompting for complex scenes
- Character customization (outfit variants, equipment)
- Scene memory (recall previous environments)
- IP-Adapter hybrid mode if InstantID struggles with pixel art

---

## Future Work & Stretch Goals

### Integrated Scene Generation

Move beyond CRPG-style composition to **characters directly in scenes**:

**Single Character in Scene:**
- Character with InstantID applied + environment LoRA in one generation
- ControlNet Depth for positioning control
- Background/foreground prompt weighting

**Multi-Character Integrated Scenes:**
- Multiple characters with individual InstantID embeddings
- Regional prompting (character A left region, character B right region)
- Layout hints ("A standing, B sitting", "camera angle: low")
- Fallback: Generate separately and composite if identity collapse occurs

**Challenges:**
- InstantID interference when multiple characters present
- Environment LoRAs can fight character identity preservation
- Layout control requires ControlNet or regional conditioning

### Pose & Expression Control

**ControlNet Pose Integration:**
- OpenPose or DWPose for specific character postures
- Pre-defined pose library ("arms crossed", "hand extended", "sitting")
- User can provide reference pose image

**Expression Presets:**
- Fine-grained facial expressions beyond simple emotion prompts
- Expression LoRAs for consistent emotional range

### Outfit & Equipment Continuity

**Character State Tracking:**
- Store "current outfit" per character (e.g., "leather armor", "tavern clothes")
- Automatically include in prompts unless explicitly changed
- Equipment persistence ("Alice is now carrying a sword")

**ControlNet for Outfit Consistency:**
- Use ControlNet Canny/Lineart from previous image
- Preserves outfit silhouette across generations
- Useful for multi-scene continuity

### Camera & Shot Language

**Shot Continuity:**
- Track camera angle across scenes ("medium shot", "close-up", "wide establishing")
- Maintain framing style within a scene sequence
- ControlNet Depth for camera position consistency

**Cinematic Presets:**
- Pre-defined camera setups ("over-the-shoulder dialogue", "dramatic reveal")
- Consistent shot/reverse-shot for conversations

### Additional Style Support

**Oil Painting Style:**
- Classic illustrated storybook aesthetic
- Different LoRA set, same architecture
- May require different negative prompts and CFG settings

**Ink Sketch / Line Art:**
- Hand-drawn fine line aesthetic (original vision)
- Test if InstantID works with high stylization
- May need IP-Adapter hybrid approach

**Watercolor & Other Styles:**
- Expand style library over time
- Each style curated with tested LoRAs and generation parameters

### Scene Memory & Callbacks

**Environment Reuse:**
- Cache generated environments by description hash
- "Return to the tavern" → reuse previous tavern generation
- Maintains visual consistency for recurring locations

**Scene Variants:**
- Generate multiple angles/times of day for same location
- "Show the forest at dawn" vs "Show the forest at night"

### Character Expression Library

**Beyond Basic Emotions:**
- Nuanced expressions ("smirking", "suspicious glance", "determined")
- Micro-expressions for subtle character moments
- Expression intensity control

### Dynamic LoRA Selection

**Context-Aware LoRA Planning:**
- LLM provides scene context → planner selects appropriate LoRAs
- Not just session-locked style, but scene-specific additions
- Example: Combat scene → add "dynamic action" LoRA
- Example: Intimate conversation → add "soft lighting" LoRA

**LoRA Categories (from original design):**
- `style` - Rendering aesthetic (session-locked)
- `grade` - Color/tone/film look (scene-specific)
- `accent` - Texture/finish (optional enhancement)
- `pose` - Posture influence (when ControlNet not used)
- `lighting` - Light environment (scene mood)
- `environment` - Material language (forest, stone, neon)
- `detail` - Quality improvements (hands, faces, fabric)

**Weight Budgeting:**
- Automatically scale LoRA weights to avoid overload
- Priority order: style > character > grade > detail > accent

### User-Provided References

**Custom Character References:**
- User uploads character art → use as InstantID/IP-Adapter source
- Bypasses initial generation step
- Useful for established characters from other media

**Scene Reference Images:**
- User provides layout reference → use ControlNet to match composition
- "Make it look like this scene, but with Alice"

---

## Directory Layout

```
McpTools/
  McpImageGen/                    # New project in solution
    Services/
      ComfyUIClient.cs           # HTTP client for ComfyUI API
      SessionManager.cs          # Track sessions, locked styles
      CharacterManager.cs        # Enrollment, InstantID refs
      WorkflowBuilder.cs         # Build/patch workflow JSON
      ImageQualityScorer.cs      # Auto-select best candidate
    Models/
      Session.cs
      Character.cs
      GenerationRequest.cs
      GenerationResult.cs
    Tools/
      ImageGenTools.cs           # MCP tool definitions
    Data/
      sessions.json              # Persisted sessions
      characters.json            # Enrolled characters + refs

  ComfyUI/                       # Managed by Stability Matrix
    workflows/
      portrait_instantid.json    # Portrait workflow template
      environment.json           # Environment workflow template
    output/                      # Generated images + metadata
      {session_id}/
        portraits/
        environments/
        composed/
```

---

## Next Steps

### Phase 1A: ComfyUI Experimentation (Current)

Test the technical foundation before writing code:

1. Install InstantID nodes in ComfyUI (via Stability Matrix)
2. Find/test HD pixel art LoRA with SDXL 1.0 base model
3. Build basic portrait workflow in ComfyUI manually
4. Verify InstantID works with pixel art style (character consistency test)
5. Test environment generation with pixel art LoRA
6. Export working workflows as JSON templates

**Decision point:** If InstantID doesn't work well with pixel art during testing, pivot to IP-Adapter or hybrid approach before implementing the MCP server.

### Phase 1B: MCP Server Implementation

Once workflows are proven:

1. Create `McpImageGen` project in solution
2. Implement ComfyUI HTTP client (workflow submission + output retrieval)
3. Build character enrollment flow (generate → score → select best)
4. Implement MCP tool wrappers (`start_session`, `enroll_character`, etc.)
5. Add session state management (persist sessions and characters)
6. Test end-to-end with LLM client (Open WebUI or similar)

**Success metric:** Enroll 2 characters, generate 10+ portraits with consistent faces across different emotions.

