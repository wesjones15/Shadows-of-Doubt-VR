# SoDVR case-board findings

Scoping notes for migrating the detective case-board UI onto the RT-panel architecture
(`MenuRTPanel.cs`/`TooltipRTPanel.cs`/`RTPanelPointer.cs`/`CameraRig.cs`'s projector-camera
helpers). Written the same way `v1_findings.md` was — for whoever picks this up next, including a
future instance of the assistant that helped scope it — so the same investigation doesn't have to
be redone. See `v1_findings.md` for the general RT-panel history/lessons and `ARCHITECTURE.md` for
how the current canvas subsystem files fit together; this document is case-board-specific and
narrower.

**Status as of 2026-09-24: scoping only. No case-board-specific RT code has been written yet.**
`MenuCanvas` and `TooltipCanvas`'s dialog mode are already migrated; the case board is the next
target, sequenced last per the original rewrite plan because it was already the hardest surface in
the base mod.

---

## 0. Read this first

The six canvases people call "the case board" are **not a Unity parent/child tree** — each is an
independent root `Canvas`, tied together only by a shared `CanvasCategory` tag
(`CanvasCategoryInfo.cs:71-86`) and by scripted position-following
(`VRCamera.EnforceCaseCanvasPosition`, `CanvasPlacement.cs:430-461`'s primary/offset bookkeeping).
This matters because **v1 already tried treating one shared canvas (`GameCanvas`) as the composite
root for the whole case board and abandoned it** — `GameCanvas` did double duty as both the
case-board root and the separately-managed legacy HUD anchor, and the two systems fought over it
with zero coordination (`v1_findings.md` §4, "Projecting `GameCanvas` as one composite RT panel").
**Do not repeat that approach.** Convert each canvas independently, the same granularity already
proven for `MenuCanvas` and `TooltipCanvas`.

---

## 1. What "the case board" actually is

Four distinct pieces, not one panel:

1. **`ActionPanelCanvas`** — a narrow horizontal navbar (spans screen width, short vertically), not
   a full panel. Two jobs: an active-case selector (switches which case's corkboard `CaseCanvas`
   displays) and four tabs (Notebook, Minimap, Inventory, Upgrades). `CanvasCategory.Panel`
   (`CanvasCategoryInfo.cs:78`). Its own buttons are ordinary generic clicks — no bespoke click
   handling exists for them in `CaseBoardInteraction.cs`. It's excluded from the grip-drag
   candidate list (`CaseBoardInteraction.cs:414`, it's the anchor, not draggable) and is the anchor
   other case-board canvases' grip-drag offsets are stored relative to
   (`CaseBoardInteraction.cs:564-578`). `VRCamera.EnforceCaseCanvasPosition` locks `CaseCanvas`
   0.15m behind it every frame (`VRCamera.cs:1351-1367`).

2. **`CaseCanvas`** — the corkboard itself: evidence pins + string connections, all pure
   2D-plane content manipulated with the trigger like a mouse (confirmed: the string-drag preview
   is a stretched UI `Image` computed from 2D canvas-local vectors — `Mathf.Atan2` on a 2D delta,
   `CaseBoardInteraction.cs:1907-1925` — not a real 3D line; pin drag is plain
   `RectTransform.localPosition` math, `CaseBoardInteraction.cs:1247-1394`). `CanvasCategory.CaseBoard`
   (`CanvasCategoryInfo.cs:72`). Clicking a pin doesn't show inline detail — it opens a `Note`
   (see below). Heaviest legacy-only baggage of any case-board canvas: drives the game's own
   `CursorRigidbody` 2D-physics object and warps the OS mouse cursor directly
   (`CaseBoardInteraction.cs:967-1074`, `GetCanvasScreenPos` `2790-2841`) because the underlying
   pin hit-testing (`DragCasePanel`, `CursorRigidbody`) expects real `Input.mousePosition`, not
   `GraphicRaycaster`.

3. **`WindowCanvas`** — a generic nested-canvas host, `CanvasCategory.Menu`
   (`CanvasCategoryInfo.cs:65`, comment: "detail/notebook windows"). Hosts two kinds of content as
   genuinely nested Unity child canvases (confirmed, `CaseBoardInteraction.cs:336`, `CanvasClickRouter.cs:109`):
   - **Evidence `Note` windows** — opened by clicking a corkboard pin. **Multiple can be open at
     once** (confirmed: "click on as many as you want, they will open in space and be
     grip-draggable" — this is why `CaseBoardInteraction.cs:335-395`'s nested-note grip-drag
     pre-pass individually detects and drags one `Note` at a time rather than the whole
     `WindowCanvas`). Each has a **close button that must be wired up** when migrated — not yet
     verified against the click-dispatch path, flag as a required test case.
   - **Detective's Notebook** — opened via `ActionPanelCanvas`'s Notebook tab, "opens in 3d space
     like the notes" (same nested-canvas mechanism). Presumably singleton, unconfirmed.
   Because multiple `Note`s can coexist, **the RT design here can't be one persistent panel like
   `MenuRTPanel`/`TooltipRTPanel`** — it needs a dynamically-spawned RT panel per open window
   (own projector camera + quad, created on open, torn down on close), reusing
   `CameraRig.SetupRTPanelProjectorCamera`/`CreateRTPanelQuad` and `RTPanelPointer` per instance.
   This specific instancing shape hasn't been built anywhere in the codebase yet.

4. **`MinimapCanvas`** — opened via `ActionPanelCanvas`'s Minimap tab, or independently via a
   controller gesture ("B-button" body-locked mode, `CanvasPlacement.cs:382-412`,
   `CaseBoardInteraction.cs:543-561`). `CanvasCategory.Panel` (`CanvasCategoryInfo.cs:57`, comment:
   "was HUD — needs to be interactable"). Carries the most legacy-specific hacks of any case-board
   canvas (manual `mapCursorNode` driving to work around a broken native screen-projection call,
   `CaseBoardInteraction.cs:1102-1155`; hidden-overlay-button click skip, `2291-2335`;
   `ScrollRect`-based panning instead of the generic drag chain, `1787-1835`). **Deferred —
   explicitly deprioritized**, the user dislikes the base mod's map implementation vs. vanilla and
   would rather revisit it separately from the case-board rewrite.

`BioDisplayCanvas`/`LocationDetailsCanvas` (`CanvasCategory.CaseBoard`,
`CanvasCategoryInfo.cs:74-75`) and `UpgradesDisplayCanvas` (`CanvasCategory.Panel`,
`CanvasCategoryInfo.cs:86`, opened via `ActionPanelCanvas`'s Upgrades tab) have **zero
special-casing anywhere in `CaseBoardInteraction.cs`** — they're handled purely through the generic
category-based placement/click machinery. Low risk, but their exact trigger mechanism (what
specifically shows `BioDisplayCanvas`/`LocationDetailsCanvas` — probably clicking a person/location
pin, not confirmed) hasn't been verified.

---

## 2. Reported bugs — already root-caused, not just cosmetic

Three corkboard bugs (hover highlight inaccurate, click selecting the wrong evidence, string not
tracking until drag-release) all trace to the same class of legacy-WorldSpace artifact, not
incidental jank:

- **`GetPinVisualWorldPos`'s `PinFixedOffsetX` correction** (`CaseBoardInteraction.cs:2849-2858`)
  exists because `CustomScrollRect` moves `ContentContainer` between `Update()` and `LateUpdate()`,
  so a pin's `transform.position` read at Update-time is stale relative to what's actually
  rendered.
- **`PreAimScan`/`PostAimScan`'s world-position shims** (`CaseBoardInteraction.cs:696-800`,
  `809-851`) exist because the game writes `Note`/`ContextMenu(Clone)` positions as if they were
  screen-pixel coordinates into a `RectTransform` that's actually WorldSpace, so the whole canvas
  has to be shifted in world space to compensate before any aim-scan reads it.

Both are timing/coordinate-mismatch hacks specific to `CaseCanvas`/`WindowCanvas` being literal
WorldSpace canvases. An RT panel's `ScreenSpaceCamera` canvas (same trick already proven for
`MenuCanvas`/`TooltipCanvas`) should make the game's own screen-coordinate writes consistent again
and eliminate the need for these shims — meaning converting `CaseCanvas` is expected to fix these
three bugs as a side effect, not just deliver the post-FX benefit that motivated the rewrite.

---

## 3. Proposed migration order

No structural "parent" to start at (see §0) — sequenced by isolation and complexity instead:

1. **`ActionPanelCanvas`** — simplest, proves out grip-drag retargeted onto an RT quad's transform
   on the lowest-risk surface (it's the anchor, not itself draggable).
2. **`BioDisplayCanvas`, `LocationDetailsCanvas`, `UpgradesDisplayCanvas`** — zero special-casing,
   same template as `MenuRTPanel`, low risk.
3. **`WindowCanvas`'s nested windows (`Note` + Notebook)** — bigger lift: needs the new
   per-instance dynamic-RT-panel pattern (§1.3), plus verified close-button routing. Can ship
   *before* `CaseCanvas` itself converts, since the legacy corkboard can just be hooked to spawn an
   RT panel instead of a legacy `Note` on pin click — decoupled from the corkboard's own rewrite
   timeline.
4. **`CaseCanvas`** (the corkboard) — the actual pin-drag/string-link rewrite. `RTPanelPointer`
   needs trigger-drag support added (currently click/hover only). Expected to fix the §2 bugs as a
   side effect.
5. **`MinimapCanvas`** — last, deferred per explicit direction.

Per CLAUDE.md's audit rule: whichever canvas converts first, grep every other per-frame pass that
currently reaches into it (`CaseBoardInteraction`'s grip-drag pre-pass, `PreAimScan`/`PostAimScan`,
`CanvasPlacement`'s recentre-on-open, `CanvasConversionScanner`'s discovery loop) and gate it off
for what the RT panel now owns — the same shape as `_ctxTooltipRTPanelOwnsDialog`, not a new
failure mode.

---

## 4. Open questions — use the F9 canvas dump (below) to resolve, don't guess

- **Does `Inventory` have any VR-canvas presence at all?** No `InventoryCanvas` (or similar) name
  exists anywhere in the codebase. Today, opening inventory is only a synthetic `X` keypress
  (`LocomotionController.UpdateInventory`, `LocomotionController.cs:738-762`) simulating the
  desktop shortcut — unknown whether that renders anything VR-visible or falls back to a
  non-VR-converted screen overlay.
- **Is `ActionPanelCanvas`'s Notebook tab button the same action as the Right-B/gesture path**
  (`LocomotionController.UpdateNotebook`, `LocomotionController.cs:599-673`, also a synthetic
  `Tab` keypress), or a separate route into the same content?
- **What actually triggers `BioDisplayCanvas`/`LocationDetailsCanvas`?** No reference to either
  name exists in `CaseBoardInteraction.cs`.

---

## 5. New tool: F9 canvas dump

`SoDVR/VR/CanvasDump.cs`, wired to the F9 key in `VRCamera.cs` alongside the existing F8
(recentre)/F10 (VR settings)/End (text-graphic dump) diagnostics. `CanvasDump.DumpAll()` walks
every `Canvas` in the scene via `Resources.FindObjectsOfTypeAll<Canvas>()` — not just the ones
already in `_managedCanvases` — and logs, per canvas: name, active state, root-vs-nested,
`renderMode`, `CanvasCategoryInfo` category (`Default` if untagged — this is what would surface an
unfamiliar canvas like Inventory), `sizeDelta`, immediate parent name (shows nesting under
`WindowCanvas`/`TooltipCanvas`), whether it has its own `GraphicRaycaster`, its `worldCamera`, and
its active-graphics count.

**To resolve §4**: open the case board in-game, open the Notebook, open a couple of evidence Notes,
try Inventory, then press F9 and check `BepInEx/LogOutput.log` for the `[CanvasDump]` block. Every
open window/tab should show up with its real name and parent, resolving all three open questions
from one capture.
