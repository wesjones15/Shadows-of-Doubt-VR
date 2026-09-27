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

**Transparent panels: render the UI outside HDRP too.** A projector set up as above always
produces an opaque texture, whatever it clears to. HDRP renders into its own colour buffer, which
has no alpha channel, and copies that into the target with alpha 1. Clearing to `(0,0,0,0)` still
gave a black box (the HUD sheet, 2026-09-25), just as the second UI camera of
`postfx_immunity_investigation.md` §2b came back opaque. The fix is to skip HDRP's render of that
camera completely. `HDAdditionalCameraData.customRender` is a per-camera hook: when it's set,
HDRP calls it *instead of* rendering the camera, and the hook draws the canvases itself straight
into the ARGB32 target (`TransparentUIRender.cs`):

```csharp
hd.add_customRender((Action<ScriptableRenderContext, HDCamera>)((context, _) =>
{
    camera.TryGetCullingParameters(out var p);
    var culling = context.Cull(ref p);
    context.SetupCameraProperties(camera);
    cmd.SetRenderTarget(camera.targetTexture);
    cmd.ClearRenderTarget(true, true, Color.clear);        // transparent clear
    context.ExecuteCommandBuffer(cmd);
    var drawing = new DrawingSettings(new ShaderTagId("SRPDefaultUnlit"),  // UI shaders' untagged pass
        new SortingSettings(camera) { criteria = SortingCriteria.CommonTransparent });
    var filtering = new FilteringSettings(new Il2CppSystem.Nullable<RenderQueueRange>(RenderQueueRange.all),
        camera.cullingMask);
    context.DrawRenderers(culling, ref drawing, ref filtering);
    context.Submit();
}));
```

The texture's alpha is then the canvas's own coverage. The compositor already alpha-blends each
quad, so the world shows through wherever the canvas draws nothing.
- **Verified** by a one-time readback of the HUD texture: every cleared pixel had alpha 0, and
  about 96% of the texture was fully clear.
- **To use it:** `RTCanvasPanel.Attach(canvas, width, transparent: true)` (or
  `CameraRig.SetupRTPanelProjectorCamera(..., transparent: true)`).
- **In use on:** the HUD (`HudRTPanels`), the clue messages (`ClueMessagePanel`) and the
  dialogue window (`DialogueRTPanel`). Other panels keep the opaque HDRP path.
- **Not camera stacking:** this camera still renders into its own texture, and HDRP just doesn't
  render it (see §2e of the investigation for why stacking is out).
- **Not in this path:** HDRP's post, exposure and the rest of its pipeline. None of it is wanted
  on UI anyway.

**Where projectors sit.** Every projector culls the shared UI layer (all 32 layers are named by the
game, so there's no free one), and a ScreenSpaceCamera canvas sits `planeDistance` in front of its
projector. So each projector gets its own spot, 50 m apart and 500 m below the city
(`CameraRig.NextProjectorIsolationPosition`): no projector sees another's canvas or the legacy
WorldSpace UI. Don't park them much further out: the canvas is laid out at that world position,
and at -10000 m a float resolves only ~1 mm, about 2 canvas pixels. That snapped movement into
steps and made a 24 px button miss its own hit test. Don't give projectors one shared pose either:
it was tried, and after a save load it delayed the pause menu and case board by a long time
(cause unknown, `caseboard_findings.md` §7).

**One exception: the clue-message projector.** `ClueMessagePanel` shares the HUD projector's pose
on purpose, but on a different layer (1, `TransparentFX`), so each still sees only its own canvas.
The game's centre messages were moved out of GameCanvas onto a canvas of our own. They fly into a
HUD status icon when a clue is finished, and sharing the pose keeps that flight in one world space.
Only two projectors share a pose here, and neither renders the other's canvas, unlike the
reverted case. The post-load delay's cause is still unknown, though. If menus get slow after a
load again, suspect this shared pose first.

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
- `HDAdditionalCameraData.customRender` is an event. The interop exposes it as `add_customRender`,
  which takes a managed `Action<ScriptableRenderContext, HDCamera>` directly. `FilteringSettings`'
  constructor wants an `Il2CppSystem.Nullable<RenderQueueRange>`, not the bare struct.

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
  geometry by the eye cameras (here: the VR Settings panel and the other not-yet-migrated canvases)
  still gets DoF, TAA and the rest.
- **Text resolution is bounded by the eye buffer.** The panel is resampled into the eye RT (rendered
  at 0.7× the headset's recommended resolution by default here), then again by the compositor's
  lens distortion. The fix is to hand the panel to the headset compositor as a quad layer: see
  "Sharper panels: OpenXR quad layers" below.

## Sharper panels: OpenXR quad layers

A panel drawn by the overlay pass is resampled twice: into the eye RT, at the eye buffer's
resolution, and again when the compositor warps the eye image. Submitted instead as an
`XrCompositionLayerQuad`, the panel's own full-resolution texture is sampled once, by the
compositor, at display resolution. Text becomes clearly crisper. This is also post-FX-immune by
construction, since HDRP never touches a layer.

**Verified in the headset on 2026-09-27** (Quest 3 over Virtual Desktop) with the loading screen,
which the stall frames (`StallFrames`) submit as a quad layer during main-thread freezes. The user
saw the loading text turn crisp exactly when the stall frames took over. The layer's orientation,
placement, size and depth matched the overlay-drawn panel, and the panel layer covered both the menu
panel and the press-any-key screen's cropped view.

```
per frame, per panel shown as a layer:
  Blit(panelRT -> flippedRT, scale (1,-1), offset (0,1))  // Unity row order -> OpenXR row order
  acquire/wait swapchain image, CopyResource(flippedRT -> image), release
  write XrCompositionLayerQuad { swapchain, imageRect, pose, size, flags }
  add it to xrEndFrame's layers, after the eyes' projection layer
```

### Details that matter

- **Flip the texture on the way.** The panel RTs are rendered by ordinary projector cameras, so
  they hold Unity's row order; OpenXR reads images top row first. Blit with a vertical flip into a
  same-size RT, then copy that into the swapchain. (The eye RTs don't need this: their cameras use
  `ForceFlipY`.) The blit also drops the panel RT's mip chain, which `CopyResource` requires to
  match the swapchain's single mip.
- **One swapchain per panel texture shown in a frame.** Several views of one canvas (the case
  board, tooltips) can share one copy: each quad points at a different `imageRect` of the same
  swapchain. The image rect counts rows from the top of the flipped copy:
  `y = textureHeight - pixelRect.yMax`.
- **Pose: rig-local, Z flipped.** `CameraRig.RigToXr` maps a rig-local point or direction into the
  OpenXR reference space (the inverse of `ApplyCameraPose`). A quad layer's +X/+Y are the image's
  right/up and its +Z faces the viewer; a Unity quad is seen from its -Z side. Build the rotation
  from rig-local right, up and `-forward` with `CameraRig.XrQuadOrientation`. Right-up-toward-viewer
  is left-handed in Unity, and the Z flip turns it into a proper right-handed rotation. The layer's
  size is the quad's world scale.
- **Alpha.** Set `XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT` (0x2) and leave the
  unpremultiplied bit off: the panel textures are already premultiplied.
- **Keep the mod's copies alive across loads.** The flipped RTs are referenced by nothing in a scene,
  so a load's unused-asset unload destroys them unless they have
  `hideFlags = HideFlags.DontUnloadUnusedAsset` (see v1_findings.md §1).
- **Swapchain access and the stall thread.** The stall frames' thread submits the same swapchains
  during freezes, so acquire/copy/release happens under `StallFrames.Gate`.

### Layers draw over the eyes: ordering is the catch

Layers composite in submission order, on top of the eyes' projection layer, with no depth test
between them. Anything that must appear in front of a layered panel has to be a layer too, submitted
after it:

- **Panels:** the panel bands already define the order (`Normal`, then `Menu`, then `Top`). A panel
  that stays in the eye image is always under every layered one. Layering the menu means layering
  every `Top` panel above it too: popups, tooltips, the keyboard and VR Settings.
- **The lasers** are drawn into the eye image, so they'd go under a layered panel. They need a
  transparent projection layer of their own on top: the overlay pass drawing only lasers into cleared
  (0,0,0,0) RTs, with the blend bit set.
- **World dots** stay in the eye image, under every panel, as they are now.
- **The monitor mirror** shows the left eye RT, which no longer contains layered panels. It needs
  its own copy of the world image with the full overlay composited on it.

The main-frame version (menu and `Top` panels as quad layers every frame, lasers in a top layer) is
being built on this basis. Its ordering and the laser layer are not yet verified in the headset.

## Verifying it works

- The menu text is upright and not mirrored (tests the `FlipY` choice).
- The laser tip lands exactly on the button whose hover highlight lights up (the overlay matrices
  agree with the collider geometry).
- The panel fuses at one depth in stereo, with no double image (per-eye view/projection are right).
- With depth of field on, the world behind the menu is blurred and the panel is sharp.
- Turning your head quickly while reading leaves no smear or ghosting on text edges (TAA doesn't
  reach it).
- The laser is a thin line with no bloom halo, in both bright and dark scenes.
