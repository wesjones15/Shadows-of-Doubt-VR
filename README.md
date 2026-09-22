# SoDVR — VR Mod for Shadows of Doubt

Full 6DOF VR support for **Shadows of Doubt** using any OpenXR runtime.
Tested with Virtual Desktop (VDXR) on a Samsung Galaxy XR headset.

## Supported runtimes

Any OpenXR runtime works — bindings are included for:
- Oculus / Meta Quest (via Virtual Desktop or Air Link)
- Valve Index
- HTC Vive
- Windows Mixed Reality
- KHR Simple Controller (generic fallback)

## Requirements

- **Shadows of Doubt** (Steam)
- **BepInEx 6 IL2CPP** — [download from the BepInEx GitHub releases](https://github.com/BepInEx/BepInEx/releases) (pick the `BepInEx_Unity.IL2CPP_win_x64_*` build)
- An OpenXR-compatible VR headset and runtime

## Installation

1. Install BepInEx 6 IL2CPP into the game folder (`Shadows of Doubt/`).
2. Run the game once with BepInEx installed so it generates its interop assemblies, then close it.
3. Download the latest `SoDVR-x.x.x.zip` from the [Releases](https://github.com/Blah64/Shadows-of-Doubt-VR/releases) page.
4. Extract the ZIP — it contains a `BepInEx/` folder. Copy it into your `Shadows of Doubt/` game folder, merging with the existing `BepInEx/` folder.

   The final layout should look like:
   ```
   Shadows of Doubt/
     BepInEx/
       plugins/
         SoDVR.dll
       patchers/
         SoDVR/
           SoDVR.Preload.dll
           RuntimeDeps/
             Native/
               openxr_loader.dll
   ```

5. Start your VR runtime / headset software **before** launching the game.
6. Launch Shadows of Doubt through Steam. The game will open on your desktop; put on your headset — press a button or click your mouse to get past the first screen, then the VR view should appear automatically.

## Controls

### Right controller

| Input | Action |
|-------|--------|
| Trigger | UI click (point at a menu and pull) |
| A button | Jump, or **right-click** when aiming at a canvas |
| B button | **Hold** to show notebook / map (Tab held while B held) |
| B button (controller behind shoulder) | Open inventory / backpack |
| B button (drag while aiming at canvas) | Middle-click drag (pan map, scroll) |
| Grip | Drag floating panels in 6DOF (notes, map, location details, bio) |
| Thumbstick left/right | Snap turn (or smooth — configurable in VR Settings) |
| Thumbstick up/down | Scroll (when VR Settings panel is open) |
| Thumbstick click | Toggle flashlight |

### Left controller

| Input | Action |
|-------|--------|
| Menu button | Pause menu (ESC) |
| Y button | Alternate use / Use item (F) |
| X button | Crouch toggle (C) |
| Trigger | World interact — doors, objects, NPCs (aims from left controller) |
| Grip | Right mouse button (pick up evidence, secondary interact) |
| Thumbstick | Move player (head-relative direction) |
| Thumbstick click | Sprint toggle (auto-stops when stick returns to centre) |

### Notes

- **Sprint** holds Shift while active. Stops automatically when you release the left stick.
- **B button map**: hold B to keep the map/notebook open; release B to close.
  Right stick B-drag pans the map; trigger click opens evidence notes for locations.
- **Backpack gesture**: with your right controller held behind your right shoulder
  (like grabbing from a backpack), press B to open inventory.
- **A button right-click** opens context menus on the case board and map.
- **Right grip drag** repositions floating canvases in 6DOF. Positions are saved and
  restored when the case board is reopened.

## VR Settings panel

Open with **F10** or the in-game Settings button. Five tabs:

- **Graphics** — render quality, resolution scale
- **Audio** — master volume, per-channel VCA sliders (soundtrack, ambience, SFX, etc.)
- **Controls** — game keybind reference
- **General** — game general settings
- **VR** — VR-specific options:
  - **Turn Mode**: Snap (discrete angles) or Smooth (proportional to stick)
  - **Snap Angle**: 15°, 22.5°, 30°, 45°, 60°, 90° (default 30°)
  - **Smooth Speed**: 60–240°/s (default 120°/s)
  - **Move Speed**: 2–8 m/s (default 4.0 m/s)
  - **Sprint Multiplier**: 1.4×–3.0× (default 1.8×)
  - **Left Laser** toggle
  - **Item Hand**: which hand holds picked-up items

Right stick controls scrolling while the VR Settings panel is open.

## License

MIT — see [LICENSE](LICENSE).
`RuntimeDeps/Native/openxr_loader.dll` is the Khronos OpenXR Loader (Apache 2.0).
