using System;
using System.Collections.Generic;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// Each canvas is assigned a category that controls placement, scale, and interactability.
/// Ignored: transient/world-space canvases — never converted, never in depth scan.
/// HUD: body-locked (VROrigin yaw only), non-interactable, excluded from ray system.
/// </summary>
internal enum CanvasCategory { HUD, Menu, Panel, Tooltip, Ignored, Default }

internal readonly struct CanvasCategoryDefaults
{
    public readonly float Distance;             // metres in front of head
    public readonly float VerticalOffset;       // metres above (+) / below (-) eye level
    public readonly float TargetWorldWidth;     // desired world width in metres; scale = this / sizeDelta.x
    public readonly bool  RecentreOnActivate;   // reposition when canvas becomes active
    public readonly bool  RepositionEveryFrame; // never mark positioned (tooltip behaviour)
    public readonly bool  IsHUD;                // body-locked; excluded from ray/click system
    public CanvasCategoryDefaults(float dist, float vOff, float targetW,
                                  bool recentre, bool everyFrame = false,
                                  bool isHud = false)
    { Distance = dist; VerticalOffset = vOff; TargetWorldWidth = targetW;
      RecentreOnActivate = recentre; RepositionEveryFrame = everyFrame;
      IsHUD = isHud; }
}

/// <summary>
/// CURRENT IMPLEMENTATION, NOT PERMANENT ARCHITECTURE — this taxonomy exists to drive placement
/// for the world-space canvas conversion approach that CLAUDE.md and v1_findings.md already flag
/// as disqualified and slated for a RenderTexture-projected-panel rewrite. Relocated here
/// mechanically from VRCamera, not redesigned.
/// </summary>
internal static class CanvasCategoryInfo
{
    internal static CanvasCategory GetCanvasCategory(string name)
        => s_canvasCategories.TryGetValue(name, out var c) ? c : CanvasCategory.Default;

    internal static CanvasCategoryDefaults GetCategoryDefaults(CanvasCategory cat)
        => s_categoryDefaults.TryGetValue(cat, out var d) ? d : s_categoryDefaults[CanvasCategory.Default];

    // Canvas name → category.  Names are matched case-insensitively.
    private static readonly Dictionary<string, CanvasCategory> s_canvasCategories =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // HUD — body-locked (VROrigin yaw), non-interactable, excluded from ray system
        ["hudCanvas"]                 = CanvasCategory.HUD,
        ["GameCanvas"]                = CanvasCategory.HUD,
        ["statusCanvas"]              = CanvasCategory.HUD,
        ["StatusDisplayCanvas"]       = CanvasCategory.HUD,
        ["interactionProgressCanvas"] = CanvasCategory.HUD,
        ["OverlayCanvas"]             = CanvasCategory.HUD,
        ["MinimapCanvas"]             = CanvasCategory.Panel,  // was HUD — needs to be interactable
        ["GameWorldDisplayCanvas"]    = CanvasCategory.Ignored,  // already WorldSpace, game manages position
        ["CentreDisplayCanvas"]       = CanvasCategory.HUD,
        ["MessageSystemCanvas"]       = CanvasCategory.HUD,

        // Menu — recentres in front of head on activate; always in front of Panel canvases
        ["MenuCanvas"]                = CanvasCategory.Menu,
        ["DialogCanvas"]              = CanvasCategory.Menu,
        ["PopupMessage"]              = CanvasCategory.Menu,     // exit/confirm dialogs — placed 0.2m closer
        ["controlsCanvas"]            = CanvasCategory.Menu,
        ["upgradesCanvas"]            = CanvasCategory.Menu,
        ["VirtualCursorCanvas & EventSystem"] = CanvasCategory.Ignored, // game virtual cursor, not ours


        // Panel — recentres on activate, interactable, behind Menu
        ["contentCanvas"]             = CanvasCategory.Panel,
        ["osCanvas"]                  = CanvasCategory.Panel,
        ["keyboardCanvas"]            = CanvasCategory.Panel,
        ["fingerprintDisplayCanvas"]  = CanvasCategory.Panel,
        ["mapLayerCanvas"]            = CanvasCategory.Panel,
        ["PrototypeBuilderCanvas"]    = CanvasCategory.Panel,
        ["ControlsDisplayCanvas"]     = CanvasCategory.Ignored,  // VR has own controls; keyboard hints block aim dot

        // Tooltip — tracks cursor depth, repositions every frame
        ["TooltipCanvas"]             = CanvasCategory.Tooltip,
        ["tooltipsCanvas"]            = CanvasCategory.Tooltip,

        // Ignored nested canvases — must NOT be converted to WorldSpace independently.
        // These live inside a parent managed canvas and inherit its scale/position/rotation.
        // Converting them separately breaks their scale (100px initial sizeDelta → 0.016 scale),
        // positions them far from their parent, and breaks all tooltip logic.
        ["ContextMenus"]              = CanvasCategory.Ignored,  // nested inside TooltipCanvas; context menu + fast-action icons
    };

    // Per-category placement defaults.
    // TargetWorldWidth: canvas scale is computed as TargetWorldWidth / sizeDelta.x at runtime,
    // so it's immune to CanvasScaler inflation (sizeDelta may be 2720 or 1280 — doesn't matter).
    // Distance ordering (front to back): Menu (1.8m) → Panel (2.1m) → HUD (2.5m back)
    private static readonly Dictionary<CanvasCategory, CanvasCategoryDefaults> s_categoryDefaults = new()
    {
        [CanvasCategory.Menu]      = new(1.8f,  0.00f, 1.2f, recentre: true),
        [CanvasCategory.Panel]     = new(2.1f,  0.00f, 2.0f, recentre: true),                 // wider: action panel buttons bigger
        [CanvasCategory.HUD]       = new(2.5f, -0.15f, 1.5f, recentre: false, isHud: true),
        [CanvasCategory.Tooltip]   = new(1.2f, -0.10f, 1.2f, recentre: false, everyFrame: true), // wider: context menu text readable
        [CanvasCategory.Default]   = new(2.0f,  0.00f, 1.6f, recentre: false),
        [CanvasCategory.Ignored]   = new(0f,    0.00f, 1.6f, recentre: false),  // placeholder; never used
    };

    internal static bool IsCanvasVisible(Canvas canvas)
    {
        if (canvas == null) return false;
        if (!canvas.gameObject.activeSelf || !canvas.enabled) return false;
        try
        {
            var cg = canvas.GetComponent<CanvasGroup>();
            if (cg != null && cg.alpha < 0.1f) return false;
        }
        catch { }
        return true;
    }

    /// <summary>
    /// Returns true when a canvas should be skipped in the depth scan / click system.
    /// Covers two cases:
    ///   1. CanvasGroup on the canvas or any ancestor has alpha &lt; 0.1 or blocksRaycasts=false.
    ///   2. No CanvasGroup at all AND not enough active Graphics to be considered "showing content"
    ///      (e.g. MenuCanvas when the pause menu is hidden has ~3 decorative Graphics).
    /// </summary>
    internal static bool IsCanvasEffectivelyHidden(Canvas c, HashSet<int> noGroupInteractable)
    {
        bool hasCanvasGroup = false;
        try
        {
            var groups = c.GetComponentsInParent<CanvasGroup>(true);
            foreach (var cg in groups)
            {
                if (cg == null) continue;
                hasCanvasGroup = true;
                if (cg.alpha < 0.1f || !cg.blocksRaycasts) return true;
            }
        }
        catch { }

        // No CanvasGroup — use the cached active-Graphics count from the scan cycle.
        // Canvases with fewer than MinActiveGraphicsForInteractable active Graphics
        // are treated as hidden (e.g. MenuCanvas with only Border elements visible).
        if (!hasCanvasGroup)
        {
            int cid = c.GetInstanceID();
            if (!noGroupInteractable.Contains(cid)) return true;
        }
        return false;
    }
}
