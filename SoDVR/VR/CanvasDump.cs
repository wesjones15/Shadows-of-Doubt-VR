using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace SoDVR.VR;

/// <summary>
/// The F9 diagnostic dump: enumerates every Canvas that currently exists in the scene, not just
/// the ones this mod already knows to manage — so an unfamiliar or not-yet-wired-up canvas (e.g.
/// whatever backs the Inventory tab, or a case-board Note/Notebook window nested under
/// WindowCanvas) can be identified by name/category/nesting from LogOutput.log instead of guessed.
/// </summary>
internal static class CanvasDump
{
    private static BepInEx.Logging.ManualLogSource Log => Plugin.Log;

    public static void DumpAll()
    {
        Log.LogInfo("[CanvasDump] === CANVAS DIAGNOSTIC DUMP ===");
        Canvas[] all;
        try { all = Resources.FindObjectsOfTypeAll<Canvas>(); }
        catch (Exception ex) { Log.LogWarning($"[CanvasDump] FindObjectsOfTypeAll failed: {ex.Message}"); return; }

        var ordered = all.Where(c => c != null)
                         .OrderBy(c => c.gameObject.name, StringComparer.OrdinalIgnoreCase)
                         .ToList();
        Log.LogInfo($"[CanvasDump] {ordered.Count} Canvas components in scene.");
        foreach (var c in ordered) DumpOne(c);
        Log.LogInfo("[CanvasDump] === END CANVAS DUMP ===");
    }

    private static void DumpOne(Canvas c)
    {
        string name = c.gameObject.name;
        bool active = c.gameObject.activeInHierarchy;
        CanvasCategory cat = CanvasCategoryInfo.GetCanvasCategory(name);
        var rt = c.GetComponent<RectTransform>();
        string size = rt != null ? $"{rt.sizeDelta.x:F0}x{rt.sizeDelta.y:F0}" : "n/a";
        bool hasRaycaster = c.GetComponent<GraphicRaycaster>() != null;
        string parent = c.transform.parent != null ? c.transform.parent.name : "(scene root)";
        string cam = c.worldCamera != null ? c.worldCamera.name : "null";
        int graphicCount = 0;
        try { graphicCount = c.GetComponentsInChildren<Graphic>(true).Count(g => g != null && g.gameObject.activeInHierarchy); }
        catch { }
        Log.LogInfo($"[CanvasDump] '{name}' active={active} root={c.isRootCanvas} mode={c.renderMode} " +
                    $"category={cat} size={size} parent='{parent}' hasRaycaster={hasRaycaster} " +
                    $"worldCam={cam} activeGraphics={graphicCount}");
    }
}
