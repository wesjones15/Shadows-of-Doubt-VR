# Pending tasks

Observations from testing (the void room, the case board, the legacy canvases) that weren't fixed on
the spot — noted here so they aren't lost.

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

## 3. BioDisplayCanvas (Inventory) close X — unconfirmed

Reported after the case-board RT migration (2026-09-24): the inventory's close X didn't show. The
inventory has since become two views, `InventoryDisplayArea` and `SocialCreditArea`, placed side by
side at the vanilla gap (`18c04ec`), and it closes instantly (`0f38248`); both confirmed in the
headset. Whether the X now shows and works wasn't re-checked, and the X-specific diagnostics were
removed in cleanup. If it's still missing, start from an F9 capture with the inventory open;
content clipped at the texture edge is already ruled out.

## 4. Minimap — revisit to match vanilla — deferred

MinimapCanvas is still on the legacy WorldSpace pipeline and behaves like the base mod's map. After
the rest of the case-board migration, rework it to behave more like the vanilla game's map rather
than porting the legacy map hacks (`LegacyCanvasInteraction`'s manual `mapCursorNode` driving, hidden
overlay-button skip, ScrollRect-based panning) onto an RT panel as-is.

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

## 7. Legacy canvases still click the base mod's way

`CanvasClickRouter.TryClick`, used for every canvas still on the legacy pipeline (dialogue,
computers, keyboard, fingerprints, ...; not the minimap's map nodes, which have their own path),
fires Buttons through `InvokeButtonClick`: persistent `onClick` listeners only, then switches them
Off. On RT panels that broke every button whose action is an `OnLeftClick` override or an `OnPress`
subscriber (the open-note eyeball), fixed there by sending the click the way a mouse does
(`bd21454`, `caseboard_findings.md` §7). The same buttons on legacy canvases are presumably dead
too — untested. When one of those canvases is next worked on (or moved to an RT panel), send it
vanilla clicks and test that surface's buttons.
