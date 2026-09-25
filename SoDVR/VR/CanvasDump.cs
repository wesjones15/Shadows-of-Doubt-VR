using System;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace SoDVR.VR;

/// <summary>
/// The F9 diagnostic dump: enumerates every Canvas that currently exists in the scene, not just
/// the ones this mod already knows to manage — so an unfamiliar or not-yet-wired-up canvas (e.g.
/// whatever backs the Inventory tab, or a case-board Note/Notebook window nested under
/// WindowCanvas) can be identified by name/category/nesting from LogOutput.log instead of guessed.
///
/// The case-board section exists to settle, from logs rather than inference, which signal means
/// "case board open", how each case-board canvas hides itself (SetActive vs CanvasGroup), where
/// each canvas's real content sits within its 1920x1080 rect, and what TooltipCanvas's dialog /
/// context-menu / tooltip / quick-menu elements are called.
/// </summary>
internal static class CanvasDump
{
    private static BepInEx.Logging.ManualLogSource Log => Plugin.Log;

    private static readonly string[] CaseBoardCanvasNames =
    {
        "ActionPanelCanvas", "CaseCanvas", "BioDisplayCanvas", "LocationDetailsCanvas",
        "UpgradesDisplayCanvas", "WindowCanvas", "TooltipCanvas",
    };

    private const int TooltipDescendantDepth = 4;

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

        DumpCaseBoardState(ordered.ToArray());
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
                    $"worldCam={cam} activeGraphics={graphicCount} enabled={c.enabled} " +
                    $"canvasGroups=[{DescribeCanvasGroupChain(c.transform)}]");
    }

    private static void DumpCaseBoardState(Canvas[] all)
    {
        Log.LogInfo("[CanvasDump] --- case-board state ---");
        try
        {
            var ic = InterfaceController.Instance;
            Log.LogInfo(ic != null
                ? $"[CanvasDump] InterfaceController.desktopMode={ic.desktopMode}"
                : "[CanvasDump] InterfaceController.Instance=null");
        }
        catch (Exception ex) { Log.LogWarning($"[CanvasDump] desktopMode read: {ex.Message}"); }

        foreach (var name in CaseBoardCanvasNames)
        {
            var canvas = all.FirstOrDefault(c => c.transform.parent == null && c.gameObject.name == name)
                      ?? all.FirstOrDefault(c => c.gameObject.name == name);
            if (canvas == null) { Log.LogInfo($"[CanvasDump] '{name}': not found"); continue; }

            int depth = name == "TooltipCanvas" ? TooltipDescendantDepth : 1;
            Log.LogInfo($"[CanvasDump] '{name}' children (canvas-local rect = xMin,yMin,w,h; canvas rect {DescribeRect(canvas.GetComponent<RectTransform>())}):");
            DumpChildren(canvas, canvas.transform, depth, 1);
        }
    }

    private static void DumpChildren(Canvas canvas, Transform parent, int maxDepth, int depth)
    {
        string indent = new string(' ', depth * 2);
        for (int i = 0; i < parent.childCount; i++)
        {
            var child = parent.GetChild(i);
            if (child == null) continue;
            bool activeSelf = child.gameObject.activeSelf;
            var rt = child.GetComponent<RectTransform>();
            string rect = rt != null ? CanvasLocalRect(canvas, rt) : "n/a";
            Log.LogInfo($"[CanvasDump] {indent}'{child.gameObject.name}' activeSelf={activeSelf} " +
                        $"rect={rect} components=[{DescribeComponents(child)}] " +
                        $"canvasGroup={DescribeCanvasGroup(child)}");
            if (activeSelf && depth < maxDepth) DumpChildren(canvas, child, maxDepth, depth + 1);
        }
    }

    private static string DescribeCanvasGroupChain(Transform t)
    {
        var sb = new StringBuilder();
        for (var tr = t; tr != null; tr = tr.parent)
        {
            var cg = tr.GetComponent<CanvasGroup>();
            if (cg == null) continue;
            if (sb.Length > 0) sb.Append("; ");
            sb.Append($"'{tr.gameObject.name}' {DescribeCanvasGroup(tr)}");
        }
        return sb.ToString();
    }

    private static string DescribeCanvasGroup(Transform t)
    {
        var cg = t.GetComponent<CanvasGroup>();
        return cg == null
            ? "none"
            : $"alpha={cg.alpha:F2} interactable={cg.interactable} blocksRaycasts={cg.blocksRaycasts}";
    }

    private static string DescribeComponents(Transform t)
    {
        try
        {
            return string.Join(",", t.GetComponents<Component>()
                .Where(c => c != null)
                .Select(c => c.GetIl2CppType().Name)
                .Where(n => n != "RectTransform" && n != "CanvasRenderer"));
        }
        catch { return "?"; }
    }

    private static string DescribeRect(RectTransform? rt)
    {
        if (rt == null) return "n/a";
        var r = rt.rect;
        return $"({r.xMin:F0},{r.yMin:F0},{r.width:F0},{r.height:F0})";
    }

    /// <summary>The child's own rect expressed in the root canvas's local space — for a canvas
    /// with scaleFactor 1 that is canvas pixels, centred on the canvas pivot.</summary>
    private static string CanvasLocalRect(Canvas canvas, RectTransform rt)
    {
        var r = RootLocalRect(canvas, rt);
        return $"({r.xMin:F0},{r.yMin:F0},{r.width:F0},{r.height:F0})";
    }

    private static Rect RootLocalRect(Canvas canvas, RectTransform rt)
    {
        var r = rt.rect;
        var ct = canvas.transform;
        Vector3 a = ct.InverseTransformPoint(rt.TransformPoint(new Vector3(r.xMin, r.yMin, 0f)));
        Vector3 b = ct.InverseTransformPoint(rt.TransformPoint(new Vector3(r.xMax, r.yMax, 0f)));
        return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
    }
}
