# SoDVR v1 Findings

Working notes from the first development pass on SoDVR (a BepInEx/IL2CPP VR mod for
*Shadows of Doubt*, built on Unity's HDRP pipeline). Written for whoever picks this up next —
including a future instance of the assistant that helped build it — so the same mistakes aren't
repeated and the same investigations don't have to be redone.

Repo: `SoDVR/` (main files: `VR/VRCamera.cs`, `VR/MapRTProjector.cs`, `VR/Rooms/VoidRoom.cs`,
`VR/CaseBoard/*.cs`, `Plugin.cs`). Deploys to `BepInEx/plugins/SoDVR.dll`.

---

## 0. Handoff verdict — read this first

**This document is a handoff for a ground-up rewrite, not a foundation to patch.** As of
2026-09-22 the decision is: the entire menu/panel/interaction layer is being thrown out and
rebuilt with a different approach. This is not a call to keep iterating on what's here — treat
everything below as documented history (what was tried, what broke, what was learned about the
base game), not as code or architecture to build the next version on top of.

**Both UI approaches tried so far are disqualified, for different reasons — not just "buggy
implementations of a sound design":**

- **The base mod's original approach** (literal world-space canvas geometry, §2 "Legacy
  world-space UI conversion") is rendered by the same HDRP camera as the game world, so it
  inherits the full post-processing stack — bloom, tonemapping, depth-of-field, blur — meant for
  the scene, not for UI. This is what made menus look washed out/blurred in the base mod, and it's
  a structural property of rendering UI-as-world-geometry through one camera, not something
  fixable with settings tweaks. **This is the original reason the RT-panel work in this document
  was started at all** — that motivating requirement (an interactable window genuinely immune to
  scene post-processing) is still correct and should carry forward even though the implementation
  below shouldn't.
- **This project's RenderTexture-panel/composite work** (all of §2 below that point) hit real
  architectural conflicts — `GameCanvas` doing double duty as both a UI root and the legacy HUD
  anchor, multiple uncoordinated per-frame passes touching the same canvases, an ownership model
  spread across ad-hoc `HashSet`/`Dictionary` bookkeeping instead of one source of truth (see §1).
  Some of these were fixed as they were found; **some regressions were never conclusively
  sourced** even with full git history and log access — the incremental fix-and-verify loop hit
  diminishing returns and went in circles. Don't assume this approach is "almost there" — treat it
  as a disproven direction for a menu system, not a bug list to finish clearing.

**What genuinely is salvageable, and why it's different from the above:** the non-UI base-mod
systems — VR head/controller tracking, camera rig setup, locomotion, HDRP eye-camera plumbing —
were never implicated in any regression chased in this document, because they aren't part of the
menu/panel/interaction surface. So is the base-game API catalogue in §3: which singleton exposes
what, how it's accessed, what's IL2CPP-interop-only versus a direct compiled reference. Both are
independent of which panel approach gets built next.

**Do not coexist with the base mod's panel/canvas system — delete it.** A repeated mistake in
this document's own history (see the nested-canvas-walk fix in §2/§4) was finding that a new
mechanism and an old one were fighting over the same canvas, and "fixing" it by adding an
exemption so the old system would leave the new one's canvases alone. That's a patch, not a fix —
it keeps the disqualified system running and adds another special case to it. The base mod's
entire canvas-to-world-space pipeline (`ConvertCanvasToWorldSpace`, the reparent-to-scene-root
pass, the "nested canvas" legacy walk, `CanvasCategory`-based world-space placement, the
`CanvasScaler`-disable-on-nested step) is in scope to be **deleted outright** as part of ripping
out the panels/interactions, not kept running alongside whatever v2's mechanism turns out to be
with exemption checks bolted on. If the new mechanism needs something the old one did (e.g.
detaching a canvas from a shared parent so it can be positioned independently), reimplement that
specific behavior inside the new mechanism — don't leave the old pass in place and teach it to
step aside.

**Organizational mandate for the rewrite**, per explicit direction: clean, well-documented,
multi-file from the first commit — not one growing monolith, and not maximum fragmentation either.
Whatever the new panel/interaction mechanism turns out to be, it gets its own file(s); the
orchestrator (`VRCamera.cs` or its replacement) should hold the loop/coordination and delegate,
not contain the implementation. The base mod's own `VRCamera.cs` was already a 9,085-line
non-partial monolith before this project touched it (confirmed via
`Shadows-of-Doubt-VR-original_Backup`) — that's inherited technical debt, not something to
preserve. See the `avoid-monolithic-files` memory: a single huge file makes small edits expensive
(the whole file has to be read/re-read for a one-line change) and makes it much harder to notice
when two independent systems are touching the same shared state — which is the actual root cause
behind most of the "what didn't work" entries in §4, not incidental to them.

---

## 1. Key learnings

- **BepInEx config files are not git-tracked.** `BepInEx/config/com.sodvr.mod.cfg` lives outside
  the repo and BepInEx never resets an existing key to a new build's default — so a stale config
  value survives a `git reset --hard` to an earlier commit and can make an old build look like it
  still has a bug that was actually fixed in source. Always check the live config, not just the
  DLL, when a rollback "doesn't seem to have worked."

- **`GameCanvas` has two jobs, and that's the root of most of the case-board pain.** It's both
  the root canvas for the entire case-board UI subtree (navbar, corkboard, tabs) *and*,
  independently, the anchor the mod's legacy world-space/HUD system continuously manages every
  frame regardless of case-board state. Any approach that requires the case-board subtree to stay
  nested inside `GameCanvas` (the composite/RT-whole-canvas approach) inherits that second job
  for free and fights it. Any approach that detaches the case-board canvases from `GameCanvas`
  (reparenting to the scene root, which is what both the legacy world-space path and the
  standalone RT-panel path do) escapes the conflict entirely because there's nothing shared left
  to fight over.

- **HDRP's `volumeLayerMask` couples sky/environment to Exposure/Tonemapping/ColorGrading on a
  camera — you cannot cleanly separate them.** Pinning it to fix the void room's ceiling color
  also cut the eye cameras off from the HDRP Volume that maps exposure, which made the menu panel
  render almost black. There is no way to keep one and drop the other via camera settings alone;
  fixing "wrong sky color" without breaking "correct exposure" needed a different mechanism
  entirely (a real ceiling mesh, or living with the tradeoff) — this was never fully resolved (see
  §4).

- **A "cosmetic" pre-hide/deferred-attach optimization must have an expiry, or it silently deletes
  UI.** Moving a canvas to an unrendered layer as a bet that an RT projector will attach shortly
  is fine until the bet loses — then the canvas is invisible forever with no error. Fixed by
  giving every pre-hide a frame-count grace period (`PreHideGraceFrames = 180` in
  `MapRTProjector.cs`) after which it's handed back regardless of whether a projector ever showed
  up.

- **Two independent systems touching the same `Canvas` object is a recurring failure class in
  this codebase**, not a one-off bug. It happened at least twice: (1) the composite's
  `OwnsCanvas` exemption vs. the world-space reparent pass, and (2) the RT attach pipeline vs. a
  legacy "nested canvas" walk that had no RT-awareness at all and kept patching
  `CanvasScaler`/Z-test on `CaseCanvas` every scan cycle while `MapRTProjector.Attach()` was
  independently trying to reparent and reconfigure the same object. When adding a new canvas
  category to any pipeline, audit every *other* per-frame pass that walks canvases and make sure
  it explicitly excludes what the new pipeline owns — don't assume owning one entry point is
  enough.

- **Ship detection logic only after it's been checked against a log with both states present.**
  Getting from "main menu" to "distinguish main menu from in-game pause" took four attempts
  because three of them were shipped on inference (`cullingMask == 0`, `_gameCamSavedMask != 0`,
  `MainMenuController.mainMenuActive`) rather than verified against one log capturing both states.
  The one that actually held up (`SessionData.Instance.startedGame`, inverted) was the one
  actually checked this way. The `[MenuSignal]` diagnostic exists specifically so the next
  ambiguous signal gets caught the same way before shipping.

- **A culling-mask/state "not yet known" sentinel must be a separate flag, never a value-range
  check.** `mask < 0` looked like a reasonable "uninitialized" sentinel, but real Unity culling
  masks (including this game's actual gameplay mask, `0xFFA57FF7`) are negative as signed ints, so
  the sentinel matched valid data and the mask restore silently never ran — eye cameras stayed
  clamped to the void room's layer forever. Fixed with an explicit `bool known` flag instead of
  inferring "unknown" from the value.

- **IL2CPP interop in this project is mostly direct compiled references, not reflection.** The
  project references the game's unhollowed `Assembly-CSharp.dll` directly (see §3), so most base
  game types are called exactly like normal C# — reflection (`GetIl2CppType().GetMethod(...).Invoke(...)`)
  is reserved for the rare case where no compiled reference exists and a method name isn't even
  confirmed (`ContextMenuController.OpenMenu`, with a fallback list of candidate names). Don't
  reach for reflection by default — check whether a normal typed reference already works first.

---

## 2. Changes made to the original mod

*Everything in this section is disqualified as a foundation per §0 — kept as a record of what was
built and why, not as code or architecture to extend. The void-room/main-menu-detection material
is the exception worth treating as closer to reusable, since it's about camera/scene state, not
the panel/interaction layer.*

### Main menu / press-any-key void room
- **What:** Replaced the game's real skybox-and-water backdrop behind the main menu and
  press-any-key screen with the mod's own minimal geometry (`VoidRoom.cs`) — floor rings, corner
  posts — instead of the expensive real environment.
- **How:** `VoidRoom.SetVisible()` toggles a persistent, `DontDestroyOnLoad` GameObject; the eye
  cameras' `cullingMask` is masked down to the room's own layer while it's shown
  (`VoidRoom.TakeOverCameras`), and restored to the real gameplay mask on release
  (`VoidRoom.ReleaseCameras`), gated on an explicit `known` flag rather than a sentinel value (see
  §1).
- **Main-menu-vs-pause distinction:** both states use the same `MenuCanvas`, so no menu-visibility
  signal works. Settled on `SessionData.Instance.startedGame`, inverted (`TryGetMainMenuActive()`
  in `VRCamera.cs`), after three other signals were tried and disproved (see §4).

### Void room rendering-environment fix
- **What:** Fixed the void room's ceiling silently changing color and gaining aliasing exactly at
  the press-any-key → main-menu boundary.
- **How:** `EyeRenderState` (`VoidRoom.cs`) bundles `clearColorMode` / `backgroundColorHDR` /
  `antialiasing` so they can be captured once (as a "neutral" snapshot, before the game camera's
  settings are ever copied onto the eye cameras) and pinned/restored alongside the culling mask
  the room already switches. Deliberately **excludes** `volumeLayerMask` — see §1 and §4.

### RenderTexture (RT) UI panel architecture
- **What:** The core mechanism for showing any game UI canvas in VR without world-space geometry's
  problems (HDRP post-processing bleeding onto text, stencil masks not clipping, exposure/bloom
  affecting UI). Render the canvas in ordinary screen space into an offscreen `RenderTexture` via
  a dedicated UI camera with post-processing disabled, then display that texture on a plain
  world-space quad.
- **How:** `MapRTProjector.cs`. `Attach(Canvas)` reparents the source canvas to the scene root
  (`SetParent(null, false)`, required because Unity only honours `renderMode` on root canvases),
  sets it to `RenderMode.ScreenSpaceCamera` against the projector's own camera (post-processing
  off, `volumeLayerMask=0`), and renders it into a `RenderTexture` sized to match the game's real
  screen resolution. `Place()`/`Follow()` position the display quad in front of the player.
  `ProcessPointer()` synthesizes `PointerEventData` from the VR controller ray and routes it
  through the canvas's own `GraphicRaycaster`, so the game's normal UI click handlers fire
  unmodified.
- **Which canvases:** controlled by the `RTCanvasName` config (substring match, comma-separated).
  Current set: `MenuCanvas, DialogCanvas, WindowCanvas, MinimapCanvas, PopupMessage, CaseCanvas,
  ActionPanelCanvas, UpgradesDisplayCanvas`.
- **Deferred attach:** canvases are queued (`_rtPending`) rather than attached immediately —
  attaching costs an HDRP camera and a full render-target chain, and doing this eagerly for every
  canvas at startup was crashing launches from VRAM pressure. A canvas is pre-hidden (moved to an
  unrendered layer) the moment it's queued so it can't flash as ordinary un-projected UI before
  its projector attaches, and promoted to a real projector once `CanvasHasContent()` returns true
  (checked every 10 frames).

### Legacy world-space UI conversion (the pre-RT path, still used for anything not in `RTCanvasName`)
- **What:** Converts a screen-space canvas into literal world-space geometry positioned in front
  of the player, for UI that hasn't been moved onto the RT path.
- **How:** `ConvertCanvasToWorldSpace()` disables `CanvasScaler` (it re-inflates `sizeDelta` from a
  reference resolution and fights manual sizing), resets `scaleFactor` to 1, sets
  `renderMode = RenderMode.WorldSpace`, and scales by `TargetWorldWidth / sizeDelta.x` so it's
  immune to `CanvasScaler` inflation. A separate reparent pass then detaches nested canvases to
  the scene root (`SetParent(null, true)`) so they don't inherit a scaled parent's transform and
  become independent, individually-positioned world objects — this is the same escape-the-shared-root
  mechanism the RT path uses (see §1), inherited from the pre-mod original implementation
  (confirmed identical in `Shadows-of-Doubt-VR-original_Backup`).

### Case-board presentation: composite attempt, then per-canvas RT panels
- **What:** Tried presenting the entire case board (navbar + corkboard + tabs) as ONE RT panel by
  projecting `GameCanvas` itself, since it's already the root canvas containing the whole subtree
  — the same trick `MenuCanvas` already gets for free. Ultimately abandoned; see §4 for why.
- **How (built, then hard-disabled):** `ICaseBoardPresenter` interface + `CompositePresenter`
  (`VR/CaseBoard/`), selected via `Experimental/CaseBoardMode` config. `EnsureCaseBoardPresenter()`
  in `VRCamera.cs` now returns unconditionally before ever constructing a presenter — the
  composite is dead code, kept (behind `#pragma warning disable CS0162`) for a possible future
  `ModularPresenter`.
- **Current approach:** each case-board canvas (`CaseCanvas`, `ActionPanelCanvas`,
  `UpgradesDisplayCanvas`) is its own standalone RT panel via the same `MapRTProjector` mechanism
  used everywhere else — no `GameCanvas` involvement, no shared-root conflicts.

### Case-board panel stacking, shared anchor, and locomotion freeze
- **What:** Fixed the navbar rendering in front of the corkboard (backwards from intended), gave
  screens opened from the navbar (upgrades, and inventory once its canvas is confirmed) their own
  nearer RT panel tier, made every panel in one case-board session share a single
  position/bearing instead of each reading the live head at its own attach moment, and froze
  player locomotion/turning while the case board is open.
- **How:**
  - Explicit per-canvas distance overrides at the placement call site (`VRCamera.cs`):
    `ActionPanelCanvas` pushed to 2.55m (back), `CaseCanvas` stays at its 2.3m category default
    (middle), `UpgradesDisplayCanvas` (and future overlay screens) at 1.95m (front).
  - `MapRTProjector` gained a `Place(Vector3 originPos, Vector3 originFwd, ...)` overload taking
    an explicit origin/bearing instead of reading a live `Transform`. Whichever case-board-family
    panel (`IsCaseBoardFamilyCanvas`) attaches first in a session captures the head's
    position/yaw-only-forward once (`_caseBoardAnchorPos/Fwd`); every other panel in the same
    session reuses it. Cleared when `ActionPanelCanvas` closes so the next session captures fresh.
  - `Follow()` (keeps a panel a fixed distance/bearing from the player as they walk) extended from
    `CanvasCategory.Panel`-only to also cover `CanvasCategory.CaseBoard`.
  - `UpdateLocomotion()`/`UpdateSnapTurn()` now return early while `ActionPanelCanvas` is active —
    previously they only checked the VR settings panel, so the player could walk and turn freely
    with the case board open.

### RT attach reliability fixes
- **What:** Two bugs that could leave a queued canvas (most visibly `CaseCanvas`) permanently
  un-attached with no error.
- **How:**
  1. The `_rtPending` promotion loop removed a canvas from the queue unconditionally after calling
     `Attach()`, regardless of success — so a canvas whose first attempt failed for any transient
     reason was silently dropped forever. Fixed to only remove on success; failure leaves it
     queued for retry with sparse warning logging (`_rtAttachFailCounts`).
  2. A separate legacy "nested canvas" walk (predates the RT work, has no RT-awareness) was
     re-patching `CanvasScaler`/Z-test on `CaseCanvas` every scan cycle while `MapRTProjector.Attach()`
     was independently trying to reparent and reconfigure the same object — a race that was the
     actual reason first-attempt attach kept failing. Fixed by exempting `MapRTProjector.IsTarget()`
     canvases from that walk entirely. The same walk was also unconditionally marking RT-targeted
     canvases as "always nested — never independently positioned," which is why a panel that *did*
     attach then sat static in the world instead of tracking the player.

---

## 3. Base game classes and methods accessed

The mod references the game's IL2CPP-unhollowed `Assembly-CSharp.dll` (and
`Assembly-CSharp-firstpass.dll`) as an ordinary compiled `<Reference>` in `SoDVR.csproj`
(`HintPath` into `BepInEx/interop/`, `Private=false`) — **not** purely through reflection. Game
types are called by name exactly like normal C#; Il2CppInterop preserves the game's original
namespace/class names verbatim (no `Il2Cpp`-prefixed wrapper for game-authored types — that
prefix is reserved for proxy/system assemblies like `Il2Cppmscorlib`). True reflection
(`GetIl2CppType().GetMethod(name).Invoke(...)`) is used in exactly one place, deliberately, where
no compiled reference exists (see `ContextMenuController` below).

### `.Instance` singletons (the dominant access pattern)

| Type | What it does | Used for |
|---|---|---|
| `SessionData.Instance` | Session/game-state; `.startedGame` (bool) | Main-menu-vs-in-game-pause detection (inverted) — the one signal that actually works (§1, §4). Also `.enableTutorialText` is force-set `false` to suppress tutorial popups that fight VR canvas handling. |
| `MapController.Instance` | Drives the in-game map/minimap: pan/zoom, cursor-to-node mapping | `.zoomController.desiredZoom`/`.zoomLimit` set the map's VR starting zoom; `.mapCursorNode` is driven manually every frame (the game's own screen-to-map cursor mapping breaks for the mod's world-space/RT canvases); `.MapToNode()` resolves a screen point to a graph node; `.directionalArrow(Container)` read for the route-arrow. |
| `PopupMessageController.Instance` | Modal popup/tutorial dialogs | `.active` distinguishes a popup-triggered desktop-mode flip (force-reverted) from a real ESC pause. |
| `InterfaceController.Instance` (also once via `FindObjectOfType`) | Top-level UI/HUD controller | `.SpawnWindow(evidence, DataKey)` opens evidence windows directly, bypassing normal click routing; `.desktopMode`/`.SetDesktopMode()` force-reverted when the game auto-enters desktop mode; `.firstPersonUI`/`.compassContainer` read for HUD placement. |
| `Toolbox.Instance` | Shared gameplay constants | `.interactionRayLayerMask` — the raycast layer mask for the mod's own interaction ray. |
| `GameplayControls.Instance` | Tunable gameplay parameters | `.interactionRange` — base interaction distance. |
| `PathFinder.Instance` | Map-node graph | `.nodeMap` looked up to resolve a screen point to a graph node for manual `mapCursorNode` driving. |
| `CasePanelController.Instance` | Evidence/case-board panel state | Used in case-board pointer/drag handling. |
| `InteractionController.Instance` (also found via `GetComponent` on the player) | Player's world-interaction/raycast system | `.carryingObject` read — one of the game's two "held item" systems (the other is first-person arms). |
| `PlayerPrefsController.Instance` | Persists/applies user settings | `.GetSettingInt()`/`.gameSettingControls`/`.OnToggleChanged()` — the mod's VR settings panel writes through this so changes take effect the same way the flat options menu's would. No `GetSettingFloat` exists on this type; float settings fall back to raw `UnityEngine.PlayerPrefs`. |
| `Game.Instance` | Top-level graphics/audio/gameplay settings API | `.SetVsync/.SetDepthBlur/.SetDithering/.SetScreenSpaceReflection/.SetAAMode/.SetAAQuality/.SetLightDistance/.SetEnableFrameCap/.SetFrameCap/.SetAllowLicensedMusic/.SetBassReduction/.SetHyperacusisFilter/.SetFOV/.SetDrawDistance/.SetGameDifficulty` — every one of the mod's VR settings panel toggles calls straight through to the same API the flat options menu uses. |
| `DynamicResolutionController.Instance` | HDRP dynamic resolution | `.SetDynamicResolutionEnabled()`. |
| `InterfaceControls.Instance` | Interface-related prefab/container registry | `.caseBoardCursorRBContainer` read. |
| `PrefabControls.Instance` | Prefab registry | `.customStringLinkSelect` read — the case-board "string link" (yarn) prefab. |

### Found via component lookup, not `.Instance`

| Type | How located | What it does |
|---|---|---|
| Player object (informally "FPSController" in comments — **not a type name**) | Walk **up** to 10 parents from the game's Main Camera calling `GetComponent<CharacterController>()`/`GetComponent<Rigidbody>()` until one is found. No `GameObject.Find` by name. | Locates the player's `CharacterController`/`Rigidbody`, cached as `_playerCC`/`_playerRb`. |
| `FirstPersonItemController` | `_playerCC.GetComponent<...>()` | Drives first-person held-item/arm animation; `.lagPivotTransform` overridden every `LateUpdate()` for VR hand tracking so the mod's write wins over the game's own `Update()`. |
| `Player` | `_playerCC.GetComponent<...>()` | Vent-state detection; also drives `directionalArrow.rotation` in its own `Update()`. |
| `DragCasePanel` | `GetComponent<...>()`, and separately by IL2CPP type-name string match when walking an arbitrary hit hierarchy | Marks a draggable case-board evidence pin; `.SetPositionDirect(Vector2)` called directly at drag-release so the game saves the new position. |
| `PinnedItemController` | `GetComponent<...>()` | The evidence-pin component; `.OpenEvidence()` called directly, bypassing the game's own click path (`OpenEvidence → EvidenceButtonController.OnLeftClick → SpawnWindow`). |

### Found by IL2CPP type-name string match (no compiled reference used, deliberately)

Used when walking an arbitrary component list and either no compiled reference exists, or the
mod doesn't want a hard compile-time dependency on a type it's only disabling:

- `"CameraController"` — mouse-look pitch/effects on the Main Camera; disabled (`mb.enabled =
  false`) so it doesn't fight the VR head-tracked camera.
- `"FirstPersonController"` — mouse-look yaw + WASD movement on the player object; disabled for
  the same reason.
- `"ContextMenuController"` — the case board's right-click context menu. **This is the one place
  the mod uses genuine reflection**: `GetIl2CppType().GetMethod("OpenMenu")` (with a fallback list
  of candidate names — `OpenMenu`, `OpenContextMenu`, `Show`, `ShowMenu`, `Open`) then
  `.Invoke(comp, null)`, specifically because no compiled reference exists and the exact method
  name wasn't confirmed ahead of time.

### Referenced only in comments (tried, rejected, or documented as evidence — not live code)

- `MainMenuController.mainMenuActive` — the first detection signal tried for main-menu-vs-pause;
  proven identical (`True`/`True`) for both states via the `[MenuSignal]` diagnostic, then
  abandoned in favor of `SessionData.startedGame` (§4).
- `StatusController`, `BioScreenController` — appear only in a quoted crash stack trace (doc
  comment) used as evidence for why canvases must be handed back to the game before a save-load
  rebuilds its own UI controllers.
- `VirtualCursorController.lastKnownPos` — cited as *why* the mod drives `mapCursorNode` manually
  rather than relying on the game's own cursor tracking (which reads `Input.mousePosition` via
  `Camera.main`, and breaks for the mod's projected canvases).
- `UpgradesController`, `CityConstructor` — named in the project's own planning notes as
  controllers to verify against, but never actually referenced in code.

### Unity/HDRP types manipulated in game-specific ways (not base-game-authored)

- **`HDAdditionalCameraData`** — read/written on both the game's own Main Camera and the mod's
  eye/UI cameras: `.clearColorMode`, `.backgroundColorHDR`, `.antialiasing`, `.volumeLayerMask`,
  `.flipYMode`, `.customRenderingSettings`, `.renderingPathCustomFrameSettings`. The game's own
  Main Camera settings are read once and copied onto the mod's eye cameras so VR rendering matches
  the game's HDRP configuration (exposure, tonemapping) — see §1 for why `volumeLayerMask` is
  deliberately excluded from that copy in the void room's case.
- **`Canvas`/`CanvasScaler`/`GraphicRaycaster`/`RectTransform`** — see §2 (RT panel and legacy
  world-space sections) for how these are force-converted and driven.
- **`Rewired.ReInput`** (third-party input middleware the base game itself uses, from
  `Rewired_Core.dll`, not IL2CPP-unhollowed) — `.mapping.GetAction(name)` and
  `.players.GetPlayer(0)` are probed for diagnostic logging of the game's configured input
  actions; not used for actual input.

---

## 4. What didn't work, and why

- **`cullingMask == 0` as a main-menu signal.** The mod itself zeroes the game camera's culling
  mask to suppress it during normal gameplay, so this read as 0 constantly during ordinary play —
  not just at the menu. Would have misfired constantly.

- **`_gameCamSavedMask != 0` as a main-menu signal.** Logged proof: at the main menu the game
  camera is real, sits at a real city position, and already carries the full gameplay mask
  (`0xFFA57FF7`). There is no separate menu scene — the menu sits over the live game world — so
  this signal never distinguished anything.

- **`MainMenuController.Instance.mainMenuActive` as a main-menu signal.** Shipped once before
  being caught. One run's `[MenuSignal]` log, both menu-open events:
  `title screen: mainMenuActive=True startedGame=False` vs.
  `in-game ESC pause: mainMenuActive=True startedGame=True` — identical on the field the fix
  actually shipped on. This is exactly why the void room briefly took over an in-game pause menu.

- **Pinning `volumeLayerMask` to fix the void room's ceiling color.** Fixed the ceiling, but cut
  the eye cameras off from the HDRP Volume that maps Exposure/Tonemapping/ColorGrading (kept live
  deliberately elsewhere in the codebase — killing post-processing wholesale leaves raw HDR values
  unmapped, which looks worse: blown highlights, crushed midtones). Result: the main menu panel
  rendered almost black. Removing `volumeLayerMask` from the pinned set fixed visibility but
  brought the ceiling-color bug back — **this tradeoff was never resolved**; the ceiling bug is
  currently back, prioritized behind menu visibility. A literal ceiling mesh (to give the room
  something to actually render instead of relying on clear-color/sky) was proposed as a
  workaround and explicitly rejected by direction; no replacement fix has been implemented.

- **A value-range sentinel (`mask < 0`) for "gameplay culling mask not yet known."** Real Unity
  culling masks (including this game's actual `0xFFA57FF7`) are negative as signed ints, so the
  sentinel matched valid data and the restore path silently never ran — this is what caused a
  full freeze/empty-world report (eye cameras stuck on the void room's layer forever). Fixed with
  an explicit `bool known` flag instead of inferring state from a value's sign.

- **Projecting `GameCanvas` as one composite RT panel for the whole case board.** Structurally
  appealing (it's already a root canvas containing the entire case-board subtree, exactly like
  `MenuCanvas`), but `GameCanvas` has a second job `MenuCanvas` doesn't: it's also the root the
  legacy HUD system continuously manages every frame (`_hudAnchor.SetActive(hudShouldShow)`),
  completely independently of the composite. Both systems reacted to the same "is the board open"
  signal on the same object tree with zero coordination — confirmed via a `[UIDepth]` diagnostic
  showing `GameCanvas` still reporting `WorldSpace` mode well after the composite had supposedly
  released it. The composite's own `OwnsCanvas` exemption (added to stop the world-space
  conversion pass from dismantling the subtree while the composite needed it intact) was itself
  found to be too broad in an earlier iteration — it returned true for anything under
  `GameCanvas` regardless of whether the composite was actually attached, which blacked out
  `MinimapCanvas`'s *separate*, non-case-board standalone-map use (fixed once, but the underlying
  GameCanvas-double-duty problem was never solved — the composite was hard-disabled instead).

- **Pre-hiding a queued RT canvas with no expiry.** A canvas moved to an unrendered layer while
  waiting for its projector to attach would stay there forever if the projector never came —
  invisible with no error, no fallback. This is exactly how the corkboard and the standalone map
  disappeared in one build. Fixed with a 180-frame grace period after which the canvas is handed
  back regardless.

- **Treating "already on the RT layer" as "already RT-owned."** The pre-RT-pass ownership check
  used to call a purely layer-based test (`IsRTOwned`, true for anything sitting on the RT layer).
  After a reload cleared the pending/attached bookkeeping, a pre-hidden canvas still looked
  "owned" by this test and was skipped on re-queue — so it was never picked up again. Fixed by
  checking the live projector list instead of the canvas's current layer.

- **Letting a legacy, RT-unaware pass keep processing an RT-targeted canvas.** The "nested canvas"
  walk (predates the RT work) had no awareness of `MapRTProjector.IsTarget()` at all, so it kept
  re-patching `CaseCanvas`'s `CanvasScaler`/Z-test every scan cycle while the RT pipeline was
  independently trying to reparent and reconfigure the same object — a race that caused the first
  several attach attempts to fail, and (separately) permanently flagged the canvas as "always
  nested, never independently positioned," so once it did attach it sat static in the world
  instead of following the player. Fixed by exempting RT-targeted canvases from that walk
  entirely — the general lesson (§1) is that adding a canvas to a new pipeline isn't enough; every
  *other* pass that might also touch it has to be told to back off.

- **A literal ceiling mesh to route around the volumeLayerMask/exposure conflict.** Proposed as a
  way to give the void room something to render overhead instead of relying on clear-color/sky
  settings, sidestepping the HDRP coupling entirely. Rejected by explicit direction before
  implementation; the change was reverted (`git checkout`) and no replacement has been built. The
  ceiling-color-vs-exposure tradeoff remains open (see above).
