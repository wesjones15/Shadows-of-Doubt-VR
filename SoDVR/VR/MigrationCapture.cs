using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace SoDVR.VR;

/// <summary>
/// TEMPORARY step-0 capture for moving dialogue and the map onto RT panels: which game calls
/// end a conversation, and how the option list and the map window are laid out. Removed once
/// those panels are built.
/// </summary>
internal static class MigrationCapture
{
    private static ManualLogSource Log => Plugin.Log;

    private const int OptionsSettleFrames = 15;
    private const int MapCursorLogFrames = 60;

    private static bool _dialogMode;
    private static int _dialogLogAt = -1;
    private static bool _mapShowing;
    private static int _mapLogAt = -1;
    private static int _mapCursorCountdown;

    public static void Tick(int frame)
    {
        try { TickDialogue(frame); } catch (Exception ex) { Log.LogWarning($"[Capture] dialogue: {ex.Message}"); }
        try { TickMap(frame); } catch (Exception ex) { Log.LogWarning($"[Capture] map: {ex.Message}"); }
    }

    private static void TickDialogue(int frame)
    {
        var ic = InteractionController.Instance;
        if (ic == null) return;
        bool mode = ic.dialogMode;
        if (mode != _dialogMode)
        {
            _dialogMode = mode;
            var group = InterfaceController.Instance?.dialogCanvasGroup;
            Log.LogInfo($"[Capture] dialogMode → {mode} type={ic.dialogType} canvasGroupAlpha={(group != null ? group.alpha : -1f):F2}");
            if (mode) _dialogLogAt = frame + OptionsSettleFrames;
        }
        if (frame != _dialogLogAt) return;

        var options = ic.dialogOptions;
        int count = options?.Count ?? 0;
        Log.LogInfo($"[Capture] dialog options={count} selection={ic.dialogSelection} " +
                    $"scrollUp={Describe(ic.moreOptionsScrollUpArrow)} scrollDown={Describe(ic.moreOptionsScrollDownArrow)}");
        for (int i = 0; i < count; i++)
        {
            var option = options![i];
            if (option == null) continue;
            var type = option.GetIl2CppType();
            var scroll = option.GetComponentInParent<ScrollRect>();
            var button = option.GetComponent<Button>();
            Log.LogInfo($"[Capture]   [{i}] path='{PathOf(option.transform)}' type={type.Name} base={type.BaseType?.Name} " +
                        $"active={option.gameObject.activeInHierarchy} button={(button != null ? $"persistent={button.onClick.GetPersistentEventCount()}" : "none")} " +
                        $"scrollRect={(scroll != null ? PathOf(scroll.transform) : "none")}");
        }

        var bubbles = UnityEngine.Object.FindObjectsOfType<SpeechBubbleController>();
        foreach (var bubble in bubbles)
        {
            var canvas = bubble.GetComponentInParent<Canvas>();
            Log.LogInfo($"[Capture] speech bubble '{PathOf(bubble.transform)}' canvas='{(canvas != null ? canvas.rootCanvas.name : "none")}'");
        }
    }

    private static void TickMap(int frame)
    {
        var ifc = InterfaceController.Instance;
        var map = MapController.Instance;
        if (ifc == null || map == null || ifc.minimapCanvasGroup == null) return;

        bool showing = ifc.minimapCanvasGroup.alpha > 0.01f;
        if (showing != _mapShowing)
        {
            _mapShowing = showing;
            Log.LogInfo($"[Capture] map showing → {showing} firstPerson={map.displayFirstPerson} showDesktopMap={ifc.showDesktopMap}");
            if (showing) _mapLogAt = frame + OptionsSettleFrames;
        }

        if (frame == _mapLogAt)
        {
            var zc = map.zoomController;
            Log.LogInfo($"[Capture] map openProgress={map.openProgress:F2} savedSize={map.savedSize:F2} " +
                        $"viewport={Describe(map.viewport)} content={Describe(map.contentRect)} " +
                        $"contentAnchored={map.contentRect.anchoredPosition} " +
                        $"zoom={zc.zoom:F3} desired={zc.desiredZoom:F3} limit={zc.zoomLimit} normalSize={zc.normalSize}");
            for (var t = map.viewport.transform; t != null; t = t.parent)
                Log.LogInfo($"[Capture]   up '{t.name}' {Describe(t.GetComponent<RectTransform>())} " +
                            $"canvas={t.GetComponent<Canvas>() != null} raycaster={t.GetComponent<GraphicRaycaster>() != null}");
            LogRaycasters(map.contentCanvas);
        }

        if (!showing || --_mapCursorCountdown > 0) return;
        _mapCursorCountdown = MapCursorLogFrames;
        var node = map.mapCursorNode;
        Log.LogInfo($"[Capture] map cursorPos={map.cursorPos} cursorNode={(node != null ? node.nodeCoord.ToString() : "null")} " +
                    $"anchored={map.contentRect.anchoredPosition} zoom={map.zoomController.zoom:F3}");
    }

    private static void LogRaycasters(Canvas? contentCanvas)
    {
        if (contentCanvas == null) return;
        var raycasters = contentCanvas.GetComponentsInChildren<GraphicRaycaster>(true);
        Log.LogInfo($"[Capture] map contentCanvas '{contentCanvas.name}' raycasters in subtree={raycasters.Length}");
        int shown = 0;
        foreach (var r in raycasters)
        {
            if (shown++ >= 5) break;
            Log.LogInfo($"[Capture]   raycaster on '{PathOf(r.transform)}'");
        }
    }

    private static string Describe(RectTransform? rt)
    {
        if (rt == null) return "null";
        var r = rt.rect;
        return $"'{rt.name}'({r.width:F0}x{r.height:F0} active={rt.gameObject.activeInHierarchy})";
    }

    private static string PathOf(Transform t)
    {
        string path = t.name;
        for (var p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
        return path;
    }

    // ── Game calls ─────────────────────────────────────────────────────────────────────────

    [HarmonyPatch(typeof(InteractionController), nameof(InteractionController.SetDialog))]
    private static class SetDialogPatch
    {
        private static void Prefix(bool __0) => Log.LogInfo($"[Capture] call InteractionController.SetDialog({__0})");
    }

    [HarmonyPatch(typeof(InteractionController), nameof(InteractionController.SetDialogSelection))]
    private static class SetDialogSelectionPatch
    {
        private static void Prefix(int __0) => Log.LogInfo($"[Capture] call InteractionController.SetDialogSelection({__0})");
    }

    [HarmonyPatch(typeof(ActionController), nameof(ActionController.Return))]
    private static class ReturnPatch
    {
        private static void Prefix() => Log.LogInfo("[Capture] call ActionController.Return");
    }

    [HarmonyPatch(typeof(ActionController), nameof(ActionController.TalkTo))]
    private static class TalkToPatch
    {
        private static void Prefix() => Log.LogInfo("[Capture] call ActionController.TalkTo");
    }

    [HarmonyPatch(typeof(MapController), nameof(MapController.OpenMap))]
    private static class OpenMapPatch
    {
        private static void Prefix(bool __0, bool __1) => Log.LogInfo($"[Capture] call MapController.OpenMap({__0}, {__1})");
    }

    [HarmonyPatch(typeof(MapController), nameof(MapController.CloseMap))]
    private static class CloseMapPatch
    {
        private static void Prefix(bool __0) => Log.LogInfo($"[Capture] call MapController.CloseMap({__0})");
    }
}
