# Changelog

What changed in each SoDVR release. 2.0.0 is compared with
[Blah64's SoDVR v1.0.0](https://github.com/Blah64/Shadows-of-Doubt-VR), the version this one
continues from.

## 2.1.0 (2026-09-27)

Sharper panels everywhere, a VR-friendly pursuit warning, and fixes for facing, tutorials and blur.

**Tested on:** Quest 3, wireless over Virtual Desktop (VDXR OpenXR runtime), RTX 3080 Ti (driver
591.86), Ryzen 7 7800X3D, 32 GB RAM.

### At a glance

- **Sharper everything.** Crisp Panels now covers the HUD, key hints, controls list, labels and the
  case board, and thin frames no longer shimmer.
- **A VR pursuit warning.** A red glow at the edge of your vision replaces the flat game's flashing
  bars, with an optional heartbeat rumble.
- **Fixes** for the black HUD after waking in hospital, which way you face after waking, loading or
  sitting, tutorial popups leaving the game half-paused, and menus blurring your hands.
- Smaller save/exit and tutorial popups.

### Sharper panels
- **Crisp Panels** now covers the HUD, key hints, the controls list, labels and the case board, not
  just menus and popups. Up to 15 panels are sharpened at once; any beyond that are drawn as before.
- Thin borders and frames on sharpened panels no longer shimmer.
- The pointer laser is smooth again over sharpened panels.
- The save/exit and tutorial popups are half their old size.

### Being chased
- Instead of the flat game's flashing bars on the HUD, a red glow appears at the edge of your vision
  while you're pursued. Set its strength in VR Settings → VR → HUD → **Pursuit Glow** (0 turns it off).
- New, optional: **Pursuit Rumble** beats a heartbeat on both controllers while you're pursued (off
  by default).

### Fixes
- Dying and waking in hospital no longer turns the HUD into a black screen.
- You now face the way the game intends when you wake in the hospital bed, load a save, or sit in a
  chair.
- Closing a tutorial popup resumes gameplay instead of leaving the game half-paused.
- The pause menu and main menu no longer blur your own hands: they use the lighter conversation blur.

### Updating from 2.0.0
Replacing `SoDVR.dll` in `BepInEx/plugins` is enough. Keep your `com.sodvr.mod.cfg` if you've
changed settings: the new ones are added to it with their defaults.

## 2.0.0 (2026-09-27)

The first release of this continuation of Blah64's SoDVR. Most of the mod is rebuilt: the whole UI,
how you point and click, the case board, the HUD and the settings panel. Blah64's OpenXR
foundation, head and controller tracking, locomotion and preloader carry over.

**Tested on:** Meta Quest 3, wireless over Virtual Desktop (VDXR OpenXR runtime), RTX 3080 Ti,
Ryzen 7 7800X3D, 32 GB RAM, Windows 10, Shadows of Doubt (Unity 2021.3.45f2), BepInEx
6.0.0-be.788. Bindings for Valve Index, HTC Vive, Windows Mixed Reality and the KHR simple
controller are still included but untested.

### At a glance

- **Sharp UI, free of the game's blur.** Every screen (menus, popups, tooltips, case board, notes,
  map, dialogue, keyboard, HUD) is drawn on its own panel after the game's post-processing, so depth
  of field, bloom and exposure never smear it. Menus, popups and tooltips go to the headset as their
  own layers for crisper text.
- **No more z-fighting.** In Blah64's version every panel was a stack of 3D layers that flickered
  against each other. Each panel is now one flat image, so text, icons and backgrounds hold still.
- **Quest button prompts.** The game's key hints and the interaction label show Quest controller
  buttons instead of keyboard keys, and they follow whichever hand is your main hand. An in-headset
  VR controls list covers what the mod adds.
- **One main hand.** The right hand (by default) points, clicks, interacts and holds your item. Pull
  the other hand's trigger to swap. The world dot now lands where your controller points.
- **Grab any window** with the grip, move it in 3D, and push or pull it with the stick. The whole
  case board moves by its top bar.
- **A case board made for VR.** Drag pins, link them with B, pan and zoom the cork, open notes as
  separate windows, and hold Y for a radial menu of Inventory, Upgrades, Notebook and Map.
- **Talking, the map and typing.** Point at a dialogue option and pull the trigger to say it, zoom
  the map with the stick, and type into text boxes on a VR keyboard.
- **A HUD for a headset.** It's see-through, follows your head loosely, and steps aside for the case
  board and computer screens. Objective markers, NPC reactions and overheard speech float at their
  targets.
- **Usable computers.** You stand back from the screen and the cursor follows your controller.
- **Jump from a standstill.** Standing jumps used to fail, and walking jumps went twice as high.
  Both now behave like the flat game, and jump speed and gravity are adjustable.
- **Fall damage and knockdown work**, with an option to keep the damage but skip the knockdown.
- **Loading a save from the pause menu no longer crashes.** The loading screen shows, and the menus,
  case board and your arms all survive the load.
- **Comfort and stability.** A simple room instead of black frames before the game starts, smooth
  headset tracking through loading freezes, a monitor mirror, and adjustable render scale.
- **VR Settings rebuilt** on the game's own settings, with Apply and Reset Defaults. Every VR option
  lives in the config file.
- **For modders:** the 9,000-line `VRCamera.cs` is split into ~70 files around a single
  render-texture panel pipeline, with architecture notes and design docs in the repo.

### Installing and upgrading

- **BepInEx 6 bleeding edge be.788 (IL2CPP x64)** from
  [builds.bepinex.dev](https://builds.bepinex.dev/projects/bepinex_be) is the tested loader. The
  GitHub "pre-release" builds that Blah64's README pointed to are untested.
- The release zip now also contains the default config (`BepInEx/config/com.sodvr.mod.cfg`), the
  LICENSE, the third-party license texts and the README. Its paths use forward slashes, so mod
  managers and non-Windows extractors unpack it into folders correctly. `SoDVR.dll` and the config
  are also attached to the release on their own.
- **Coming from Blah64's version:** overwrite every file. Your old VR settings aren't carried over:
  Blah64's version kept them in the game's PlayerPrefs (turning, movement, HUD, menu distance) or
  only for the session (Left Laser, Item Hand). 2.0.0 keeps everything in
  `BepInEx/config/com.sodvr.mod.cfg`, so set them again on the VR tab (F10).
- On the "press any key" screen, pull any trigger or grip.
- The plugin and preloader now report version 2.0.0 (Blah64's build reported 0.1.0).

### Controls compared with Blah64's v1.0.0

"Main" means the main hand (the right hand unless you swap). Rows marked "unchanged" behave as before.

| Input | Blah64's v1.0.0 | 2.0.0 |
|---|---|---|
| Right trigger | Click menus | **Main trigger**: click and drag on panels; interact / use item in the world; click on an in-game computer's screen |
| Left trigger | Interact in the world, aimed from the left controller | Swaps the main hand (the pull is swallowed; it never clicks). With the left hand as main, it does what the right trigger does |
| Right grip | Drag floating panels | **Main grip**: grab and move any window; secondary interact (right mouse) in the world |
| Left grip | Right mouse (pick up evidence, secondary interact) | Nothing, unless the left hand is the main hand |
| A | Jump; right-click on a canvas | Unchanged. The right-click now works from whichever hand's laser is on the panel |
| B (hold) | Notebook / map while held | The map, locked in front of you while held |
| B, controller behind right shoulder | Inventory | Unchanged |
| B drag on a canvas | Middle-click drag (pan the map, scroll) | Removed: pan with a trigger drag. On panels, B is now the "alt" button: hold on a pin and release on another to link them; end a conversation |
| Y | Sent F (listed as "use item"; F is the game's case-board key) | **Tap**: open / close the case board (or close a screen opened from the radial menu). **Hold**: radial menu |
| X | Crouch | Unchanged |
| Menu button | Pause | Unchanged |
| Left stick | Move; click to sprint | Unchanged. While the left hand drags a window, its stick pushes and pulls the window instead |
| Right stick left/right | Snap or smooth turn | Unchanged, but paused while the laser is on a panel |
| Right stick up/down | Scroll, in VR Settings only | Scroll any panel; zoom the case board and map around the laser; step through dialogue options. While the right hand drags a window, pushes and pulls it |
| Right stick click | Flashlight | Unchanged, but held back while the laser is on a panel |
| "Press any key" screen | A mouse click | Any trigger or grip, either hand |
| F8 | Re-centre every canvas | Re-centre the case board in front of you |
| F9 | (none) | Debug: dump every canvas to the log |
| F10 | VR Settings | Unchanged |
| End | Text diagnostic dump | Removed |

### Everything that changed

#### Rendering and image quality

- **UI is drawn after the game's post-processing.** Each screen is rendered to its own texture by a
  dedicated camera and drawn into each eye after depth of field, bloom, auto-exposure, tonemapping
  and TAA have run. In Blah64's version UI was 3D geometry rendered by the game camera, which is why
  menus looked washed out or blurred and needed brightness boosts.
- **No more z-fighting.** Blah64's version turned each screen into 3D geometry, nudging its layers
  a few centimetres apart, and the pieces flickered against each other on every panel. Each panel is
  now one flat, pre-rendered image, so nothing on it can z-fight.
- **Crisp Panels** (VR tab → Rendering, on by default): the main and pause menu, popups, tooltips,
  the VR keyboard and VR Settings go to the headset as OpenXR layers of their own. The headset
  samples each panel's texture at display resolution instead of the reduced-resolution eye image.
  The lasers go in a see-through layer above them. If a panel can't become a layer, that frame
  draws everything into the eye image as before.
- Panels always draw on top of the world; walls and objects never cut into them.
- Panel textures are mipmapped (trilinear, 4× anisotropic), so text stays legible without
  shimmering at a distance. Panels no longer use TAA, which only softened them.
- **See-through panels:** the HUD, dialogue window, conversation subtitles, tooltips, context menus
  and popups, case-board navbar, inventory and XP bar, location details, upgrades, note windows, VR
  Settings and the interaction label have transparent backgrounds. The map keeps a solid background,
  because its red border only reads on one.
- Glow sprites on see-through panels draw as the game draws them. The black-backed lens-flare
  decals (the navbar's and map's glow strips) are hidden there instead of showing as dark boxes or
  smears.
- Scrolling lists on see-through panels (such as the notebook's) clip to their boxes again.
- **The laser** is thin, plain cyan and drawn after post-processing. The old HDR-bright beam
  bloomed into a glowing halo. It ends exactly where it hits, with no separate cursor dot, and always
  shows on top of the panel it points at. In the pause and main menu, the case board and VR Settings
  it stays visible, 3 m long, when it misses every panel, so you can always find it.
- **Render scale** is a setting (VR tab → Rendering → "Scale (restart)", 0.5× to 1.4×, default 0.7×,
  the value Blah64's version had fixed). It's capped at the headset's maximum and applies on the
  next launch.
- **Monitor mirror** (VR tab → Rendering, on by default): the game window shows the left eye's
  finished image, panels included, cropped to the monitor's shape and upright. Toggles live.

#### Pointing and the main hand

- **One main hand** does all the pointing: the panel laser, the world dot and its label, interact
  (trigger, left mouse), secondary interact (grip, right mouse), your held item and its animations,
  and the game camera's aim. It's the right hand by default.
- Pulling the other hand's trigger swaps the main hand. That pull only swaps. A swap can't happen
  mid-click or mid-drag. The choice is saved in the config (VR tab → "Main Hand: Right"), where
  Blah64's "Item Hand" was per session and defaulted to the left hand.
- With the left hand as main hand, the arms are mirrored, so your item and its animations appear in
  the left hand.
- **The world dot and the game's interaction aim where the controller points.** The game's ray used
  to start at your eye and run parallel to your hand, landing off by the gap between them. Now the
  hand's own ray finds the spot, and the game's interaction is aimed through it, so the dot shows
  exactly what you'll interact with.
- The world dot is a round dot drawn after post-processing, so depth of field no longer blurs it. It
  hides behind any panel in front of it, while a menu, the case board or VR Settings is up, and while
  you use a computer. It replaces Blah64's pink aim dots.
- **World Laser** (VR tab → Controls, off by default) draws a beam from the main hand into the
  world. It replaces "Left Laser", whose beam never actually drew in the headset.
- A and B work on panels from whichever hand is pointing.
- **Controller presses no longer reach the flat game window behind a menu.** World clicks, grip
  right-clicks and stick-click middle-clicks are held back while the laser is on UI. Before, a world
  click with a menu up could hit whatever flat-screen button sat under the mouse cursor, such as the
  exit prompt behind the pause menu.
- **Panels behave like a real mouse:** hover highlights, press, drag (starting after 1.5 cm of
  movement on the panel), click on release, A as right-click, and stick scrolling. A panel you
  pressed keeps the pointer until you let go.
- Buttons now run the game's own click handling, so buttons that ignored the old simulated clicks
  work, such as the eye toggle that hides a fact on a note.
- Overlapping panels no longer both take one click: the nearest gets the pointer. Within a window,
  the pointer goes to whatever is drawn on top.
- **The game stays in mouse-and-keyboard mode.** A virtual gamepad appearing mid-session (Virtual
  Desktop can create one) used to switch the game to gamepad mode: pause, the case board and
  interaction stopped responding, and the hints showed gamepad buttons.
- NPC reaction icons and speech bubbles fade in and out by where your head looks, not where your
  hand points.

#### Quest button prompts

- **The game's key hints show Quest controller buttons** instead of keyboard keys, for every key the
  mod binds. Keys the mod doesn't bind keep the game's own glyph.
- They follow your main hand, so swapping hands updates the prompts.
- Case board and notebook show Y; the map, create string and weapon select show B; Back shows the
  menu button; the case board's zoom shows the right stick. Right-click shows A on menus and the
  case board, and the main grip in the world.
- The interaction label shows the button beside each action it lists.
- The glyphs are drawn at twice the text size, centred on the words, and still fit inside the game's
  hint rows, so every hint keeps its text.
- The glyphs are Kenney's Input Prompts (CC0).

#### Grabbing and moving windows

- **Grip any panel** with the laser hand to grab it. The grabbed point stays under the laser, and the
  drag stays with the hand that started it.
- While dragging, that hand's stick pushes the window away (up) or pulls it closer (down), from 0.2 m
  to 15 m. Meanwhile the stick does nothing else: no turning, scrolling or dialogue stepping on the
  right, no walking on the left.
- Positions are remembered for the rest of the session: the case board (where it reopens, each panel
  on it, open notes), the dialogue window, the keyboard, the map (on the board and while walking),
  the VR controls panel and any other screen.
- **Window Positions: Reset** (VR tab → Windows) puts them all back at once, without waiting for
  Apply.
- Menus and windows open 1.5 m from your head by default, down from 1.8 m (VR tab → Windows → Menu
  Distance, 0.5 to 3.5 m).

#### Main menu, pause menu and popups

- The main and pause menu is a 1.6 m wide panel (Blah64's was 1.2 m) that opens in front of you at
  the menu distance.
- It stays over the case board and your notes: nothing in front of it can cover it or catch its
  clicks. Popups, tooltips, the keyboard and VR Settings draw over the menu.
- It stays where it was placed relative to you when the game moves you, such as the jump from the
  "press any key" screen to the main menu, which used to leave the menu far behind you.
- The menu's Settings button opens VR Settings, as before.
- Popups (the save/exit prompt, tutorial messages) open in front of your head, sized to the dialog.
- Context and quick menus open where the laser points, and tooltips follow beside the laser. They
  draw above every other panel. A press anywhere else closes an open context menu, as a click does
  in the flat game.
- Menus the game places for a mouse cursor that isn't there, off the edge of the screen, are moved
  back into view. Context-menu text renders properly instead of as solid blocks.

#### Case board

**Opening and layout**
- The board opens in front of you each time: the navbar 1.85 m away and the corkboard 2.0 m (Blah64's
  corkboard was 2.3 m away). F8 places it in front of you again.
- Every case-board screen is laid out exactly as the flat game lays it out on your monitor.
  Blah64's rescaled layout could lose pieces, such as the inventory's close button.
- The navbar shows at its own size, as wide as the corkboard and centred just above it.
- **Grip the navbar to move the whole board.** The cork, navbar, open panels, notes, the board's map
  and the HUD around it move together, staying upright. The board then reopens in that spot relative
  to you for the rest of the session.

**Corkboard**
- Trigger on a pin (its photo counts) opens its note. Drag to move the pin; its strings stay attached
  as it moves.
- **B:** hold on a pin and release on another to link them with string.
- The pin quick-menu's "new link": the string follows the laser, a trigger on another pin finishes
  it, and a trigger elsewhere, B or closing the board cancels it.
- **A** on a pin or string opens its context menu.
- **Pan:** trigger-drag on empty cork. The grabbed point stays under the laser, panning stops at the
  cork's edges, and it's smooth in both directions. The board no longer vanishes when panned to an
  empty stretch of cork.
- **Zoom:** right stick, around the laser.

**Notes and windows**
- Every open note, inspected item and the Detective's Notebook is its own window, cascading in front
  of the navbar. The first opens 1.2 m from you. Each can be grip-dragged and keeps its place while
  it's open.
- A note's small close/minimise button has a bigger hit area, matching the other buttons in its
  column, and its hover highlight matches.
- The pin icon on a note toggles its pinning. Re-pinning puts the pin back where it was on the cork
  (or the middle of the visible board), with its strings redrawn at once.
- Notes sit on whole pixels, so their contents don't jitter.

**Inventory, upgrades and location details**
- The inventory and its XP bar show side by side (the game anchors the XP bar to the screen's edge),
  1.5 m from you in front of the board. They close at once instead of lingering through the game's
  fade-out.
- Upgrades open 1.5 m from you, in front of the board.
- Location details sit just in front of the board and can be grip-dragged.

**Radial menu and single screens**
- **Hold Y** (over 0.3 s) for a radial menu 1 m out along the left controller's laser: Inventory,
  Upgrades, Notebook and Map. Aim the left controller at one (it lights up) and release to open it.
  Releasing in the centre opens nothing.
- The chosen screen opens on its own, 1.5 m in front of you (the notebook at 1.2 m), without the
  corkboard. Tap Y to close it. If the game opened the board along with it, the board closes again
  afterwards. Moving a single screen doesn't change your board layout.

#### Map

- The map is its own panel, cropped to the map window.
- It has two places: **hold B** and it's locked in front of your body; on the case board it sits where
  the flat game shows it. Each remembers where you drag it, and zoom, pan and floor carry over
  between them.
- Trigger-drag pans, a double-click opens an address, the right stick zooms around the laser, and A
  opens the map's context menu at the laser (routes and the rest).
- The map stays covering its window as you pan and zoom, zoom no longer flickers on the case board,
  and the map clips at its edges as in the flat game.
- B no longer opens the map while you're talking to someone, or right after ending the conversation.

#### Conversations

- The dialogue window is its own see-through panel, 0.75 m in front of you at eye level, where it no
  longer covers the citizen's face. Grip-drag it and it keeps that spot for later conversations.
- **Point at an option to select it, pull the trigger to say it.** The right stick steps through the
  options from anywhere, repeating while held. **B** ends the conversation from anywhere.
- The citizen's replies appear just above the dialogue window and move with it. They lie flat and
  read correctly (they used to turn toward your hand, foreshortened or mirrored), on a see-through
  background.
- Clue and evidence messages (the game's centre-screen notifications) appear beside the dialogue
  window during a conversation, and in their HUD spot otherwise. Their flight into the status icon
  still lands.

#### VR keyboard (new)

- A QWERTY keyboard opens whenever a text box gets focus, whether you click one or the game focuses
  it (as the save-name popup does). Neither Virtual Desktop's runtime nor the game offers one in VR.
- Keys type straight into the box at its cursor: Shift, cursor left and right, Bksp, Space,
  **Clear** (empties the box) and **Done** (does what Enter does, and closes the keyboard).
- A preview line shows the text. Press on it to place the cursor or drag to select, as in the flat
  game's text boxes; typing, Bksp and Clear act on the selection.
- It opens low and tilted up at arm's length, draws above other panels, and can be grip-dragged
  (remembered).

#### HUD

**While walking**
- The HUD is one see-through sheet at a fixed apparent size, drawn after post-processing. It follows
  your head loosely: look around within 25° and it stays put, look further and it eases after you.
- Place it on the VR tab → HUD: **HUD Distance** (default 2.5 m; changes only its depth, not its
  apparent size), **HUD Size** (0.6× to 1.5×; 1 spans about 60° of view) and **HUD Height** (−0.3 to
  +0.3 m, default −0.15 m).

**Around the case board and computers**
- With the case board open, each HUD element moves to its side of the board. The left and right
  columns hinge 25° toward you like a trifold board and keep clear of the corkboard and navbar's
  actual outline. They follow the board when you move it.
- At a computer, the HUD is laid out around the screen you're using instead of floating in front of
  it.
- Subtitles outside a conversation, such as a warning that someone spotted you, go with the HUD.

**Markers in the world**
- Objective pointers, overheard speech and NPC reaction indicators appear at their targets in the
  world, facing you at the HUD's apparent size. An objective pointer whose target is out of view
  clamps to the edge of the HUD and points the way, as the flat game clamps it to the screen's edge.
- NPC reaction indicators are drawn at twice the game's size, so they're readable.

**Awareness compass**
- The 3D awareness compass sits 1 m ahead, just under the HUD, as the game orients it: a level disc
  pointing north. Its awareness icons turn to face you. It appears when the game shows it.

**VR controls list**
- **VR controls panel:** with the case board open, a panel on the HUD's right side, beside the board,
  lists the controls the mod adds, with Menus and World pages. Its "Show in world" switch (VR tab →
  HUD → Controls in World, off by default) keeps the World page up while you walk. It can be
  grip-dragged on the case board; the move carries over to the walking HUD, and Window Positions:
  Reset forgets it.
- **Live hints:** with the case board open, a row right under the game's key hints shows what the
  controls do on what the laser is on: a pin, a string, the board, the map, or a window you can grab.

**Interaction label**
- The label over what the world dot is on uses the game's purple tooltip box: the name in white, and
  under it each action with its Quest button glyph.
- It's 1.5× the HUD's size at 1 m and shrinks with distance (half that at 4 m), standing a fixed gap
  above the point so it doesn't cover what it labels.
- It hides behind any menu, window or case-board panel in front of it, and while you use a computer.

#### Computers

- Using an in-game computer, you stand 30 cm further back from the screen than the game seats you,
  so it fits in view. The cursor goes where your controller points, and the main trigger clicks.
- The HUD is laid out around the screen; the world dot and interaction label are hidden.

#### Other game screens

- Any other screen the game shows (the splash screen, "press any key", the prototype builder, others)
  gets a panel of its own: clickable, grip-draggable, opened at the menu distance, and reopened where
  you last left it.

#### Movement and the body

- **Jumping from a standstill works.** Standing still, the ground check often missed the floor, so
  standing jumps failed. Walking jumps no longer go twice as high, and falls are no longer twice as
  fast. Movement now runs once per frame, as the flat game's does.
- **Jump Speed** (default 5 m/s, 3 to 7) and **Gravity** (default 15 m/s², 9.8 to 20) are on the VR
  tab → Movement. They used to be fixed.
- **Fall damage** works, using the game's own landing rules: damage, broken legs, landing and impact
  sounds, the knockdown, and the "Shafted" achievement. Loads, teleports and air vents never count
  as falls.
- **Fall Knockdown** (VR tab → Movement, on by default): turn it off to take the same fall damage
  without the view dropping to the floor and getting back up.

#### Loading, pre-game screens and stability

- **Loading a save from the pause menu no longer crashes.** A load from in game reloads the whole
  scene, and the VR view didn't survive it. Now:
  - the void room covers the teardown instead of black frames
  - the loading screen shows
  - the menu no longer stays frozen on the save list
  - the case board and menus don't vanish after the load, leaving only the blurred world
  - panels keep working for the rest of the session
  - your arms come with you, instead of staying behind frozen and pink
- **Void room:** a simple reference room replaces the blank frames on the "press any key" screen,
  while loading and at startup, and stays through the main menu in place of the game's animated
  water backdrop, which costs more and looks worse in a headset. It also covers the moment a load
  from the pause menu tears down the old scene. Config: `VoidRoom.Enabled`, `VoidRoom.ShowOnMainMenu`.
- **Smooth Loading** (VR tab → Rendering, on by default): when a loading step freezes the game, the
  headset keeps showing a world-fixed view of the room at full frame rate, with the live loading
  screen (or "press any key") in its own place, instead of juddering or freezing.

#### VR Settings

- VR Settings is a see-through panel (900 × 700) opened in front of you at the menu distance. You can
  grip-drag it, the stick scrolls the open tab, and the laser clicks everything, scroll arrows
  included. Its colours look as intended rather than over-bright, and its arrows and close button
  use characters the game's font has, instead of empty boxes.
- **The Graphics, Audio, Controls and General tabs are rebuilt on the game's own settings.** Each
  row reads its value, range, default and choices from the game and applies through the game's own
  options menu, so values use the game's scale (light distance used to be sent as 1.0 against the
  game's 40–200%) and the game saves them itself. New rows include DLSS mode, interface volume,
  language, and the game's HUD toggles (objective markers, directional arrow, awareness indicator,
  popup tips).
- **Apply and Reset Defaults.** Changes wait until you press Apply. Reset Defaults resets the open
  tab. Switching tabs or closing with unapplied changes asks Apply / Discard / Cancel.
- It reopens on the last tab you used, and tabs no longer draw over each other.
- **The VR tab** holds every mod setting, each saved in the config file:
  - Turning: Smooth Turn, Snap Angle (15° to 90°, default 30°), Smooth Speed (60 to 240°/s, default 120)
  - Movement: Move Speed (2 to 8 m/s, default 4), Sprint Multi (1.4× to 3×, default 1.8×), Jump
    Speed, Gravity, Fall Knockdown
  - Controls: Main Hand: Right, World Laser
  - Windows: Menu Distance, Window Positions: Reset
  - HUD: HUD Distance, HUD Size, HUD Height, Controls in World
  - Rendering: Monitor Mirror, Smooth Loading, Crisp Panels, Scale (restart)
- Open it with F10 or the Settings button in the main or pause menu, as before.

#### Removed

- Blah64's world-space UI (game screens converted into 3D geometry in the game camera) and everything
  built on it: the pink aim dots, the right-hand cursor reticle, the old laser, and the per-screen
  brightness boosts.
- The "Left Laser" and "Item Hand" settings (replaced by World Laser and Main Hand).
- The HUD's horizontal offset and "laggy follow" options. The HUD always follows loosely now.
- B-drag middle-clicking on canvases (pan with a trigger drag instead).
- The End-key text dump (F9 dumps every canvas instead).

### For modders

#### Config reference

Everything lives in `BepInEx/config/com.sodvr.mod.cfg`. Blah64's version had no config entries; its
VR settings were `SoDVR.*` PlayerPrefs keys, which 2.0.0 no longer reads.

| Section | Key | Default | VR tab row |
|---|---|---|---|
| Turning | `SmoothTurn` | false | Smooth Turn |
| Turning | `SnapTurnAngle` | 30 | Snap Angle |
| Turning | `SmoothTurnSpeed` | 120 | Smooth Speed |
| Movement | `MoveSpeed` | 4 | Move Speed |
| Movement | `SprintMultiplier` | 1.8 | Sprint Multi |
| Movement | `JumpSpeed` | 5 | Jump Speed |
| Movement | `Gravity` | 15 | Gravity |
| Movement | `FallKnockdown` | true | Fall Knockdown |
| Controls | `MainHandRight` | true | Main Hand: Right |
| Controls | `WorldLaser` | false | World Laser |
| Windows | `MenuDistance` | 1.5 | Menu Distance |
| HUD | `Distance` | 2.5 | HUD Distance |
| HUD | `Size` | 1 | HUD Size |
| HUD | `VerticalOffset` | -0.15 | HUD Height |
| HUD | `ControlsInWorld` | false | Controls in World |
| Rendering | `MonitorMirror` | true | Monitor Mirror |
| Rendering | `StallFrames` | true | Smooth Loading |
| Rendering | `PanelLayers` | true | Crisp Panels |
| Rendering | `RenderScale` | 0.7 | Scale (restart); read when the swapchains are made |
| Rendering | `ForceDisableDepthOfField` | false | config only; a leftover development switch that forces depth of field off on every HDRP Volume |
| VoidRoom | `Enabled` | true | config only |
| VoidRoom | `ShowOnMainMenu` | true | config only |

BepInEx never resets an existing key to a new default, so the release ships the config. In the
repo, the tracked config is the source of truth for defaults, and a build copies it over the live
one.

#### Code layout

- Blah64's 9,085-line `VRCamera.cs` is split into ~70 files by subsystem; `VRCamera` now only
  orchestrates the frame. [ARCHITECTURE.md](ARCHITECTURE.md) maps how the files relate.
- **One UI pipeline.** Every canvas is owned by a render-texture panel (`RTCanvasPanel`, shown
  through one or more `RTPanelView` quads). Each panel's canvas is set to ScreenSpaceCamera against
  its own projector camera, parked in its own slot 500 m below the city. The quads' MeshRenderers
  are off: HDRP never draws them. They're kept for placement and hit-testing, and
  `PostFXOverlayCompositor` draws them into each eye's RenderTexture with a CommandBuffer after
  `Camera.Render()` returns. Crisp Panels copies panel textures into OpenXR quad layers
  (`PanelLayerCopy`, `XrQuadLayer`).
- Transparent panels render their UI outside HDRP (its colour buffer has no alpha) into ARGB32
  textures with a depth-stencil buffer, and composite premultiplied.
- Owned canvases are detached from `GameCanvas` to the scene root and registered in
  `RTOwnedCanvases`. `LooseCanvasPanels` claims any root screen canvas no dedicated panel owns.
- **Input:** `RTPanelInput` is the single pointer arbiter (focus, the one laser, trigger edges, the
  hand swap); `RTPanelPointer` delivers input-module events; `IRTPointerExtension` adds panel-specific
  handling (corkboard, map, dialogue, windows); `RTPanelGrip` drags any `IRTGripTarget`; `PanelLayer`
  bands decide draw and pointer order.
- How to add a panel, and the pitfalls (the Y-flip, projector precision, perspective-projector
  text, zero z-scale, asset unloading), are in
  [docs/postfx_immune_ui.md](docs/postfx_immune_ui.md).

#### Game hooks

- Harmony patches: `ControlsDisplayController.GetControlIcon` (postfix, the Quest glyphs);
  `ReactionIndicatorController.Update` and `SpeechBubbleController.Update` (the camera takes the
  head's rotation while they run); `ComputerController.OnClickOnOSElement` (prefix, logging only).
- `FirstPersonController` stays disabled, as in Blah64's version, so `FallDamage` reimplements its
  landing handler's human branch and calls the game's own methods: `fallCount` grows 0.1 per metre
  airborne; damage = (fallDamageModifier upgrade + 1) × max(fallCount − 0.4, 0) ×
  fallDamageMultiplier, zero in spending-time mode; impact sound from 0.2; a broken-leg chance above
  0.6 damage unless `Game.disableFallDamage`; `Player.Trip(damage)` for a damaging fall, else the
  drunk trip chance; "Shafted" for about four floors down a stairwell.
- The mod moves `InterfaceController.speechDisplayAnchor` and the centre-message container onto
  canvases of its own, closes context menus with `ContextMenuController.ForceClose`, drives pins
  through `DragCasePanel.SetPositionDirect` and `CasePanelController`'s custom string links, toggles
  pinning through `InfoWindow.TogglePinned`, and switches the game back to mouse-and-keyboard input
  mode whenever it changes. Mods touching the same objects may conflict.
- Mod-made meshes, materials and textures use `HideFlags.DontUnloadUnusedAsset`, since every load
  unloads unreferenced assets. Game objects stay in their own scene's hierarchy, since a load from the
  pause menu reloads the scene.

#### OpenXR and the frame loop

- A frame can submit up to 16 composition layers: the eyes' projection layer, then panel quad layers
  and the lasers' projection layer on top.
- The eye swapchain size is `RenderScale` × the recommended size, capped at the runtime's maximum
  image size.
- Smooth Loading: when the main thread is silent for 45 ms while the void room is up, a background
  thread takes over `xrWaitFrame` / `xrBeginFrame` / `xrEndFrame` and submits a captured cube of the
  room plus the current loading panel as quad layers. All frame calls go through one gate, and the
  D3D11 context is set multithread-protected.

#### Diagnostics

- **F9** dumps every canvas (name, owner, active state, nesting, raycasters, render mode) and the
  case-board canvases' children to `BepInEx/LogOutput.log`.
- A hang watch logs the frame step the main thread is stuck in after 3, 10, 30 and 60 s of silence.
- Every gap of 100 ms or more without a headset frame is logged with the city loader's state.
- The Quest glyph font is embedded in `SoDVR.dll` and written to `BepInEx/cache` on first use.

#### Building and packaging

- `scripts/pack.ps1` now packs the config, LICENSE, `licenses/` and README, writes the zip with
  forward-slash paths (not `Compress-Archive`), and prints a `gh release create` line that also
  attaches `SoDVR.dll` and the config.
- A build copies the tracked `com.sodvr.mod.cfg` into the game's `BepInEx/config`, overwriting your
  live config.
- `licenses/` holds the OpenXR loader's Apache 2.0 text and Kenney's CC0 notice. The LICENSE keeps
  Blah64's MIT copyright and adds one for this version's changes.

#### Docs

- [ARCHITECTURE.md](ARCHITECTURE.md): the files and how they fit together.
- [docs/postfx_immune_ui.md](docs/postfx_immune_ui.md): the working post-FX-immune panel approach, as
  a how-to.
- [docs/postfx_immunity_investigation.md](docs/postfx_immunity_investigation.md): every approach tried
  (custom passes, a second UI camera, the AfterPostProcess queue) and why each failed.
- [docs/caseboard_findings.md](docs/caseboard_findings.md): the case board's canvases and how their
  migration was solved.
- [docs/v1_findings.md](docs/v1_findings.md): notes on Blah64's version and the game's APIs.
- [docs/pending_tasks.md](docs/pending_tasks.md): development notes on open questions.

### Known limitations in 2.0.0

- The in-game mod.io browser isn't shown in VR.
- Only Quest 3 over Virtual Desktop has been tested. Vive and WMR controllers have no A/B/X/Y
  buttons, so several actions are unavailable on them.

### Credits

Blah64 (the original SoDVR), Kenney (Quest glyphs, CC0), the Khronos Group (OpenXR loader, Apache
2.0) and ColePowered Games (Shadows of Doubt).
