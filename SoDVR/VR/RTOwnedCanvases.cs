using System;
using System.Collections.Generic;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// The canvases an RT panel owns outright. Known by name up front, not registered when a panel
/// attaches: a case-board canvas starts out nested under GameCanvas, and the legacy scanner and
/// material patcher walk that whole subtree — possibly before the panel has found and detached it.
/// Anything they did to it (material swaps, HDR text boosts, zeroed alphas, forced CanvasGroup
/// alpha) would stay on the objects and show in the RT panel.
/// </summary>
internal static class RTOwnedCanvases
{
    private static readonly HashSet<string> s_names = new(StringComparer.Ordinal)
    {
        "MenuCanvas",
        "ActionPanelCanvas",
        "BioDisplayCanvas",
        "LocationDetailsCanvas",
        "UpgradesDisplayCanvas",
        "WindowCanvas",
        "CaseCanvas",
        "TooltipCanvas",
        "DialogCanvas",
    };

    public static bool IsOwned(string canvasName) => s_names.Contains(canvasName);

    /// <summary>True if <paramref name="t"/> is an owned canvas or anything inside one.</summary>
    public static bool Contains(Transform t)
    {
        for (var tr = t; tr != null; tr = tr.parent)
            if (s_names.Contains(tr.gameObject.name) && tr.GetComponent<Canvas>() != null) return true;
        return false;
    }
}
