# SoDVR case-board findings

Scoping notes for migrating the detective case-board UI onto the RT-panel architecture
(`MenuRTPanel.cs`/`TooltipRTPanel.cs`/`RTPanelPointer.cs`/`CameraRig.cs`/`PostFXOverlayCompositor.cs`).
Written the same way `v1_findings.md` was — for whoever picks this up next, including a future
instance of the assistant that helped scope it — so the same investigation doesn't have to be
redone.

**`postfx_immune_ui.md` is the source of truth for how the RT-panel pipeline actually works
end-to-end** (projector camera → mipmapped RT → disabled-renderer quad → `PostFXOverlayCompositor`
drawing panels *and* the laser into each eye's RenderTexture with a `CommandBuffer` after
`Camera.Render()` returns, immune to HDRP post-processing entirely) — read it before writing any
case-board RT code. `v1_findings.md` is cited below only for still-valid historical/structural
lessons (why a composite-canvas approach was abandoned) that predate and aren't superseded by that
rewrite. `ARCHITECTURE.md` covers how the current canvas subsystem files fit together generally.

**Status as of 2026-09-25: migrated.** Every case-board canvas except `MinimapCanvas` is an RT
panel, and the legacy WorldSpace corkboard machinery is deleted. §0–§6 are the original scoping
notes (file/line references into the since-renamed `CaseBoardInteraction.cs` are historical);
**§7 records what the migration found and how each problem was solved.** `ARCHITECTURE.md` has the
current file map.

**Important correction to earlier case-board planning discussion:** an RT panel's laser was
previously believed to be unable to escape HDRP post-processing (it has to exist in real, moving
3D space, so it was drawn by the eye camera and inherited full bloom/exposure — the same bloom
problems chased on `MenuRTPanel`'s laser in an earlier session). **That's no longer true.**
`postfx_immune_ui.md` §4 documents the laser now being drawn by `PostFXOverlayCompositor` in the
same post-HDRP `CommandBuffer` pass as the panels — genuinely immune, no HDR color-tuning tradeoff
needed anymore, plain SDR cyan. Whatever corkboard-interaction laser/cursor work the `CaseCanvas`
migration needs should use this pattern from the start, not the older tuned-color approach.

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
   `GraphicRaycaster`. **Confirmed via the F9 dump (2026-09-24):** `CaseCanvas`'s `worldCamera` is
   literally the game's real `Main Camera`, not the mod's `LeftEye` — every other case-board canvas
   (`ActionPanelCanvas`, `BioDisplayCanvas`, `LocationDetailsCanvas`, `WindowCanvas`) reads
   `worldCam=LeftEye`. This is the concrete mechanism behind the `CursorRigidbody`/OS-cursor-warp
   workarounds above: the corkboard is deliberately kept on the real camera so the game's own
   `Input.mousePosition`-based click detection keeps working. An RT panel replacing `CaseCanvas`
   removes the need for this entirely (its `GraphicRaycaster` runs against the projector camera
   like every other RT panel), which is one more reason that legacy code is expected to disappear
   rather than need porting.

3. **`WindowCanvas`** — a generic nested-canvas host, `CanvasCategory.Menu`
   (`CanvasCategoryInfo.cs:65`, comment: "detail/notebook windows"). Hosts at least three kinds of
   content as genuinely nested Unity child canvases (confirmed via code, `CaseBoardInteraction.cs:336`,
   `CanvasClickRouter.cs:109`; **and now via the F9 dump, 2026-09-24** — all parented directly under
   `WindowCanvas`, `worldCam=LeftEye`, `category=Default` since none of these names are in
   `CanvasCategoryInfo`'s taxonomy):
   - **`Note`** (evidence windows) — opened by clicking a corkboard pin. **Multiple confirmed open
     simultaneously**: the F9 dump caught two active `Note` canvases at once, both `514x658`,
     36 active graphics each — matches "click on as many as you want, they will open in space and
     be grip-draggable." This is why `CaseBoardInteraction.cs:335-395`'s nested-note grip-drag
     pre-pass individually detects and drags one `Note` at a time rather than the whole
     `WindowCanvas`. Each has a **close button that must be wired up** when migrated — not yet
     verified against the click-dispatch path, flag as a required test case.
   - **Item-inspect windows, named per item** — confirmed by a third path into this same mechanism:
     selecting an item in the (still-unidentified, §4) Inventory panel and clicking Inspect *closes
     Inventory entirely* and opens the item as a `Note`-shaped window on the corkboard, named after
     the item itself (`'Katana'` in the capture that caught this — `parent='WindowCanvas'`,
     `514x658`, `39` active graphics, i.e. structurally identical to a `Note`). Whatever discovers
     `WindowCanvas`'s nested windows can't match on a fixed name list (`"Note"`, `"Detective's
     Notebook"`) — it needs to treat *any* newly-appeared direct child canvas of `WindowCanvas` as a
     window to manage, since the name is dynamic per item.
   - **`Detective's Notebook`** — opened via `ActionPanelCanvas`'s Notebook tab, "opens in 3d space
     like the notes" (same nested-canvas mechanism). F9 confirms it as a single `920x800` nested
     canvas with real content (311 active graphics), and reveals it has its **own internal
     pagination**: a `Page` object containing a further-nested `Scroll View` canvas (also its own
     `Canvas`/`GraphicRaycaster`, `worldCam=LeftEye`) — i.e. this is a *three-deep* nested-canvas
     structure (`WindowCanvas` → `Detective's Notebook` → `Page` → `Scroll View`), one level
     deeper than anything migrated so far. A dynamic RT panel for the Notebook will need to
     register `Scroll View` as an overlay canvas the same way `TooltipRTPanel` already does for
     `PopupMessage`/`TutorialMessage` (`RTPanelPointer.AddOverlayCanvas`), not just the Notebook's
     own root. Whether multiple `Page`/`Scroll View` instances exist (one per page) or it's reused
     across pages wasn't captured — worth a targeted F9 press while flipping notebook pages.
   Because multiple `Note`s can coexist, **the RT design here can't be one persistent panel like
   `MenuRTPanel`/`TooltipRTPanel`** — it needs a dynamically-spawned RT panel per open window (own
   projector camera + RT + disabled-renderer quad, created on open, torn down on close), reusing
   `CameraRig.SetupRTPanelProjectorCamera`/`CreateRTPanelTexture`/`CreateRTPanelQuad` and
   `RTPanelPointer` per instance, each registering itself with `PostFXOverlayCompositor` via its own
   `AppendOverlay` call (see `postfx_immune_ui.md`, "Adding another immune panel") so the
   coordinator can composite an arbitrary number of simultaneously-open windows, not just a fixed
   one or two. This specific instancing shape hasn't been built anywhere in the codebase yet.

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
category-based placement/click machinery. Low risk. `UpgradesDisplayCanvas` toggled
`active=False`/`True` cleanly (plain `SetActive`) across F9 captures as expected.

**`BioDisplayCanvas` is confirmed multi-purpose — it's also the Inventory panel** (§4): a real click
capture (`LogOutput.log:2528`, `[CaseBoard] CaseBoard target: 'Icon' on 'BioDisplayCanvas'`) shows
an Inventory item icon resolving as a hit on `BioDisplayCanvas` itself, not a separate canvas. Its
internal content evidently swaps between Bio info and the Inventory item grid depending on which
`ActionPanelCanvas` tab is selected, which is why it read `active=True` in every capture of the
session with a fluctuating `activeGraphics` count (0-2 normally, one capture at 118) — same object,
different internal content, never actually closed. `LocationDetailsCanvas`'s exact trigger (probably
a location pin) is still unconfirmed, but is expected to be equally simple.

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
   same template as `MenuRTPanel`, low risk. Covers Inventory for free — it's `BioDisplayCanvas`'s
   own internal content (§4), not a separate canvas.
3. **`WindowCanvas`'s nested windows (`Note` + item-inspect + Notebook)** — bigger lift: needs the new
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

## 4. Open questions — status after the 2026-09-24 F9 test sessions

Nine F9 captures total, across two rounds (six, then a follow-up three after a game/log restart),
covering different case-board and Inventory states. Findings:

- **Does `Inventory` have any VR-canvas presence at all? Resolved — it's `BioDisplayCanvas`.**
  Two wrong turns preceded this, worth recording so a future pass doesn't retrace them:
  1. First guess: "Inventory is just a readout inside `BioDisplayCanvas`" — based on a static
     `CanvasMaterialPatcher` child-list log (`InventoryText`/`CashText` among its children).
     Rejected too hastily: in-headset testing showed button 3 opens a real interactive WorldSpace
     panel with the legacy laser and clickable items, which didn't sound like "just a readout" —
     but the underlying canvas identification was actually right.
  2. Second guess: a `'Katana'` canvas caught in a later F9 capture looked like it might be the
     panel itself. Wrong — that's the *item-inspect* window (§1.3), opened only after Inventory has
     already closed.
  3. **Confirmed by a real click capture**, not another static scan: right before the
     `InspectButton` click in the log, `[CaseBoard] CaseBoard target: 'Icon' on 'BioDisplayCanvas'
     btn=Left` (`LogOutput.log:2528`) records the item icon you clicked as a hit on `BioDisplayCanvas`
     itself. So `BioDisplayCanvas` **is** the Inventory panel — a shared, multi-tab canvas whose
     internal content swaps between Bio info and the Inventory item grid depending on which
     `ActionPanelCanvas` tab is selected, rather than two separate canvases. That's exactly why it
     read `active=True` in every F9 capture with a fluctuating `activeGraphics` count: same object,
     different internal content each time, never actually closed. Migrating `BioDisplayCanvas`
     (already step 2 in §3) covers Inventory for free — no separate canvas or migration step needed.
- **Is `ActionPanelCanvas`'s Notebook tab button the same action as the Right-B/gesture path?
  Still open.** The dump confirms the canvas itself — `Detective's Notebook`, nested under
  `WindowCanvas` — but can't distinguish which input path opened it in a given capture. Not
  blocking: the migration only needs to know the canvas exists and what it looks like structurally
  (now confirmed, §1.3), not which of possibly two input paths triggers it.
- **What actually triggers `BioDisplayCanvas`/`LocationDetailsCanvas`? Bio resolved, LocationDetails
  still open** — see §6. Bio shows its content by activating child areas, not by CanvasGroup.
  LocationDetails couldn't be triggered during the capture session, so its visible state is still
  uncaptured.

---

## 5. Diagnostic tool: F9 canvas dump

`SoDVR/VR/CanvasDump.cs`, wired to the F9 key in `VRCamera.cs` alongside the existing F8
(recentre)/F10 (VR settings)/End (text-graphic dump) diagnostics. `CanvasDump.DumpAll()` walks
every `Canvas` in the scene via `Resources.FindObjectsOfTypeAll<Canvas>()` — not just the ones
already in `_managedCanvases` — and logs, per canvas: name, active state, root-vs-nested,
`renderMode`, `CanvasCategoryInfo` category (`Default` if untagged — this is what surfaced that
`Note`/`Detective's Notebook`/`Scroll View` aren't in the taxonomy at all), `sizeDelta`, immediate
parent name (shows nesting depth), whether it has its own `GraphicRaycaster`, its `worldCamera`,
and its active-graphics count.

A case-board section (added for the RT migration) also logs, per case-board canvas: the
`CanvasGroup` chain (canvas and every ancestor), `InterfaceController.desktopMode`, direct children
with their rect in canvas-local pixels and component types (TooltipCanvas to depth 4), and
`ContextMenuController`'s members. Results are in §6.

---

## 6. Capture session results (2026-09-24, CanvasGroup-aware F9)

Eight F9 captures in one log: (1) board closed in gameplay, (2) board open, no tab, (3) Notebook
tab, (4) Minimap tab, (5) Inventory tab, (6) Upgrades tab, (7) one note open, (8) two notes open.
Not captured: a LocationDetails display, a hover tooltip, the pin quick-menu, a pin context menu.
The hover states couldn't be produced because **the legacy pointer never delivers hover to the
game** (it only moves aim dots), and the legacy A-on-pin context menu didn't open either. These get
verified once the RT pointer sends real pointer enter/exit/right-click events.

**Case-board open signal: `ActionPanelCanvas.activeInHierarchy`.** `False` in (1), `True` in
(2)-(8); `CaseCanvas` tracks it identically. The old `CanvasPlacement` comment claiming
ActionPanelCanvas "is always active during gameplay" is wrong. `InterfaceController.desktopMode`
agrees in all eight captures, but popups and tutorials also set it via `PauseGame` (see
`HudController`), so it's less specific and isn't used as the board signal.

**How each canvas shows and hides** (only signals seen changing between captures):

| Canvas | Hidden | Shown |
|---|---|---|
| ActionPanelCanvas | `activeInHierarchy=False` | active; content strip `TopPanel`+`CaseList`, rect (-928,408,1856,100) = `ControllerSelection` |
| CaseCanvas | inactive with the board | active, `CorkBoard` fills the canvas; its CanvasGroup goes `interactable=False blocksRaycasts=False` (alpha stays 1) while the Inventory or Upgrades tab is up |
| BioDisplayCanvas (= Inventory) | always active, no CanvasGroup, children inactive | Inventory tab activates `InventoryDisplayArea` (-610,-395,1220,790) and `SocialCreditArea` (622,-440,300,828) |
| UpgradesDisplayCanvas | inactive; CanvasGroup alpha drops to 0 after its first close | active, alpha 1; content `UpgradeConnections` (-904,-450,1808,860) |
| WindowCanvas | never inactive; `Canvas.enabled=False` + non-interactive CanvasGroup while the Inventory/Upgrades tab is up | windows are direct children carrying an `InfoWindow` component |
| MinimapCanvas (stays legacy) | CanvasGroup alpha 0 | active, alpha 1 |
| LocationDetailsCanvas | always active; `LocationDetails` child (100x100, 2 graphics) | not captured |

Child rects read `(0,0,0,0)` while the board is closed — layout is only valid once it's open, so RT
views must read rects live while visible, never cache them from the closed state.

**Windows are placed off-canvas and on top of each other.** In (8): `702 Etheridge Heights` at
(457,-961), below the canvas's bottom edge, and `Address Book` and `1201 Etheridge Heights` both at
exactly (960,-118), past the right edge and fully overlapping. Rendering WindowCanvas as-is would clip
or merge them, so the RT migration lays each window out into its own slot in canvas space. Window
names are dynamic (`Detective's Notebook`, `Address Book`, addresses, item names); `InfoWindow` is
the reliable identifier. Sizes seen: Notebook 920x800, notes/address windows 514x658 and 452x510.

**TooltipCanvas** (rect 2560x1440): `ContextMenus` (nested Canvas + GraphicRaycaster), `Tooltips`
(plain container), `PopupVignette`, `PopupMessage` (268x128), `TutorialMessage` (268x187),
`CutSceneImage`. Tooltip and quick-menu element names are still unknown (see above).

**`ContextMenuController` API** (on every pin, 40+ instances with the board open): methods
`OpenMenu`, `ForceClose`, `SetScreenPosition`, `OnPointerClick`, `OnCommand`; fields `useLeftButton`,
`pos`, `useCursorPos`, `cursorPosOffset`, `menuButtons`, `activeMenu`, `spawnedMenu`, `lastButton`.
`ForceClose` is the dismiss path when a trigger press lands elsewhere; `OnPointerClick` suggests the
menu opens natively from a right-button pointer click once the RT pointer sends one.

**Correction to §0 — the case-board canvases are NOT independent roots in the game.** They start
out as children of `GameCanvas` (startup log: `Canvas 'CaseCanvas' ... parent='GameCanvas'`, same
for WindowCanvas, BioDisplayCanvas, LocationDetailsCanvas, UpgradesDisplayCanvas, MinimapCanvas).
The F9 dumps showed them at the scene root only because the legacy reparent pass had already moved
them there. Worse, which ones get moved is timing-dependent: the scanner classifies a canvas once,
on its first scan, and `ActionPanelCanvas` (inactive at that moment) was classified as *nested*,
so it stayed under GameCanvas as WorldSpace HUD content and never got reparented — which is why
the first navbar RT build z-fought like a legacy panel. Consequences for every migration step:
the RT panel must find its canvas by name wherever it lives and detach it itself
(`RTCanvasPanel.Attach` does this), and the canvas must be in `RTOwnedCanvases` so the scanner's
nested walk and the material patcher never touch it, even before the panel attaches.

---

## 7. What the migration found (2026-09-25)

Each item: symptom → verified cause → fix. Commit hashes point at the change.

**Clicks must arrive the way a mouse click does (`bd21454`).** The base mod's `InvokeButtonClick`
fires only a Button's persistent `onClick` listeners and, when there are any, sends no pointer
click. The game's own `ButtonController.OnPointerClick` then never runs, and that is what calls
`OnLeftClick` (overridden by `FactButtonController`, `ContextButtonController`, ...) and raises
`OnPress`. The open-note eyeball (`FactButtonController.ToggleHidden`, subscribed to
`toggleHiddenButton.OnPress`) did nothing while zero-listener buttons like the Connections tab
worked. The base mod skipped the game's path to avoid double fire because it also sent real OS
clicks; RT panels send none. `RTPanelPointer` now sends down/up/click exactly like Unity's input
module, with no `submit` (which ran `Button.Press` a second time) and no listener-state changes
(the old code switched persistent listeners Off after invoking them). The game's `mouseInputMode`
guard accepts these events. The legacy router still has the old behaviour (`pending_tasks.md` §7).

**Projector precision (`3b292cf`).** Projectors parked at y=-10000 quantised canvas positions to
about 2 px: the corkboard's vertical pan moved in steps, the 24 px note minus button failed its hit
test, and open-note scroll content flipped a pixel every few frames. At -500 m it's gone. Note
slots are also placed on whole units (`2f3a21b`), since a half-pixel window position flips too.

**Cross-canvas position copies, and why projectors aren't shared.** In the flat game every canvas
is one screen, and the game copies world positions between them: the pin quick-menu takes its
pin's position (a CaseCanvas point), so on TooltipCanvas it landed about 93,000 units off the
texture. Giving the screen-sized projectors one shared pose fixed that (`6fedc16`), but after a
save load the pause menu and case board then took a long time to open (A/B confirmed by the user;
mechanism never found), so it was reverted (`e821ed0`). Instead, an element drawn off the
TooltipCanvas texture is mapped through whichever RT projector sees it on its canvas plane to the
same screen point on TooltipCanvas (`RTCanvasPanel.TryMapFromOtherScreen`, `4ee3141`). Context
menus opened by A are positioned for the OS cursor instead (logged at pixel x=-426, on no other
panel's screen), so anything still off the texture is slid onto it (`4902946`). The views are
placed at the laser regardless; only the canvas position is corrected.

**Zero z-scale breaks text under a perspective projector (`22990c7`).** The game scales the pin
context menu `(1, 1, 0)`. On its overlay canvas that's inert, but our projectors are perspective
cameras, and TextMeshPro's SDF shader then corrects each glyph by its surface normal, which a zero
z-scale collapses: glyphs drew as solid blocks. Tooltip-canvas elements get z put back to 1 each
frame.

**Compositor order (`74c0f85`).** Panels are composited farthest-first by quad centre. A tooltip 5 cm
in front of the navbar's edge can still be farther from the eye than the navbar's centre, so the
navbar was drawn over it. TooltipCanvas views are now always drawn last, as the flat game's topmost
canvas.

**Draw order: legacy vs native.** `CanvasMaterialPatcher` forced render queues by graphic type
(background 3000, additive 3001, everything else 3008, text 3009), so in the legacy pipeline every
button drew above every background regardless of hierarchy. RT panels use the game's native draw
order, so anything the game layers underneath stays underneath. This explains differences from
legacy when comparing the two.

**Corkboard panning.** The board's `CustomScrollRect` doesn't move from drag events, so the trigger
pan drives `ContentContainer.anchoredPosition` directly (`75131fc`) and keeps the cork covering the
viewport, or centred when zoomed out (`a263adc`). The corkboard view shows the whole texture while
the board is open (`ded64cb`): the usual "enough visible graphics" rule hid it when panned onto bare
cork, with no way back.

**Visibility must count renderer alpha (`cea9827`).** The inventory fades out through each element's
`CanvasRenderer.SetAlpha`, leaving colour and CanvasGroup alpha at 1, so its views showed black.

**Custom links follow the laser (`8a2fbb4`).** The quick-menu's new link starts the game's
`CasePanelController.CustomStringLink` coroutine, whose preview follows the OS mouse. The click is
taken over and runs `CorkboardInput`'s own link instead, like the B-hold link.

**Small targets.** A note's 24 px minus button gets a hit area as wide as the other buttons in its
column (`raycastPadding`, `c1947cd`). Its hover corners match it through
`ButtonController.additionalHighlightRectModifier`, which takes (left, bottom, right, top) with the
same sign as `raycastPadding` (`31ea5ed`).

**Not causes.** Game-window OS focus had nothing to do with the post-load menu delay; the focus
change was reverted (`51e69f3`).

**Decided against: laying canvases out at 1920x1080.** The case-board canvases are laid out as a
2560x1440 screen at scale 1, so edge-anchored parts spread wider than in a 1080p layout (the XP-bar
gap). Separate inventory/XP views fixed that (`18c04ec`). A 1080 layout would only make the UI
1.33x bigger, not sharper — the texture already has about twice the pixels the headset shows at
board distance. If text legibility ever matters, enlarge that panel's world size instead.
