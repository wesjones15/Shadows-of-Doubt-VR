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
