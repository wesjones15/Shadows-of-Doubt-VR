# Post-FX-immune VR UI in HDRP: draw it after the camera, not inside it

How this mod makes VR menus, popups and the pointer laser immune to HDRP post-processing (TAA, depth
of field, bloom, auto-exposure, tonemapping), while the 3D world keeps all of it. Verified in the
headset (Meta Quest 3 via Virtual Desktop, OpenXR) on 2026-09-24.

The history of approaches that didn't work lives in `postfx_immunity_investigation.md`. This
document covers only the working solution.

## The idea in one paragraph

Don't try to get HDRP to exempt UI geometry from its post stack. Instead, keep UI out of HDRP's
frame completely. Render each eye with HDRP as normal. When `Camera.Render()` returns, the eye's
RenderTexture holds the finished, fully post-processed image. Before that texture is handed to the
headset, draw the UI into it yourself with a plain `CommandBuffer`, using the eye's own view and
projection matrices. HDRP never sees that geometry, so none of its effects can touch it.

```
per frame, per eye:
  eyeCam.Render()                  // HDRP: world + full post stack -> eyeRT
  overlay.Composite(eyeCam, eyeRT) // our CommandBuffer: UI quads + laser -> eyeRT
  copy eyeRT -> OpenXR swapchain   // xrEndFrame
```

This isn't camera stacking, which HDRP doesn't support: there's no second camera and no second
HDRP render into the frame. It's an immediate-mode draw into a texture you own, after HDRP is done
with it.

## Prerequisite: you need a gap between "HDRP finished" and "frame submitted"

This mod drives the eye cameras manually (`cam.enabled = false`, `targetTexture = eyeRT`, explicit
`Camera.Render()` every frame) and submits frames to OpenXR itself (a D3D11 `CopyResource` into the
swapchain image, then its own `xrEndFrame`). That gives a natural, synchronous gap to draw in.
`Camera.Render()` doesn't return until HDRP has recorded the whole frame, and GPU commands then run
in submission order, so no fence is needed.

If your project lets Unity's XR plugin own the eye rendering and submission, you need an equivalent
hook that runs after HDRP's final pass writes the camera target and before the XR plugin submits it.
This project didn't need one, so no such hook has been tested here.

## The pieces

### 1. Render the UI content into its own texture (content immunity)

Each UI canvas is rendered by its own "projector" camera into a RenderTexture, with post-processing,
exposure and anti-aliasing switched off for that camera. The canvas is switched to
`RenderMode.ScreenSpaceCamera` against that camera.

From `CameraRig.SetupRTPanelProjectorCamera`:

```csharp
var cam = camGO.AddComponent<Camera>();
cam.enabled = false;                          // rendered manually, once per frame
cam.stereoTargetEye = StereoTargetEyeMask.None;
cam.cullingMask = 1 << canvasLayer;
cam.clearFlags = CameraClearFlags.SolidColor;
cam.backgroundColor = Color.black;

var hd = camGO.AddComponent<HDAdditionalCameraData>();
hd.clearColorMode     = HDAdditionalCameraData.ClearColorMode.Color; // default is Sky
hd.backgroundColorHDR = Color.black;
hd.clearDepth         = true;
hd.antialiasing       = HDAdditionalCameraData.AntialiasingMode.None;
hd.volumeLayerMask    = 0;                   // no scene Volumes

hd.customRenderingSettings = true;
var fs = FrameSettings.NewDefaultCamera();
fs.SetEnabled(FrameSettingsField.Postprocess, false);
fs.SetEnabled(FrameSettingsField.ExposureControl, false);
var mask = new FrameSettingsOverrideMask();
mask.mask[(uint)FrameSettingsField.Postprocess] = true;
mask.mask[(uint)FrameSettingsField.ExposureControl] = true;
hd.renderingPathCustomFrameSettingsOverrideMask = mask;
hd.m_RenderingPathCustomFrameSettings = fs; // backing field, see "IL2CPP notes"
```

The target texture is **mipmapped**. A ~1920 px canvas usually ends up a few hundred pixels wide in
the eye buffer, and without mips the text shimmers and crawls as your head moves. Don't use TAA to
hide that; mips fix it directly. From `CameraRig.CreateRTPanelTexture`:

```csharp
var rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
{
    useMipMap = true,
    autoGenerateMips = false,
    filterMode = FilterMode.Trilinear,
    anisoLevel = 4,
};
rt.Create();
```

Each frame, before the eyes render: `projectorCam.Render(); rt.GenerateMips();`

**Where projectors sit.** Every projector culls the shared UI layer (all 32 layers are named by the
game, so there's no free one), and a ScreenSpaceCamera canvas sits `planeDistance` in front of its
projector. So each projector gets its own spot, 50 m apart and 500 m below the city
(`CameraRig.NextProjectorIsolationPosition`): no projector sees another's canvas or the legacy
WorldSpace UI. Don't park them much further out: the canvas is laid out at that world position,
and at -10000 m a float resolves only ~1 mm, about 2 canvas pixels. That snapped movement into
steps and made a 24 px button miss its own hit test. Don't give projectors one shared pose either:
it was tried, and after a save load it delayed the pause menu and case board by a long time
(cause unknown, `caseboard_findings.md` §7).

**Separate projectors break the game's cross-canvas position copies.** In the flat game every
canvas is one screen, and the game copies world positions between canvases (the pin quick-menu
takes its pin's position). Here such a copy lands on the other projector's plane, far off this
texture. `RTCanvasPanel.TryMapFromOtherScreen` maps it through whichever attached projector sees it
to the same screen point on this canvas.

**The projectors are perspective cameras.** TextMeshPro's SDF shader then applies a
perspective-correction term that uses each glyph's surface normal, so text under a zero z-scale
(the game scales some elements `(1, 1, 0)`) draws as solid blocks. On the game's overlay canvases
that term never runs.

### 2. A world-space quad that only places the panel, never renders

Each panel keeps a real `GameObject` quad (`GameObject.CreatePrimitive(PrimitiveType.Quad)`),
because it's convenient for:
- the panel's **transform**: position, rotation, and scale set to the panel's world size,
- a **MeshCollider** for pointer hit tests (`Collider.Raycast` gives `hit.textureCoord`, which maps
  straight to canvas pixel coordinates for `GraphicRaycaster`),
- its **mesh** and a `UI/Default` **material** with `mainTexture = rt`, which the compositor draws.

The important line is `meshRenderer.enabled = false`. If the eye cameras draw the quad, it's under
their post stack again. The GameObject's active state stays the panel's visibility flag.

### 3. The compositor: draw into each eye RT after HDRP

This is the whole trick, from `PostFXOverlayCompositor.Composite`:

```csharp
_cb.Clear();
_cb.SetRenderTarget(eyeRT);
_cb.ClearRenderTarget(true, false, Color.clear);   // depth only
_cb.SetViewProjectionMatrices(eye.worldToCameraMatrix, FlipY * eye.projectionMatrix);

// panels farthest-first (standard transparent ordering), then lasers
foreach (var p in panelsSortedFarToNear)
    _cb.DrawMesh(p.Mesh, p.LocalToWorld, p.Material, 0, 0);
foreach (var laser in lasers)
    _cb.DrawMesh(laserMesh, laser, laserMaterial, 0, 0);

Graphics.ExecuteCommandBuffer(_cb);
```

where `FlipY = Matrix4x4.Scale(new Vector3(1, -1, 1))`.

The coordinator calls it right after the eye cameras render, then copies to the swapchain:

```csharp
_rightCam.Render();
_leftCam.Render();

_overlay.BeginFrame();
_menuRTPanel.AppendOverlay(_overlay);    // adds quad mesh/matrix/material (+ its laser)
_tooltipRTPanel.AppendOverlay(_overlay);
_overlay.Composite(_rightCam, _rightRT);
_overlay.Composite(_leftCam, _leftRT);

// ... CopyEye(left/right) -> xrEndFrame
```

### 4. The laser goes in the same pass

A pointer laser drawn by the eye cameras gets bloomed and auto-exposed like everything else. Drawn
by the compositor, it's immune too, and because it's drawn after the panels it always shows on top
of the panel it hits.

The mesh is built **once**: a unit-length beam along +Z made of two crossed, tapered quads, so it
reads as a line from any angle without per-eye billboarding. Each frame it's placed with
`Matrix4x4.TRS(origin, Quaternion.LookRotation(dir), new Vector3(1, 1, length))`. Only Z is scaled,
so the widths (2.5 mm tapering to 0.8 mm) stay absolute. The material is `UI/Default` with plain SDR
cyan; HDR "punch-through" colours aren't needed once exposure can't reach the laser.

The pointer class keeps only the laser state (`origin`, `hitPoint`, `visible`) and hands it to the
compositor; it owns no renderer.

## Details that matter

- **Y orientation.** On D3D11, `SetViewProjectionMatrices` applies Unity's own render-into-texture Y
  flip. This project's eye cameras use `HDAdditionalCameraData.flipYMode = ForceFlipY`, so the eye
  RT is already in OpenXR row order, and the compositor has to cancel Unity's flip:
  `FlipY * projection`. If your eye RT follows Unity's normal RT convention instead, drop `FlipY`.
  An upside-down panel means this is the wrong way round.
- **Always on top, and why the depth clear is safe.** HDRP renders depth into its own internal
  buffers and writes only colour into the camera's target texture, so the target's depth
  attachment holds nothing meaningful. Clearing it makes `UI/Default`'s LEqual depth test pass, so
  UI draws over the world. Don't get "on top" by setting the `unity_GUIZTestMode` global instead:
  other UI/Default renderers still inside HDRP's frame read that same global.
- **Use legacy shaders** (`UI/Default` here). Outside HDRP's render loop, legacy shaders get
  everything they need from `SetViewProjectionMatrices` and `DrawMesh`: Unity's built-in
  view/projection and per-draw object matrices. HDRP shaders depend on per-camera state that HDRP
  sets up during its own render, so they aren't expected to work in this pass. That wasn't tested
  because there was no need.
- **Use the unjittered projection.** The eye camera's `projectionMatrix` is the one the mod set from
  the OpenXR FOV. HDRP applies TAA jitter internally without writing it back, so the overlay lines
  up with the world and doesn't jitter.
- **TAA history isn't disturbed.** HDRP's TAA history lives in its own buffers, not the camera
  target, so drawing into the target after the fact doesn't feed UI into next frame's
  reprojection.
- **Ordering.** Panels are sorted farthest-first from each eye (they're alpha-blended), then lasers
  are drawn last. The sort uses each quad's centre, which misorders a small quad just in front of a
  large one's edge (a tooltip over the navbar's edge is farther than the navbar's centre). So
  panels added with `onTop` (TooltipCanvas's views, the flat game's topmost canvas) are drawn after
  all the others.
- **Throttled rendering.** If you skip `Camera.Render()` on some frames and resubmit the last image,
  composite inside the same block as the render, so the resubmitted image already contains the UI.
- **Colour.** The panel's pixels reach the headset exactly as the projector camera produced them, with
  no tonemapping or exposure applied. Expect the UI to look like the flat-screen UI, not like the old
  in-world quad.

## IL2CPP / BepInEx notes

- Everything above (`CommandBuffer.SetRenderTarget` with a `RenderTexture`, `ClearRenderTarget`,
  `SetViewProjectionMatrices`, `DrawMesh`, `Graphics.ExecuteCommandBuffer`, `RenderTexture.GenerateMips`,
  and assigning managed arrays to `Mesh.vertices`/`uv`/`colors`/`triangles`) compiles and runs
  against the game's interop assemblies. The implicit `RenderTexture → RenderTargetIdentifier` and
  `T[] → Il2CppStructArray<T>` conversions work as is.
- Build meshes once and move them with matrices, rather than rewriting vertex arrays every frame:
  each managed-to-IL2CPP array assignment allocates.
- Write custom FrameSettings through `HDAdditionalCameraData.m_RenderingPathCustomFrameSettings`,
  not the `ref`-returning `renderingPathCustomFrameSettings` getter. Writes through `ref`-returning
  accessors are silently dropped across the interop boundary.
- Create the `CommandBuffer` lazily on first use rather than in a field initializer of an injected
  `MonoBehaviour`.

## Adding another immune panel

Use `RTCanvasPanel`, which does the projector, texture, quads and pointer registration:

1. Add the canvas's name to `RTOwnedCanvases`, so the legacy scanner and material patcher never
   touch it, even before the panel finds it.
2. Find the canvas and `Attach` it (screen layout: the game's own scaler lays it out as the flat
   game does) or `AttachSheet` it (you position content on it yourself, like open notes).
3. `CreateView` for each part to show. Each view is a sub-rect of the texture on its own world
   quad, with its own `RTPanelPointer` registered with `RTPanelInput`. Set its pixel rect, pose and
   `Visible` each tick.
4. Call the panel's `Render()` from the coordinator's `LateUpdate` before the eyes render, and its
   `AppendOverlay` between `BeginFrame()` and the `Composite` calls.

`TooltipRTPanel.cs` (many small views of one canvas), `CaseBoardPanel.cs` (screen layout) and
`CaseBoardWindows.cs` (sheet layout) are the reference implementations. `MenuRTPanel.cs` predates
`RTCanvasPanel` and still builds its pieces by hand.

## Tradeoffs and limits

- **No world occlusion.** Panels draw over walls and props. That's usual for VR menus and was
  accepted deliberately. Occluding them would need HDRP's internal depth buffer, which isn't
  exposed through the camera target.
- **Only what goes through the compositor is immune.** Anything still rendered as world-space canvas
  geometry by the eye cameras (here: HUD, minimap and the other not-yet-migrated canvases) still gets
  DoF, TAA and the rest.
- **Text resolution is bounded by the eye buffer.** The panel is resampled into the eye RT (rendered
  at 0.7× the headset's recommended resolution here), then again by the compositor's lens
  distortion. For sharper text, a panel could be submitted as an OpenXR `XrCompositionLayerQuad`
  through the same hand-built `xrEndFrame`. The laser would still need this overlay pass to draw
  over it.

## Verifying it works

- The menu text is upright and not mirrored (tests the `FlipY` choice).
- The laser tip lands exactly on the button whose hover highlight lights up (the overlay matrices
  agree with the collider geometry).
- The panel fuses at one depth in stereo, with no double image (per-eye view/projection are right).
- With depth of field on, the world behind the menu is blurred and the panel is sharp.
- Turning your head quickly while reading leaves no smear or ghosting on text edges (TAA doesn't
  reach it).
- The laser is a thin line with no bloom halo, in both bright and dark scenes.
