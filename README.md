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

### Main hand

One hand points: its laser aims at menus and its dot at the world, its **trigger** interacts or
clicks, its **grip** is the secondary interact (right mouse), and it holds your item. Pull the
**other hand's trigger** to swap — that press only swaps, it doesn't click. The choice is saved
(`Controls.MainHandRight`, or "Main Hand: Right" on the VR tab).

### In the world

| Input | Action |
|-------|--------|
| Main trigger / main grip | Interact / secondary interact with what the dot is on |
| Other hand's trigger | Swap main hand |
| Left stick | Move (head-relative) |
| Left stick click | Sprint toggle (stops when the stick returns to centre) |
| Right stick left/right | Turn (snap or smooth — VR tab) |
| Right stick click | Flashlight |
| A | Jump |
| X | Crouch |
| B (hold) | Map, while held |
| B with the right controller behind your right shoulder | Inventory |
| Y (tap) | Case board |
| Y (hold) | Radial menu — Inventory, Upgrades, Notebook, Map; aim with the left controller, release on one |
| Menu button | Pause |

### Menus, case board and windows

| Input | Action |
|-------|--------|
| Main trigger | Click; drag to move pins, pan the board or map |
| A | Context menu (pins, strings, the map) |
| B (hold on a pin, release on another) | Link them with a string |
| B | End a conversation |
| Right stick up/down | Zoom the case board or map at the laser; scroll elsewhere |
| Main grip (hold) | Move a window in 6DOF; the stick of that hand pushes / pulls it |
| Main grip on the board's top bar | Move the case board |
| Y (tap) | Close the case board |

Windows remember where you put them ("Window Positions: Reset" on the VR tab).

### On-screen hints

The game's key hints show Quest buttons instead of keys, and the trigger and grip glyphs follow
the main hand. Under them, with the case board open, a **VR controls** list shows what the laser's
target takes (a pin, a string, the map, a window to grab) and the mod's own controls, on a Menus
and a World page. Its **Show in world** button (`HUD.ControlsInWorld`, or "Controls in World" on
the VR tab) keeps the World page up while walking.

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
The Quest button glyphs (`SoDVR/Assets/QuestGlyphs`) are from Kenney's Input Prompts (www.kenney.nl, CC0).
