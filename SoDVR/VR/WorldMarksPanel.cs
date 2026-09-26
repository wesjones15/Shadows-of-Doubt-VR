using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// The game's world-tied marks — objective pointers, overheard speech bubbles and NPC reaction
/// indicators — shown at the things they belong to. The game places them on screen through its
/// own camera, which in VR follows the left controller, and on our projected canvas that lands
/// them far off the texture. So their containers move onto a transparent canvas of our own; before
/// each render every mark is laid into its own slot on that texture, and a small view of it is set
/// at its target in the world, facing the player at the HUD's apparent size. An objective pointer
/// whose target is out of view clamps to the edge of the HUD sheet and points the way, as the
/// flat game clamps it to the screen edge.
/// </summary>
internal sealed class WorldMarksPanel
{
    private static ManualLogSource Log => Plugin.Log;

    public const string CanvasName = "SoDVR_WorldMarksCanvas";
    private const int DiscoveryRetryFrames = 90;
    private const int ContainerScanFrames = 60;
    private const float MarginPixels = 6f;
    private const int SlotColumns = 3;
    private const int SlotRows = 3;
    private const float SpeechAboveHeadMeters = 0.35f;
    private const float ReactionAboveHeadMeters = 0.5f;
    // The game draws reaction indicators about 13 pixels across — unreadable at HUD scale in VR.
    private const float ReactionScale = 2f;
    // A pointer target inside this much of the eye's viewport counts as in view.
    private const float InViewMargin = 0.05f;
    // How far from the sheet's centre an off-view pointer sits, as a fraction of the sheet.
    private const float EdgeClamp = 0.45f;

    private enum MarkKind { Pointer, Speech, Reaction }

    private sealed class Mark
    {
        public Mark(RTPanelView view) { View = view; }
        public readonly RTPanelView View;
        public bool Seen;
    }

    private readonly RTCanvasPanel _panel;
    private readonly List<Transform> _containers = new();
    private readonly Dictionary<int, Mark> _marks = new();
    private readonly HashSet<string> _loggedKinds = new();
    private readonly Dictionary<int, Vector3> _enlargedScales = new();
    private Canvas? _canvas;
    private int _discoveryCooldown;
    private int _containerScan;

    public WorldMarksPanel(int quadLayer, RTPanelInput input)
    {
        _panel = new RTCanvasPanel("WorldMarks", quadLayer, input);
    }

    public void Tick(HudRTPanels hud)
    {
        if (_panel.IsAttached && _containers.TrueForAll(c => c != null)) return;
        if (_panel.IsAttached) Teardown();
        if (--_discoveryCooldown > 0) return;
        _discoveryCooldown = DiscoveryRetryFrames;
        TryDiscover(hud);
    }

    /// <summary>Lays out and places every mark. Runs after the game's own Updates have positioned
    /// them, just before the render.</summary>
    public void BeforeRender(HudRTPanels hud, Camera? head, bool boardOpen)
    {
        if (!_panel.IsAttached || _panel.Texture == null) return;
        foreach (var mark in _marks.Values) mark.Seen = false;

        bool showing = head != null && hud.IsShowing && !boardOpen;
        if (showing)
        {
            if (--_containerScan <= 0) { _containerScan = ContainerScanFrames; AdoptReactionContainers(); }
            int slot = 0;
            foreach (var container in _containers)
                for (int i = 0; i < container.childCount; i++)
                {
                    var child = container.GetChild(i);
                    if (child.gameObject.activeInHierarchy && Place(child, slot, hud, head!)) slot++;
                }
        }

        var gone = new List<int>();
        foreach (var (id, mark) in _marks)
        {
            mark.View.Visible = mark.Seen;
            if (!mark.Seen && !IsAlive(id)) gone.Add(id);
        }
        foreach (var id in gone) { _panel.DestroyView(_marks[id].View); _marks.Remove(id); _enlargedScales.Remove(id); }
    }

    public void Render()
    {
        _panel.Render();
        ProbeRenderedMarks();
    }
    public void AppendOverlay(PostFXOverlayCompositor overlay) => _panel.AppendOverlay(overlay);

    /// <summary>Puts one mark in texture slot <paramref name="slot"/> and its view at its target.
    /// False if it isn't a mark or has nothing to show.</summary>
    private bool Place(Transform markTransform, int slot, HudRTPanels hud, Camera head)
    {
        var rect = markTransform.GetComponent<RectTransform>();
        if (rect == null || slot >= SlotColumns * SlotRows) return false;

        MarkKind kind;
        Vector3? target;
        bool isPlayerSpeech = false;
        var pointer = markTransform.GetComponent<UIPointerController>();
        var speech = pointer == null ? markTransform.GetComponent<SpeechBubbleController>() : null;
        var reaction = pointer == null && speech == null ? markTransform.GetComponent<ReactionIndicatorController>() : null;
        if (pointer != null) { kind = MarkKind.Pointer; target = PointerTarget(pointer); }
        else if (speech != null) { kind = MarkKind.Speech; isPlayerSpeech = speech.isPlayer; target = HeadOf(speech.speechController?.actor, SpeechAboveHeadMeters); }
        else if (reaction != null) { kind = MarkKind.Reaction; target = HeadOf(reaction.actor, ReactionAboveHeadMeters); }
        else return false;

        // An off-view pointer turns to point the way, before its crop is measured.
        var texture = _panel.Texture!;
        Vector2? edge = null;
        rect.localRotation = Quaternion.identity;
        if (pointer != null)
        {
            ShowPointer(pointer);
            if (target != null && !InView(head, target.Value, out var viewport))
            {
                var fromCentre = viewport - new Vector2(0.5f, 0.5f);
                float furthest = Mathf.Max(Mathf.Abs(fromCentre.x), Mathf.Abs(fromCentre.y));
                if (furthest > 0.001f) fromCentre *= EdgeClamp / furthest;
                rect.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(fromCentre.y, fromCentre.x) * Mathf.Rad2Deg - 90f);
                edge = new Vector2((0.5f + fromCentre.x) * texture.width, (0.5f + fromCentre.y) * texture.height);
            }
        }
        if (reaction != null) EnlargeOnTexture(rect, markTransform.GetInstanceID());
        PutInSlot(rect, slot);

        var pixels = _panel.ContentPixelRect(markTransform, MarginPixels, out int graphics);
        if (graphics == 0) return false;

        int id = markTransform.GetInstanceID();
        bool created = !_marks.TryGetValue(id, out var mark);
        if (created)
        {
            mark = new Mark(_panel.CreateView($"{kind}/{markTransform.name}", interactive: false));
            _marks[id] = mark;
        }
        mark.View.SetPixelRect(pixels);
        mark.Seen = true;

        if (isPlayerSpeech || target == null)
        {
            // The player's own lines, and anything without a target, go low on the HUD sheet.
            mark.Seen = hud.PlaceOnSheet(mark.View, new Vector2(0.5f * texture.width, 0.25f * texture.height));
        }
        else if (edge != null)
        {
            mark.Seen = hud.PlaceOnSheet(mark.View, edge.Value);
        }
        else
        {
            var headPos = head.transform.position;
            float distance = Mathf.Max(0.3f, Vector3.Distance(headPos, target.Value));
            mark.View.Scale = HudRTPanels.SheetMetersPerPixelAt(distance, texture.width) / _panel.MetersPerPixel;
            mark.View.SetPose(target.Value, Quaternion.LookRotation(target.Value - headPos));
        }

        if (_loggedKinds.Add($"{kind}:{isPlayerSpeech}"))
            Log.LogInfo($"[WorldMarks] First {kind}{(isPlayerSpeech ? " (player)" : "")} '{markTransform.name}' target={target?.ToString() ?? "none"} crop={pixels}" +
                        (reaction != null ? $" sprite='{reaction.img?.sprite?.name}'" : ""));
        if (created && _probesLeft > 0)
        {
            _probesLeft--;
            _probes.Enqueue(($"{kind} '{markTransform.name}'", pixels, mark.View, head.transform.position));
        }
        return true;
    }

    // ── Diagnostics (temporary: do the marks reach the texture, and where are their views?) ────

    private int _probesLeft = 6;
    private readonly Queue<(string label, Rect crop, RTPanelView view, Vector3 head)> _probes = new();

    private void ProbeRenderedMarks()
    {
        while (_probes.Count > 0)
        {
            var (label, crop, view, head) = _probes.Dequeue();
            try
            {
                int w = Mathf.Max(1, (int)crop.width), h = Mathf.Max(1, (int)crop.height);
                var readback = new Texture2D(w, h, TextureFormat.RGBA32, false);
                var previous = RenderTexture.active;
                try
                {
                    RenderTexture.active = _panel.Texture;
                    readback.ReadPixels(new Rect(crop.x, crop.y, w, h), 0, 0);
                    readback.Apply();
                }
                finally { RenderTexture.active = previous; }
                var pixels = readback.GetPixels32();
                int drawn = 0;
                for (int i = 0; i < pixels.Length; i++) if (pixels[i].a > 8) drawn++;
                UnityEngine.Object.Destroy(readback);

                var position = view.Transform.position;
                Log.LogInfo($"[WorldMarks] Probe {label}: drawn={drawn}/{pixels.Length} px in crop {crop}; view visible={view.Visible} " +
                            $"at {position} ({Vector3.Distance(head, position):F2} m from head) size={view.WorldSize}");
            }
            catch (Exception ex) { Log.LogWarning($"[WorldMarks] Probe {label}: {ex.Message}"); }
        }
    }

    /// <summary>Draws a reaction indicator bigger on the texture, so it stays sharp shown bigger. The
    /// game resizes indicators by distance whenever it updates them: enlarge what it last set, once.</summary>
    private void EnlargeOnTexture(RectTransform rect, int id)
    {
        if (_enlargedScales.TryGetValue(id, out var applied) && rect.localScale == applied) return;
        rect.localScale *= ReactionScale;
        _enlargedScales[id] = rect.localScale;
    }

    /// <summary>Moves the mark so its visible content is centred in slot <paramref name="slot"/> on the
    /// texture — wherever the game tried to put it. Centring the content rather than the pivot
    /// matters: a reaction indicator's pivot sits at its edge, and it overran its slot.</summary>
    private void PutInSlot(RectTransform rect, int slot)
    {
        var texture = _panel.Texture!;
        var camera = _panel.ProjectorCamera!;
        float cellW = (float)texture.width / SlotColumns, cellH = (float)texture.height / SlotRows;
        var centre = new Vector2((slot % SlotColumns + 0.5f) * cellW, (slot / SlotColumns + 0.5f) * cellH);
        rect.position = camera.ScreenToWorldPoint(new Vector3(centre.x, centre.y, _canvas!.planeDistance));
        if (!TryContentBounds(rect, out var bounds)) return;
        var pivot = camera.WorldToScreenPoint(rect.position);
        var shift = centre - bounds.center;
        rect.position = camera.ScreenToWorldPoint(new Vector3(pivot.x + shift.x, pivot.y + shift.y, pivot.z));
    }

    /// <summary>Where the mark's shown graphics actually land on the texture, including any part off it.</summary>
    private bool TryContentBounds(Transform mark, out Rect bounds)
    {
        bounds = default;
        bool any = false;
        foreach (var graphic in mark.GetComponentsInChildren<UnityEngine.UI.Graphic>(false))
        {
            if (graphic == null || !graphic.enabled) continue;
            var r = _panel.UnclampedPixelRectOf(graphic.rectTransform);
            bounds = any ? Rect.MinMaxRect(Mathf.Min(bounds.xMin, r.xMin), Mathf.Min(bounds.yMin, r.yMin),
                                           Mathf.Max(bounds.xMax, r.xMax), Mathf.Max(bounds.yMax, r.yMax)) : r;
            any = true;
        }
        return any;
    }

    private static bool InView(Camera head, Vector3 target, out Vector2 viewport)
    {
        var vp = head.WorldToViewportPoint(target);
        if (vp.z < 0f) vp = new Vector3(1f - vp.x, 1f - vp.y, vp.z); // behind: the opposite side
        viewport = new Vector2(vp.x, vp.y);
        return vp.z > 0f && vp.x > InViewMargin && vp.x < 1f - InViewMargin && vp.y > InViewMargin && vp.y < 1f - InViewMargin;
    }

    private static Vector3? PointerTarget(UIPointerController pointer)
    {
        try
        {
            var element = pointer.objective?.queueElement;
            if (element != null && element.usePointer) return element.pointerPosition;
        }
        catch { }
        return null;
    }

    private static Vector3? HeadOf(Actor? actor, float above)
    {
        try
        {
            var look = actor?.lookAtThisTransform;
            if (look != null) return look.position + Vector3.up * above;
        }
        catch { }
        return null;
    }

    /// <summary>The game fades a pointer by its own camera's view; here it's placed by the head.</summary>
    private static void ShowPointer(UIPointerController pointer)
    {
        try
        {
            pointer.fadeIn = 1f;
            if (pointer.img != null) pointer.img.enabled = true;
            if (pointer.rend != null) pointer.rend.SetAlpha(1f);
        }
        catch { }
    }

    /// <summary>Reaction indicators turned up in no known container: that container moves over too.</summary>
    private void AdoptReactionContainers()
    {
        foreach (var reaction in Resources.FindObjectsOfTypeAll<ReactionIndicatorController>())
        {
            if (reaction == null || !reaction.gameObject.scene.IsValid()) continue;
            var parent = reaction.transform.parent;
            if (parent == null || _containers.Contains(parent) || parent == _canvas!.transform) continue;
            Log.LogInfo($"[WorldMarks] Reaction indicators live under '{parent.name}' — moved onto the marks canvas");
            parent.SetParent(_canvas.transform, false);
            _containers.Add(parent);
        }
    }

    private bool IsAlive(int id)
    {
        foreach (var container in _containers)
            for (int i = 0; i < container.childCount; i++)
                if (container.GetChild(i).GetInstanceID() == id) return true;
        return false;
    }

    private void TryDiscover(HudRTPanels hud)
    {
        var hudCanvas = hud.Canvas;
        if (hudCanvas == null) return;
        try
        {
            var pointers = InterfaceController.Instance?.uiPointerContainer;
            var speech = InterfaceControls.Instance?.speechBubbleParent;
            if (pointers == null || speech == null) return;
            // Conversation subtitles belong to ConversationSpeechPanel.
            if (speech == InterfaceController.Instance!.speechDisplayAnchor)
            {
                Log.LogWarning("[WorldMarks] Speech bubble parent is the conversation subtitle anchor — not taking it");
                return;
            }

            if (_canvas == null) _canvas = ModCanvas.CreateLike(hudCanvas, CanvasName);
            _containers.Clear();
            foreach (var container in new[] { pointers, speech })
            {
                if (container.parent != _canvas.transform) container.SetParent(_canvas.transform, false);
                _containers.Add(container);
            }

            _panel.Attach(_canvas, HudRTPanels.ScreenWorldWidth, transparent: true);
            _containerScan = 0;
            Log.LogInfo($"[WorldMarks] '{pointers.name}' and '{speech.name}' on a transparent RT panel.");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[WorldMarks] Setup failed: {ex.Message}");
            Teardown();
        }
    }

    private void Teardown()
    {
        _panel.Detach();
        _marks.Clear();
        _containers.Clear();
        Log.LogInfo("[WorldMarks] Containers gone (scene reload?) — RT panel torn down, will rediscover.");
    }
}
