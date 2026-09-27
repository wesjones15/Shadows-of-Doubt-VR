# SoDVR — VR Mod for Shadows of Doubt

Full 6DOF VR for **Shadows of Doubt** over OpenXR: head-tracked view, motion controllers, and the
game's whole UI (menus, case board, notes, map, dialogue, HUD) on panels you can point at and grab.

This is a heavily reworked continuation of [Blah64's SoDVR](https://github.com/Blah64/Shadows-of-Doubt-VR)
(v1.0.0). Thanks to Blah64 for the original mod: the OpenXR bootstrap and much of the groundwork are
theirs. See [How this version differs](#how-this-version-differs-from-blah64s) below.

## Tested on

- **Headset:** Meta Quest 3, wireless over **Virtual Desktop**, using Virtual Desktop's **OpenXR**
  runtime (VDXR)
- **PC:** NVIDIA RTX 3080 Ti, AMD Ryzen 7 7800X3D, 32 GB RAM, Windows 10
- **Game:** Shadows of Doubt, current Steam build (Unity 2021.3.45f2)
- **Mod loader:** BepInEx 6.0.0-be.788 (IL2CPP)

The mod also includes controller bindings for Valve Index, HTC Vive, Windows Mixed Reality and the
generic KHR simple controller, but **only the Quest 3 over Virtual Desktop setup has been tested**.
Vive and WMR controllers have no A/B/X/Y buttons, so several actions are unavailable on them.

## Requirements

- **Shadows of Doubt** on Steam (Windows)
- **BepInEx 6 bleeding-edge build, IL2CPP x64**. The tested build is **be.788**:
  `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788+5b766a3.zip` from
  [builds.bepinex.dev](https://builds.bepinex.dev/projects/bepinex_be).
  The older "pre-release" builds on BepInEx's GitHub releases page are not what this was tested with.
- **An OpenXR runtime and headset.** The tested path is Virtual Desktop with its OpenXR runtime (VDXR).
- **No .NET install is needed to play.** BepInEx's IL2CPP build ships its own .NET 6 runtime.
  (.NET is only needed to [build from source](#building-from-source).)

## Installation

1. **Install BepInEx.** Extract the BepInEx zip into the game folder (the one containing
   `Shadows of Doubt.exe`, usually `C:\Program Files (x86)\Steam\steamapps\common\Shadows of Doubt`).
2. **Run the game once without the mod** and wait for the main menu. The first launch with BepInEx
   takes several minutes while it generates its interop assemblies. Then quit.
3. **Install SoDVR.** Download `SoDVR-2.0.0.zip` from the
   [Releases](https://github.com/wesjones15/Shadows-of-Doubt-VR/releases) page and extract it into
   the same game folder, merging its `BepInEx` folder with the existing one. You should end up with:
   ```
   Shadows of Doubt/
     BepInEx/
       config/
         com.sodvr.mod.cfg
       plugins/
         SoDVR.dll
       patchers/
         SoDVR/
           SoDVR.Preload.dll
           RuntimeDeps/
             Native/
               openxr_loader.dll
   ```
4. **Start your headset's PC software first.** With Virtual Desktop: connect from the headset so
   you're looking at your desktop, with its OpenXR runtime (VDXR) selected.
5. **Launch Shadows of Doubt** from Steam or the desktop. The game opens on the monitor, which
   mirrors the headset's left eye; the headset view starts automatically.
6. On the "press any key" screen, **pull any trigger or grip**.

**Upgrading, including from Blah64's version:** overwrite every file, config included. The config
that ships with each release holds that release's defaults, and BepInEx keeps old values in an
existing config instead of replacing them. If you had changed settings, set them again (F10) after
upgrading.

**Uninstalling:** delete the four files above. To remove BepInEx too, delete the `BepInEx` folder,
`winhttp.dll`, `doorstop_config.ini`, `.doorstop_version` and the `dotnet` folder from the game folder.

## Controls

Button names are for Quest (Touch) controllers. The game's own on-screen key hints are replaced
with Quest button glyphs.

### Main hand and the trigger swap

One hand is the **main hand** (the right, by default). It does all the pointing:
- its **laser** aims at menus and windows, and its **dot** aims at the world
- its **trigger** clicks, interacts and uses your item (left mouse)
- its **grip** does the secondary interact (right mouse) and grabs windows
- it **holds your item**, with the arm following it

**Pulling the other hand's trigger swaps the main hand.** That trigger pull only swaps; it never
clicks or interacts. A swap can't happen while the main hand is mid-click or mid-drag. The choice is
saved between sessions (`MainHandRight` in the config, or "Main Hand: Right" on the VR tab).

With the **left** hand as main hand:
- the left trigger and left grip take over clicking, interacting and grabbing windows, and the
  laser, dot and item move to the left hand
- the **right trigger does nothing except swap back**
- a window you're grip-dragging is pushed and pulled with the **left** stick, so you can't walk
  while dragging one (with the right hand as main, it's the right stick, so you can't turn or scroll
  while dragging)
- **everything else stays where it is:** A, B, X, Y, both sticks and the menu button do the same
  jobs as with the right hand as main. A and B still act on whatever the laser points at, the right
  stick still turns, scrolls and zooms, the Y radial menu is always aimed with the left controller,
  and the backpack gesture always uses the right controller.

In the tables, **main trigger** and **main grip** mean the main hand's trigger and grip.

### In the world

| Input | Action |
|---|---|
| Left stick | Move, relative to where your head faces (in air vents: 3D, toward where you look) |
| Left stick click | Sprint toggle; stops when you let the stick return to centre (swapped if the game's "Always Run" is on) |
| Right stick left/right | Turn: 30° snaps by default, or smooth (VR tab). Off while the laser is on a panel |
| Right stick click | Flashlight |
| Main trigger | Interact / use item on what the dot is on. Also clicks on an in-game computer's screen |
| Main grip | Secondary interact |
| Other hand's trigger | Swap main hand |
| A | Jump |
| B (hold) | Show the map in front of you while held |
| B, with the right controller behind your right shoulder | Open the inventory ("backpack" gesture) |
| X | Crouch toggle |
| Y (tap) | Open / close the case board (also closes a screen opened from the radial menu) |
| Y (hold) | Radial menu: aim the **left** controller at Inventory, Upgrades, Notebook or Map and release to open it on its own. Release in the centre to cancel |
| Menu button (left controller) | Pause menu |

### Menus, windows and the case board

These apply while the laser is on a panel.

| Input | Action |
|---|---|
| Main trigger | Click; hold and move to drag |
| Right stick up/down | Scroll. On the case board or map: zoom around the laser |
| A | Right-click / context menu (pins, strings, the map) |
| Main grip (hold) | Grab the window and move it in 3D; that hand's stick pushes it away / pulls it closer |
| Y (tap) | Close the case board |
| **Case board:** main trigger on a pin | Open its note; drag to move the pin |
| **Case board:** main trigger drag on empty cork | Pan the board |
| **Case board:** hold B on a pin, release on another | Connect them with string (B again cancels a link started from a pin's menu) |
| **Case board:** main grip on the top bar | Move the whole case board |
| **Map:** main trigger drag / double-click | Pan / open an address |
| **Map:** A | Map context menu at the pointed spot (route, etc.) |
| **Dialogue:** point at an option | Select it |
| **Dialogue:** main trigger | Say the selected option |
| **Dialogue:** right stick up/down | Step through the options |
| **Dialogue:** B | End the conversation |
| **Text box:** main trigger | Opens the VR keyboard |

Windows remember where you put them; "Window Positions: Reset" on the VR tab puts them all back.
While the pause menu or case board is open you can still walk, but only within 2 m of where you
opened it. On the pre-game screens and the main menu you can't walk or turn.

**In-headset help:** with the case board open, a **VR controls** list sits under the game's key
hints, with Menus and World pages, plus live rows showing what the laser's current target accepts.
Its "Show in world" switch (`ControlsInWorld`, "Controls in World" on the VR tab) keeps the World
page up while you walk around.

### Keyboard

| Key | Action |
|---|---|
| F10 | Open / close VR Settings |
| F8 | Re-centre the case board in front of you |
| F9 | Debug: dump every canvas to the log |

## VR Settings

Open with **F10**, or the **Settings** button in the pause or main menu. Changes wait until you
press **Apply**; **Reset Defaults** resets the current tab.

- **Graphics, Audio, Controls, General**: the game's own settings.
- **VR**: the mod's settings:
  - Turning: Smooth Turn, Snap Angle, Smooth Speed
  - Movement: Move Speed, Sprint Multi, Jump Speed, Gravity, Fall Knockdown (turn off to take fall
    damage without the view dropping to the floor)
  - Controls: Main Hand: Right, World Laser (draws a beam from the main hand into the world)
  - Windows: Menu Distance, Window Positions: Reset
  - HUD: HUD Distance, HUD Size, HUD Height, Controls in World
  - Rendering: Monitor Mirror, Smooth Loading, Crisp Panels, Scale (render resolution; applies
    after a restart)

The same settings live in `BepInEx/config/com.sodvr.mod.cfg`, each with a description.

## How this version differs from Blah64's

This version is a rewrite of most of the mod, built on Blah64's OpenXR foundation:

- **UI rebuilt from scratch.** Every game canvas (menus, popups, tooltips, case board, notes, map,
  dialogue, HUD) is rendered to its own texture and drawn after the game's post-processing, so
  panels stay sharp and depth of field never blurs them. Menus, popups and tooltips go to the
  headset as their own compositor layers for crisper text. Blah64's world-space canvas system is gone.
- **Grab any window** with the grip, move it in 3D and push or pull it with the stick. Positions
  are remembered. The whole case board moves by its top bar.
- **One main hand, swappable with the other trigger.** Your item and arm follow the main hand, and
  the game's interaction aims where the controller points, not where your head looks.
- **New ways to get around the UI:** a Y radial menu for the inventory, upgrades, notebook and map;
  point-and-trigger dialogue; stick zoom on the case board and map; a VR keyboard for text boxes;
  Quest button glyphs in the game's key hints; an in-headset controls list.
- **HUD** on a transparent sheet that follows your head and makes room for the case board or a
  computer screen. The awareness compass sits at your feet, and objective markers, NPC reactions
  and speech bubbles appear at their targets in the world.
- **Comfort and stability:** a simple room replaces blank frames on the pre-game screens and main
  menu, loading freezes no longer stutter the headset, the monitor mirrors the headset, and
  render scale is adjustable.
- **Movement:** fall damage and knockdown work (optional knockdown), you can jump from a
  standstill, and air vents allow 3D movement.
- **VR Settings** rebuilt on the game's own settings, with Apply / Reset Defaults and a VR tab for
  all of the mod's options.
- **Code:** the original 9,000-line `VRCamera.cs` is split into ~70 files by subsystem. See
  [ARCHITECTURE.md](ARCHITECTURE.md); design notes and findings are in [docs/](docs/).

## Known issues

- Using Virtual Desktop's menu shortcut mid-game and coming back once left the headset black and
  frozen (seen once, not reproduced). Restart the game if it happens.
- Right after loading a save, there's a brief moment where you can see the world but can't move yet.
- A faint grain is visible over the room shown on the pre-game screens and main menu.

If something goes wrong, `BepInEx/LogOutput.log` (in the game folder) and
`%USERPROFILE%\AppData\LocalLow\ColePowered Games\Shadows of Doubt\Player.log` are the logs to
attach to a bug report.

## Building from source

1. Install the **.NET 6 SDK** (tested with 6.0.428). The projects target `net6.0`.
2. Install BepInEx be.788 into the game and run the game once so `BepInEx/interop` exists. The
   build references the game's interop assemblies from there.
3. Copy `Directory.Build.props.example` to `Directory.Build.props` and set `GameDir` to your game folder.
4. Build:
   ```
   dotnet build SoDVR/SoDVR.csproj -c Release
   dotnet build SoDVR.Preload/SoDVR.Preload.csproj -c Release
   ```
   A build copies the output straight into the game's `BepInEx` folder, **including
   `com.sodvr.mod.cfg`, which overwrites your live config** (the tracked config is the source of
   truth for defaults).
5. Package a release with `.\scripts\pack.ps1 -Version x.y.z`.

## License

MIT, see [LICENSE](LICENSE). Original work © Blah64; changes in this version © Wes Jones.
`RuntimeDeps/Native/openxr_loader.dll` is the Khronos OpenXR Loader (Apache 2.0).
The Quest button glyphs (`SoDVR/Assets/QuestGlyphs`) are from Kenney's Input Prompts (www.kenney.nl, CC0).
