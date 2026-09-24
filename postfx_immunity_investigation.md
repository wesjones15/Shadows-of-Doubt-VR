# Post-FX immunity investigation: every avenue tried

Working notes on one specific, recurring problem across this project's history: making VR UI
(menus, panels, windows) render without inheriting the HDRP post-processing stack (bloom, DoF,
tonemapping, TAA, exposure) meant for the 3D scene, not for UI. Written so a future session (human
or assistant) doesn't re-walk ground already covered — several avenues here look promising on
first read and have already cost real time (and once, a crash) to rule out.

**Confidence key**: findings from this document's Era 3 onward are backed by git commit messages
with specific in-headset test results, or by direct log evidence gathered this session (interop DLL
inspection, before/after diagnostic logging). Era 1 (`v1_findings.md`) is **secondary and lower-
confidence** — that document itself admits "some regressions were never conclusively sourced," and
its own verdict is that the whole layer it describes was thrown out. Treat its claims as "what was
believed at the time," not verified fact.

**What "immune" actually requires, precisely**: a UI element only counts as genuinely immune if it
looks identical regardless of the 3D scene's post-FX state. Two things get conflated throughout this
history and need to stay separate:
1. **Content immunity** — is the UI's own rendered content (text, buttons) crisp, correctly exposed,
   free of stencil/z-fighting artifacts? Solvable by rendering to an offscreen texture via a camera
   with post-processing disabled for that camera specifically.
2. **Display immunity** — once that content is shown in the world (as world-space geometry, or a
   quad displaying a texture), does *that displayed object* also escape the *main* camera's post
   stack? This is the hard, still-unsolved part, and is what this whole document is about.

## Why this is hard here specifically

Post-FX-exempt UI is a solved problem in plenty of HDRP projects. It's been unusually hard in *this*
project for reasons specific to this codebase's situation, not because the underlying idea is exotic:

- **HDRP itself is a large, version-specific, interlocking system**, and this mod targets whatever
  HDRP package version this game happens to ship with — not a version this project chose or can
  upgrade. General HDRP knowledge (tutorials, forum answers, even this document's author's own
  starting assumptions) has repeatedly turned out to be *wrong for this specific version*: the
  assumption that `HDRenderPipelineAsset` holds default `FrameSettings` (true in many HDRP versions,
  documented in plenty of places) is simply false here — this game's version moved that onto a
  separate `HDRenderPipelineGlobalSettings` object instead, discovered only by inspecting the actual
  shipped interop DLL. Nothing about HDRP internals in this project can be trusted from memory or
  general docs without checking it against this specific build.

- **No source, no debugger, no Editor.** This is a compiled BepInEx/IL2CPP mod reflecting into
  another compiled, interop-generated game assembly — there's no HDRP source to read, no breakpoint
  to set inside its render pipeline, no Inspector checkbox to flip. Every question this document
  answers ("does this bit exist," "what does this method actually return," "what type does this
  property expect") had to be answered by decompiling the shipped interop DLL's metadata (the
  `.pecheck` tool built for exactly this) and cross-checking against in-headset behavior — there's no
  faster or more direct way to know.

- **IL2CPP interop has its own failure modes layered on top of HDRP's own complexity.**
  Ref-returning getters/methods silently drop writes made through them (confirmed at least twice,
  different APIs, same shape) — and this session found that *reads* through the same shape can be
  unreliable too, not just writes. Any HDRP API discovered to be `ref`-returning has to be treated as
  suspect on both sides, not just when writing to it.

- **This is a shipped game's baked HDRP asset, not a project this mod controls from scratch.**
  Features like `CustomPassVolume` support (`supportCustomPass`) and the native after-post-process
  queue (`FrameSettingsField.AfterPostprocess`) ship *off* by default, because the base game never
  uses them — the mod has to discover that they're off, discover where the actual toggle lives (not
  always where documentation says), and flip it at runtime on an asset instance it didn't create.

- **No compiled-shader authoring pipeline exists in this repo.** Unity doesn't support compiling new
  shaders from source text at runtime in a built player — shaders have to be authored in a Unity
  Editor project and shipped as an AssetBundle. This repo has never needed one before (confirmed —
  no `.shader` files, no `Assets/` folder, no AssetBundle anywhere in the tree) — so anything that
  needs its own purpose-built shader (a `LightMode`-tagged shader for `AfterPostProcess` filtering,
  say) means standing up an entire new build artifact, not a small code change. This pushed 4b's
  investigation toward guessing at a *stock* shader's undocumented property/tag expectations instead
  — which is what caused the one crash in this document (§4b.6): fiddling with an unfamiliar
  compiled shader's keyword/property state from outside, with no way to inspect what it actually
  expects beyond runtime reflection.

- **The VR eye cameras don't render the normal way.** Rather than being enabled and left to Unity's
  own per-frame SRP scheduling, they're `cam.enabled = false` and manually `Camera.Render()`-called
  each frame to feed the OpenXR swapchain. This is confirmed to rule out camera stacking entirely
  (Era 2e — undefined behavior, not a configuration bug), and raises a standing, only partially
  resolved question for everything else in this document: does every HDRP render-pipeline stage
  execute identically for a manually-invoked camera as it would for a normally-scheduled one? The
  magenta test (§4b.4) shows *some* `AfterPostProcess` content draws correctly through this manual
  invocation, so it's not a blanket "manual cameras skip this stage" problem — but it means every
  finding in this document is scoped to *this mod's specific camera-invocation pattern* and may not
  transfer to how HDRP behaves in a conventionally-driven project, including ones this document's
  author might otherwise generalize from.

- **No spare Unity layer.** A common trick for isolating UI from the main render pass (put it on its
  own layer, exclude that layer from the main camera, draw it separately) is unavailable — all 32
  layers (0–31) are already named and claimed by the base game, confirmed via a full audit. Anything
  that would normally lean on layer separation has to use shader-tag or render-queue-based filtering
  instead, both of which are harder to reverse-engineer without source.

---

## Era 0 — the original problem (base mod, pre-project)

The base mod renders UI as literal `WorldSpace` canvas geometry, drawn by the same HDRP camera as
the game world. It inherits the full post stack unconditionally — bloom, tonemapping, DoF, vignette,
color grading — none of which are meant for UI. This is why the base mod's menus looked washed out
and blurred, and it's the reason this entire investigation exists. Structural property of the
approach, not a settings bug.

## Era 1 — v1's RT-panel attempt (`v1_findings.md`, grain of salt)

v1 built `MapRTProjector.cs`: render a canvas via a dedicated camera in `ScreenSpaceCamera` mode with
post-processing disabled for that camera (`volumeLayerMask=0`) into an offscreen `RenderTexture`,
then display that texture on a world-space quad. This is architecturally the *same core mechanism*
the current `MenuRTPanel`/`TooltipRTPanel` independently arrived at in Era 3 — not a conscious reuse
(v1 was declared fully disqualified before Era 3 began), but a convergent rediscovery.

v1's own notes never record a test isolating **display immunity** specifically (nothing like this
session's DoF-toggle test). Its stated motivating goal (§0: "an interactable window genuinely immune
to scene post-processing") was carried forward into Era 3 as an *assumption*, not a verified
conclusion — the same gap this document exists to close, just never actually checked at the time.

v1 was abandoned for unrelated architectural reasons: `GameCanvas` doing double duty as both the
case-board UI root and the legacy HUD anchor (composite RT panel fought the HUD system with zero
coordination), an ownership model spread across ad-hoc bookkeeping instead of one source of truth,
and a `volumeLayerMask`/exposure tradeoff for the void room's ceiling color that was never resolved.
None of these are about display immunity specifically — the whole layer was thrown out before that
question was ever isolated.

## Era 2 — the CustomPass/second-camera saga (this repo, 2026-09-22 to 09-23, pre-RT-panel)

This happened in *this* project, confirmed via git log with detailed commit messages — high
confidence, distinct from v1. Five attempts in two days, while UI was still on the legacy
WorldSpace-canvas pipeline (RT panels didn't exist yet). **This is the direct ancestor of this
session's CustomPass investigation** — several root causes discovered here were independently
re-discovered this session before this document's author found these commits.

### 2a. First `CustomPassVolume` + `AfterPostProcess` attempt (`4862915`)
Global `CustomPassVolume` + `DrawRenderersCustomPass` at `CustomPassInjectionPoint.AfterPostProcess`,
redrawing `UILayer`'s content after the post stack. Moved `UILayer` from 5 to 15 to avoid a collision
with `VoidRoomController`'s takeover geometry. Confirmed via interop DLL reflection that the types
resolve before writing any code.

**Result**: never actually tested in this commit — see 2b.

### 2b. Second-camera-plus-blit (`a4d425a`)
In-headset test of 2a: **confirmed non-functional** — menus/hands still fully blurred with the DoF
stopgap disabled. Deleted the CustomPass code outright rather than leave it alongside a replacement.
Also reverted the layer-15 move: a full 0–31 layer audit showed layer 15 is `TextToImage`, a live
gameplay system (the document/note text-capture camera), not free.

Replacement: a genuine second `Camera` per eye, culled to `UILayer` only, its own `RenderTexture`,
post-processing disabled via `customRenderingSettings=true` + a `FrameSettingsOverrideMask` flagging
`Postprocess`/`ExposureControl` — then alpha-blitted onto the main eye camera's already-rendered
output via `Graphics.Blit` with the `UI/Default` shader, before the swapchain copy.

**Result**: superseded by 2c the same day — see below.

### 2c. Drop second camera, force `supportCustomPass` (`655f953`)
2b's blit didn't behave like a real alpha blend: PressAnyKey's translucent background came back
fully opaque, other menus flickered, the game world stopped rendering. Root-caused (at the time) as
`UI/Default`'s blend state depending on per-vertex color that `Graphics.Blit`'s implicit fullscreen
quad doesn't populate the way UGUI's own `CanvasRenderer` draw path does.

Reflecting against the interop DLL surfaced `RenderPipelineSettings.supportCustomPass` — an
asset-level gate, off by default, that 2a never checked. Removed all second-camera/blit machinery,
reinstated the `CustomPassVolume` approach (this is `CameraRig.SetupPostFXExemptPass`, which — dead,
unmodified in its core shape — is still in the codebase today), now preceded by forcing
`supportCustomPass = true`.

**Result**: still dead — see 2d, same day.

### 2d. Second camera again, rendering directly into the shared eye RT (`58d2d2c`)
2c's `CustomPassVolume`, even with `supportCustomPass` forced, was confirmed dead via a magenta
`overrideMaterial` diagnostic — **the exact same technique this session independently reinvented**
before finding this commit. Tried a second camera again, but instead of a separate RT + blit,
`_leftUICam`/`_rightUICam` render directly into the *same* `RenderTexture` the main eye camera
already wrote that frame (`clearColorMode=None`, `clearDepth=false` — just draw more geometry into
what's there, no compositing step at all).

This also retroactively explained the pre-project second-camera attempt's failure (`clearFlags=Depth`
"overwrote the scene with blue/black"): a fresh `HDAdditionalCameraData` defaults `clearColorMode` to
`Sky`, and that old attempt never set the field explicitly.

**Result**: introduced head-motion-linked ghosting — see 2e.

### 2e. Ghosting → HDRP doesn't support camera stacking (`99010b1`)
Root-caused via diagnostic logging: pose/rotation/projection matrices were byte-identical between
each eye's main and UI camera on every sampled frame, and the ghosting persisted regardless — **HDRP
does not support camera stacking** (Unity's own documented pipeline limitation). "Two cameras
contributing to one final image" is undefined behavior in this pipeline, not a settings bug reachable
by configuration.

Reverted to the exact pre-experiment state (`fdc72fa`) — one camera per eye, rendering everything
directly, no UI camera, no CustomPass. **Pivoted to the RT-panel approach**: render UI to a texture
via an independent, *non-stacked* camera, then display it on an ordinary quad the one real eye camera
renders normally — this is Era 3.

**Standing lesson from this era**: two cameras cannot contribute to one final composited image in
this HDRP version, in any configuration (separate RT+blit, shared RT direct-draw). Any future
approach that implies "a second camera also touches this frame" should be considered pre-disqualified
unless something fundamental about this constraint has changed.

## Era 3 — RT panels (current architecture, `MenuRTPanel`/`TooltipRTPanel`)

Independent camera renders a canvas into its own `RenderTexture`; that texture is displayed on a
world-space quad the *one* real eye camera renders like any other object — not stacking, since only
one camera ever contributes to the final frame. This is architecturally the same core mechanism as
Era 1's `MapRTProjector`, rebuilt cleanly.

**Confirmed solved**: z-fighting (the problem that made Era 0/1's WorldSpace canvases visually
unstable) — genuinely fixed, independent of the post-FX question, and reason enough on its own to
keep this architecture regardless of how the rest of this investigation resolves. Content-level
immunity (crisp text, no stencil bleed) — also solid, same mechanism as Era 1.

**The cursor was made genuinely immune** (commit `ad5d25c`) by drawing it as a UI `Image` on a second
`ScreenSpaceCamera` canvas sharing the panel's own projector camera — i.e., making it part of the
panel's *pre-rendered pixels* rather than separate real-world geometry. This is the one piece of the
interaction layer that achieved real display immunity, and it worked precisely because it never
became a separately-displayed object — it's baked into the same texture the quad shows.

**The laser cannot be made immune this way** — established this session, not re-litigated here in
detail. It must exist as real, moving 3D geometry tracked from the controller every frame, so it's
drawn live by the eye camera and inherits the full post stack regardless of any technique in this
document. Color-tuned (`0,4,4`) as a tradeoff, not a fix.

**The assumption that turned out false**: the quad itself — ordinary geometry in the eye camera's
frame — was assumed immune "for free" because its *content* is pre-rendered. Never tested until this
forked conversation. **Confirmed false** by toggling `ForceDisableDepthOfField = false` in
`com.sodvr.mod.cfg` (i.e., letting DoF run) and observing the `MenuRTPanel` quad visibly blur.

## Era 4 — this investigation (current forked conversation)

Everything below started from the Era 3 assumption being disproven. `CameraRig.SetupPostFXExemptPass`
— the dead code from Era 2c/2d, never deleted — became the basis for renewed diagnosis, initially
without realizing it was the same code from Era 2.

### 4a. Proposals discussed but not implemented/tested
- **`BeforePostProcess` depth/stencil write** (from a user-supplied, explicitly-unverified excerpt):
  write a fake "in focus" depth for the quad's footprint before DoF samples the frame, defeating DoF
  specifically. Only ever a DoF-only fix — doesn't touch TAA, bloom, exposure, tonemapping, since
  those aren't purely depth-driven. Abandoned once TAA became a stated concern (double-TAA suspected:
  the projector camera runs its own TAA on the RT content, then the quad gets the main eye camera's
  TAA a second time) — `AfterPostProcess` was judged the better target since it sits after the
  *whole* stack, TAA included. **Never actually built or tested.**
- **`HDRP/Unlit` with `Exposure Weight = 0`**: a real, documented HDRP material property that exempts
  a surface from auto-exposure. Would help exposure/bloom-triggering specifically. Deprioritized —
  panels already "light up fine as is," so there's no confirmed symptom this would fix. **Never
  tested.**
- The user-supplied excerpt's claim that stripping a layer from `Camera.cullingMask` breaks
  `CustomPassVolume` (draws nothing) was judged probably wrong (contradicts the common "hide from
  main pass, draw via custom pass" pattern used for outline/X-ray effects elsewhere), **but never
  actually tested either way** — moot regardless, since a full 0–31 layer audit (re-confirming Era
  2b's finding) showed no free layer exists in this game to isolate RT-panel quads on.

### 4b. `AfterPostProcess` `CustomPassVolume` — diagnosing why it doesn't fire

1. **Root-cause hypothesis, revised from Era 2's**: this HDRP version moved default `FrameSettings`
   off `HDRenderPipelineAsset` entirely, onto a separate `HDRenderPipelineGlobalSettings` object
   (confirmed via interop DLL inspection — Era 2's code, and this session's first assumption, both
   targeted the wrong type). `GetDefaultFrameSettings` is byref-returning — the same shape as
   `HDAdditionalCameraData.renderingPathCustomFrameSettings`, already proven earlier in this project
   to silently drop writes over the IL2CPP interop boundary. Fix: write through the backing-field
   property (`m_RenderingPathDefaultCameraFrameSettings`) instead. (commit `3adda22`)
2. **Expanded diagnostic** (commit `a3ada45`): confirmed the backing-field write *does* persist
   durably (`CustomPass` reads `True` via the backing field, both before and after, sticky across
   separate game sessions) — but `GetDefaultFrameSettings()` itself is an **unreliable read**: it
   flip-flopped on `Postprocess`, a bit known to be `True` (DoF visibly works), between two separate
   test runs. This diagnostic method cannot be trusted as an oracle for anything past this point.
3. Confirmed `RenderPipelineSettings.supportCustomPass` is genuinely `True`, via both
   `currentPlatformRenderPipelineSettings` and the `m_RenderPipelineSettings` backing field,
   durable across sessions.
4. **Magenta smoke test** (commit `86635cd`): pointed the existing dead pass's `overrideMaterial` at
   an unmistakable magenta `Sprites/Default` material. Result: draws on PressAnyKey void-room
   content, but **not** on `MenuRTPanel`'s own quad, despite both being on the same shared `UILayer`.
   This proves `CustomPassVolume` *does* fire for these manually-rendered eye cameras (ruling out an
   earlier worry that `cam.enabled=false` + explicit `Camera.Render()` might skip injection points
   entirely) — the puzzle narrows to *why this one object* is excluded.
5. **Periodic FrameSettings-bit tracker** (commit `94ed2fd`): tested a user hypothesis that something
   resets the bits at the PressAnyKey→MainMenu scene transition. Logged the bits every frame via the
   reliable backing-field read; they never changed after the initial write at frame 0, across the
   whole session including a confirmed scene-handle change. **Ruled out.**
6. **Shader-tag eligibility test** (commit `a0fc441`, reverted `1311e7b`): `DrawRenderersCustomPass`
   has `forwardShaderTags`/`depthShaderTags`/`cachedShaderTagIDs` fields — it filters eligible
   renderers by matching the renderer's *own real* material's shader tags, separately from whatever
   `overrideMaterial` later draws with. `MenuRTPanel`'s quad uses `UI/Default`, a legacy pre-SRP
   shader, which likely doesn't carry HDRP-recognized tags. Tested by swapping just the quad's
   material to the stock `HDRP/Unlit` shader (texture property found via runtime
   `Shader.GetPropertyType` reflection rather than assumed; explicit `_CullMode`/`_DoubleSidedEnable`
   set to preserve the yaw-only quad's required double-sided rendering).
   **Result: crashed on first launch** — `Player-prev.log` cuts off abruptly mid-stream with no
   exception or shutdown message, consistent with a native-level crash rather than a managed one.
   Second launch didn't crash, but also didn't produce magenta — the hypothesis wasn't confirmed even
   on the run that survived. **Reverted** rather than iterate further on an unexplained crash with no
   payoff.

### 4c. The native `HDRenderQueue.AfterPostProcess` mechanism — a second, separate system

Discovered via interop DLL inspection: HDRP has a **native, queue-based** after-post-process
mechanism, entirely separate from `CustomPassVolume`. Any material whose `renderQueue` falls in
`HDRenderQueue.k_RenderQueue_AfterPostProcessOpaque`/`Transparent`'s range gets drawn automatically by
HDRP's own internal `RenderAfterPostProcessObjects` pass, after the whole stack — no volume, no
custom pass class needed. Gated by its own distinct bit, `FrameSettingsField.AfterPostprocess`
(separate from `CustomPass`), forced alongside it in the same diagnostic write. This also validates
something in the user-supplied excerpt from 4a that was initially, incorrectly dismissed as a
conflation — a genuine material-render-queue-based after-post-process path really does exist.

**First test of this was actually wrong** — conflated it with 4b's shader-tag theory and re-tested
`HDRP/Unlit` eligibility for `DrawRenderersCustomPass` again, not the native queue mechanism. Caught
and corrected mid-session.

**Corrected test** (commit `bad627a`): kept the quad's existing, already-stable `UI/Default` material
entirely unchanged — only set its `renderQueue` to
`HDRenderQueue.k_RenderQueue_AfterPostProcessTransparent.lowerBound` (resolved to `3600`, confirmed
within the range `[3600, 3800]` — read via `RenderQueueRange.lowerBound`/`upperBound`, resolved
through interop DLL inspection of `UnityEngine.CoreModule`). `FrameSettingsField.AfterPostprocess`
already confirmed enabled and durable from 4b.

**Result**: log confirms the write took (`renderQueue set to AfterPostProcessTransparent range [3600,
3800], using value 3600`), the FrameSettings bit stayed `True` throughout — and the panel was **still
blurry** under DoF. Clean negative result: every prerequisite gate identifiable so far is correctly
set, and the mechanism still doesn't exempt this object.

---

## What's actually been tested, as a matrix

The user's own caution applies directly here: most of the above were tested **in isolation**, not in
combination, and changing two variables between tests (mechanism *and* shader, in the 4b→4c
confusion) briefly produced a genuinely wrong conclusion before being caught. Explicit matrix of what
varied and what didn't:

| # | Mechanism | Quad's shader | Gates confirmed set? | Result |
|---|---|---|---|---|
| 2a/2c/2d (Era 2) | `CustomPassVolume` @ `AfterPostProcess` | `UI/Default` (legacy WorldSpace canvas, pre-RT-panel) | `supportCustomPass` forced (2c), but the per-camera `CustomPass` FrameSettings bit was never found/fixed | Dead — never drew anything |
| 4b.4 (magenta) | `CustomPassVolume` @ `AfterPostProcess` | `UI/Default` (MenuRTPanel quad) | Yes — both `supportCustomPass` and `CustomPass` bit confirmed | Fires (PressAnyKey content), but not this quad |
| 4b.6 | `CustomPassVolume` @ `AfterPostProcess` | `HDRP/Unlit` (MenuRTPanel quad) | Yes | Crashed once; didn't work on the run that survived |
| 4c (`bad627a`) | Native `HDRenderQueue.AfterPostProcess` | `UI/Default` (MenuRTPanel quad) | Yes — `AfterPostprocess` bit confirmed | renderQueue write confirmed; still blurry |

**Combinations never tried**:
- Native `HDRenderQueue.AfterPostProcess` queue **with `HDRP/Unlit`** (or any properly HDRP-tagged
  shader) — the one cell in this matrix that most directly tests whether the native queue path has
  the *same* shader-tag eligibility requirement `DrawRenderersCustomPass` appears to have, isolated
  from the crash that derailed the last shader-swap attempt.
- `BeforePostProcess` depth-write, in any form — not tried at all, isolated or combined with anything.
- `HDRP/Unlit`'s `Exposure Weight = 0` — not tried at all.
- A custom-authored shader (not a stock one) with a deliberately distinct `LightMode` tag, targeted
  by either mechanism's shader-tag/pass filter — the fix this document's evidence increasingly points
  toward, not yet built. This repo currently has **zero** shader-authoring or AssetBundle
  infrastructure (`.pecheck`-style interop inspection only), so this is a real infrastructure cost,
  not a small addition.

## Recommended next step, ranked

If picking this up cold, don't start by re-deriving a plan from the matrix above — start here:

1. **Retry `HDRP/Unlit` eligibility, but on the native queue path (4c), not `CustomPassVolume` (4b).**
   This is the one untested cell that would most cheaply confirm or kill the "needs a properly
   HDRP-tagged shader" theory, without repeating 4b.6's crash — the crash happened while testing the
   `CustomPassVolume` mechanism specifically; whether it repeats on the queue-based path is itself
   unknown and worth finding out with a save backed up first. If it crashes here too, that's evidence
   the crash is about the shader/material manipulation itself, not the mechanism — informs whether a
   custom-authored shader is truly required before touching `HDRP/Unlit` again at all.
2. **If that's inconclusive or crashes again**: stand up the shader-authoring/AssetBundle pipeline and
   build a minimal purpose-made shader, rather than continuing to guess at stock shaders' undocumented
   property/tag expectations from outside. This is the path the evidence has been pointing toward
   since 4b.6, and removes the guessing-at-a-black-box risk entirely — testable in the Unity Editor
   before it ever touches this game.
3. **`BeforePostProcess` depth-write** remains a smaller, lower-effort fallback that only fixes DoF
   specifically (not TAA/bloom/exposure) — worth doing only if 1–2 stall out completely and a partial
   fix is judged better than none.

## Current live code state — what's actually in the codebase right now

Not all of the above was cleanly reverted. A fresh session should know exactly what's still sitting in
production files before assuming the code reflects settled architecture:

- **`SoDVR/VR/CameraRig.cs`**: `SetupPostFXExemptPass` still installs the magenta `overrideMaterial`
  smoke test from §4b.4 (`Shader.Find("Sprites/Default")`, `Color.magenta`) — still called from
  `VRCamera.cs`, still live. `DiagnoseDefaultFrameSettingsCustomPass` (§4b.1–4b.3's diagnostic writes
  and logging) is also still present and still called once at startup. `PeriodicCheckFrameSettingsBits`
  (§4b.5) is still called **every single frame** from `VRCamera.Update()` — harmless (only logs on
  change, and hasn't logged since frame 0) but real per-frame overhead sitting in a hot path.
- **`SoDVR/VR/MenuRTPanel.cs`**: the `renderQueue` test from §4c (`bad627a`) is still live in `Setup()`
  — the quad's material still has its `renderQueue` forced into
  `HDRenderQueue.k_RenderQueue_AfterPostProcessTransparent`'s range. This has no visible effect today
  (confirmed still blurry) but means the quad is *not* currently on its original default render queue.
- **`SoDVR/VR/PostProcessingOverride.cs`**: unchanged throughout this whole investigation —
  `ForceDisableDepthOfField` is still the only thing actually suppressing the symptom in the shipped
  build.
- **Reverted, not live**: the `HDRP/Unlit` shader-eligibility test (§4b.6) — reverted in `1311e7b`,
  `MenuRTPanel`'s quad is back on `UI/Default`.
- **`.pecheck/Program.cs`** is a scratch tool, not a persisted reference — it currently sits pointed at
  whatever was last inspected (`RenderQueueRange` in `UnityEngine.CoreModule.dll`, from §4c's
  investigation) and gets overwritten by whatever the next interop question is. Don't treat its current
  contents as meaningful; treat the *technique* (decode a specific interop DLL's metadata for a
  specific type/method, via `System.Reflection.Metadata`/`PortableExecutable`) as the reusable part.

None of this diagnostic/test code should be treated as intentional design — it's exactly what CLAUDE.md's
no-coexistence rule means to flag for cleanup once a real fix lands or this investigation is shelved,
not evidence of a decision to keep any of it.

## Standing constraints, confirmed across multiple eras

- **HDRP does not support camera stacking in this game's build** (Era 2e). Any future idea implying
  a second camera also contributes to the final frame is pre-disqualified.
- **No free Unity layer exists** — all 32 (0–31) are named/claimed by the base game, confirmed via a
  full audit (Era 2b, re-confirmed Era 4). Any fix relying on layer-based isolation needs a different
  mechanism (shader-tag filtering, explicit renderer lists, render-queue ranges) instead.
- **The laser cannot be made display-immune** — must exist as live 3D geometry, inherits the full
  post stack unconditionally. Not in scope for any technique in this document.
- **IL2CPP interop silently drops writes through ref-returning getters/methods**, repeatedly
  confirmed (`renderingPathCustomFrameSettings`, `GetDefaultFrameSettings`) — any future HDRP API
  interaction should be checked for this shape (a `ref`-returning accessor) before trusting a write
  through it, and reads through the same shape should be treated as unreliable too (per 4b.2).
- **`PostProcessingOverride.cs`'s global `ForceDisableDepthOfField` config toggle remains the only
  currently-deployed mitigation** — a blanket, non-surgical stopgap its own doc comment already
  flags as temporary. Still active; nothing in this document has replaced it yet.
