# Pending tasks

Observations from testing the void room (see `SoDVR/VR/Rooms/VoidRoomController.cs`,
`SoDVR/VR/Rooms/VoidRoom.cs`) that weren't fixed on the spot — noted here so they aren't lost.

## 1. Void room terminates a hair before player movement is enabled

After loading a save, the void room disappears slightly before the player can actually move.
`VoidRoomController.Tick()` releases the room the moment `SessionData.Instance.startedGame`
flips true (via `TryGetMainMenuActive`), but that's not the same moment locomotion actually
arms — `VRCamera` has its own separate discovery/grace-period state
(`_movementDiscoveryDone`, `_playerCC` discovery, `_sceneLoadGrace`) that appears to settle
slightly later. The result is a brief window where the real world is visible but the player
still can't move — the room comes down before control is actually live.

Needs investigation into exactly which signal marks "player can move" and whether releasing
the room should instead be gated on that (or on both, whichever is later) rather than on
`startedGame` alone.

## 2. Grimy/aliased "visual snow" filter visible across the void room

A visual-noise/grain effect is visible over the void room, most apparent when transitioning
from the press-any-key void into the main-menu void (i.e. it's present on both, so it's not
specific to one screen). This survives `PostProcessingOverride.ForceDisableDepthOfField`
(`SoDVR/VR/PostProcessingOverride.cs`), so it isn't DepthOfField — likely film grain or
dithering, still being applied via the HDRP volume/camera settings even with DoF forced off.

Needs identifying which HDRP component/camera setting is responsible (candidates: film grain,
dithering — see `HDAdditionalCameraData.dithering`, `FrameSettingsField.Dithering` already
referenced elsewhere in `VRCamera.cs`) and either disabling it the same way DoF was, or via the
existing `EyeRenderState`/`TakeOverCameras` neutral-environment path in `VoidRoom.cs`.

## 3. BioDisplayCanvas (Inventory) RT panel still misbehaves — deferred

Reported after the case-board RT migration (2026-09-24): the Inventory panel works for item
selection and Inspect, but has remaining issues, including its close button not showing. It can be
closed from the navbar, so this was deferred rather than blocking the migration.

What's already ruled out: content clipped at the texture edge. After `9f3b715` (screen-sized
texture, game's own ConstantPixelSize scaler left enabled) the `[CaseBoardPanel] ... reach past the
texture edge` diagnostic logged nothing for BioDisplayCanvas, so the close button isn't being cut off
— the cause is elsewhere (candidates: the button is hidden by the game's controller/mouse-mode
state, lives on another canvas, or is excluded by `CaseBoardPanel`'s content-rect/visibility
rules). Start with an F9 capture with Inventory open and a careful look at what the flat game shows.

## 4. Minimap — revisit to match vanilla — deferred

MinimapCanvas is still on the legacy WorldSpace pipeline and behaves like the base mod's map. After
the rest of the case-board migration, rework it to behave more like the vanilla game's map rather
than porting the legacy map hacks (`CaseBoardInteraction`'s manual `mapCursorNode` driving, hidden
overlay-button skip, ScrollRect-based panning) onto an RT panel as-is.

Update (2026-09-25): the panel is now centred on the screen centre (inventory in the middle, XP bar
on the right). The close button is still under investigation: legacy showed a working X in the
case-board inventory, and its state (active, alphas, masks, owning canvas, rect) is now logged
automatically about a second after the inventory opens (`[CanvasDump] closeButton ...`).

## 5. Inventory status cards — deferred to a HUD refactor

The status cards shown to the left of the flat game's inventory (Bruised, Wet, Cold, ...) are not
inventory content: they're the HUD status display (`StatusController`), visible behind the pause
inventory. The mod hides the HUD while the case board is open, so they don't appear beside the
case-board inventory. Bring them in (or show the HUD status alongside the board) as part of a HUD
refactor; the centred Bio panel leaves room on the left for them.

## 6. Black, frozen headset after a Virtual Desktop menu round trip — not reproduced

Reported 2026-09-25: after using the Virtual Desktop shortcut to go from the game to the VD menu and
back, the headset stayed black and frozen. The session log from that run shows the OpenXR session
leaving focus (state 5 → 4) when the VD menu opened; the return wasn't captured. Could not be
reproduced afterwards. If it recurs, copy `BepInEx/LogOutput.log` and Player.log before relaunching
and check how `VRCamera`/`OpenXRManager` handle the VISIBLE → FOCUSED transition and whether frame
submission resumes.

## 7. Pin quick-menu never shows — step 6's fix reverted

The game puts the pin quick-menu (and right-click context menus) at a world position copied from
another canvas, which in the flat game is the same screen. Each RT canvas has its own projector at
its own spot, so the copy lands tens of metres off the TooltipCanvas texture (logged x ≈ 93,228)
and the menu is never visible. Sharing one projector pose between the screen-sized panels fixed
that (`6fedc16`), but after loading a save the pause menu and case board then took a long time to
open; reverting it (`e821ed0`) made them instant again, confirmed by the user. The mechanism behind
that delay is unknown. Fix the quick-menu another way — e.g. move a tracked TooltipCanvas element
that lands off its texture back inside it (its view is placed at the laser anyway, so only its
canvas position needs correcting).
