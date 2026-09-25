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

        DumpContextMenuControllerMembers();
        DumpInventoryCloseButton();
    }

    /// <summary>The inventory's X (BioScreenController.closeButton), which is missing from its RT
    /// panel, with the inventory's own state.</summary>
    public static void DumpInventoryCloseButton()
    {
        try
        {
            var bio = BioScreenController.Instance;
            if (bio == null) { Log.LogInfo("[CanvasDump] BioScreenController.Instance=null"); return; }
            Log.LogInfo($"[CanvasDump] BioScreenController isOpen={bio.isOpen} openedFromPause={bio.openedFromPause} " +
                        $"inventoryDisplayProgress={bio.inventoryDisplayProgress:F2}");
            DumpButton("inventory X", bio.closeButton != null ? bio.closeButton.transform : null);
        }
        catch (Exception ex) { Log.LogWarning($"[CanvasDump] Inventory close button: {ex.Message}"); }
    }

    /// <summary>
    /// Everything that decides whether a button is seen and hit in its RT panel: where it sits
    /// (and the layout chain that put it there), every way it could be hidden (inactive, faded,
    /// culled, masked), what can hit-test it (the raycasters of its canvases, CanvasGroups blocking
    /// raycasts), and which graphics are drawn over it in the game's own draw order.
    /// </summary>
    public static void DumpButton(string label, Transform? t)
    {
        if (t == null) { Log.LogInfo($"[CanvasDump] {label}: null"); return; }
        try
        {
            Canvas? owner = null;
            var path = new StringBuilder();
            for (var tr = t; tr != null; tr = tr.parent)
            {
                path.Insert(0, tr == t ? tr.gameObject.name : tr.gameObject.name + "/");
                if (owner == null) owner = tr.GetComponent<Canvas>();
            }
            var root = owner != null ? owner.rootCanvas : null;
            var rt = t.GetComponent<RectTransform>();
            Log.LogInfo($"[CanvasDump] {label}: path='{path}' activeInHierarchy={t.gameObject.activeInHierarchy} " +
                        $"ownerCanvas='{owner?.gameObject.name}' nested={(owner != null && !owner.isRootCanvas)} " +
                        $"overrideSorting={owner?.overrideSorting} sortingOrder={owner?.sortingOrder} rootCanvas='{root?.gameObject.name}' " +
                        $"rectInRoot={(root != null && rt != null ? CanvasLocalRect(root, rt) : "n/a")} rootRect={DescribeRect(root?.GetComponent<RectTransform>())} " +
                        $"raycasters=[{DescribeRaycasters(t)}] canvasGroups=[{DescribeCanvasGroupChain(t)}] masks=[{DescribeMasks(t)}]");

            int maxDepth = int.MinValue;
            foreach (var g in t.GetComponentsInChildren<Graphic>(true))
            {
                if (g == null) continue;
                var cr = g.canvasRenderer;
                if (g.gameObject.activeInHierarchy && g.enabled) maxDepth = Math.Max(maxDepth, g.depth);
                Log.LogInfo($"[CanvasDump]   graphic '{g.gameObject.name}' {g.GetIl2CppType().Name} activeInHierarchy={g.gameObject.activeInHierarchy} " +
                            $"enabled={g.enabled} colorA={g.color.a:F2} rendererA={cr.GetAlpha():F2} inheritedA={cr.GetInheritedAlpha():F2} " +
                            $"cull={cr.cull} raycastTarget={g.raycastTarget} depth={g.depth} " +
                            $"rect={(root != null ? CanvasLocalRect(root, g.rectTransform) : "n/a")}");
            }

            for (var tr = t; tr != null; tr = tr.parent)
            {
                var r = tr.GetComponent<RectTransform>();
                if (r != null)
                    Log.LogInfo($"[CanvasDump]   layout '{tr.gameObject.name}' anchorMin={r.anchorMin} anchorMax={r.anchorMax} pivot={r.pivot} " +
                                $"anchoredPosition={r.anchoredPosition} sizeDelta={r.sizeDelta} localPosition={r.localPosition} localScale={r.localScale}");
                if (root != null && tr == root.transform) break;
            }

            if (root != null && rt != null) LogOccluders(root, t, RootLocalRect(root, rt), maxDepth);
        }
        catch (Exception ex) { Log.LogWarning($"[CanvasDump] {label}: {ex.Message}"); }
    }

    private const int MaxOccludersLogged = 12;

    /// <summary>Graphics on the same root canvas that overlap the button and are drawn after it
    /// (depth is absolute within the root canvas) — what covers it, and takes the pointer first.</summary>
    private static void LogOccluders(Canvas root, Transform button, Rect buttonRect, int buttonDepth)
    {
        int count = 0;
        var sb = new StringBuilder();
        foreach (var g in root.GetComponentsInChildren<Graphic>(false))
        {
            if (g == null || !g.enabled || g.transform.IsChildOf(button) || g.depth <= buttonDepth) continue;
            bool drawn = g.color.a * g.canvasRenderer.GetInheritedAlpha() > 0.01f;
            if (!drawn && !g.raycastTarget) continue;
            if (!RootLocalRect(root, g.rectTransform).Overlaps(buttonRect)) continue;
            if (count++ >= MaxOccludersLogged) continue;
            var canvas = g.canvas;
            sb.Append($" '{g.gameObject.name}'(depth={g.depth} drawn={drawn} raycastTarget={g.raycastTarget} canvas='{canvas?.gameObject.name}')");
        }
        Log.LogInfo($"[CanvasDump]   drawn over it: {count}{sb}");
    }

    private static string DescribeRaycasters(Transform t)
    {
        var sb = new StringBuilder();
        for (var tr = t; tr != null; tr = tr.parent)
        {
            if (tr.GetComponent<Canvas>() == null) continue;
            var gr = tr.GetComponent<GraphicRaycaster>();
            if (sb.Length > 0) sb.Append("; ");
            sb.Append($"'{tr.gameObject.name}' {(gr == null ? "none" : gr.enabled ? "enabled" : "disabled")}");
        }
        return sb.ToString();
    }

    private static string DescribeMasks(Transform t)
    {
        var sb = new StringBuilder();
        for (var tr = t.parent; tr != null; tr = tr.parent)
        {
            var mask = tr.GetComponent<Mask>();
            var rectMask = tr.GetComponent<RectMask2D>();
            if ((mask == null || !mask.enabled) && (rectMask == null || !rectMask.enabled)) continue;
            if (sb.Length > 0) sb.Append("; ");
            sb.Append($"'{tr.gameObject.name}' {(mask != null && mask.enabled ? "Mask" : "RectMask2D")}");
        }
        return sb.ToString();
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

    private static void DumpContextMenuControllerMembers()
    {
        try
        {
            var controllers = Resources.FindObjectsOfTypeAll<MonoBehaviour>()
                .Where(mb => mb != null && mb.GetIl2CppType().Name == "ContextMenuController")
                .ToList();
            Log.LogInfo($"[CanvasDump] ContextMenuController instances: {controllers.Count}");
            if (controllers.Count == 0) return;

            var type = controllers[0].GetIl2CppType();
            Log.LogInfo($"[CanvasDump] ContextMenuController methods: {string.Join(", ", type.GetMethods().Select(m => m.Name).Distinct())}");
            Log.LogInfo($"[CanvasDump] ContextMenuController fields: {string.Join(", ", type.GetFields().Select(f => f.Name))}");
            Log.LogInfo($"[CanvasDump] ContextMenuController properties: {string.Join(", ", type.GetProperties().Select(p => p.Name))}");
        }
        catch (Exception ex) { Log.LogWarning($"[CanvasDump] ContextMenuController members: {ex.Message}"); }
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
