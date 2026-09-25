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
