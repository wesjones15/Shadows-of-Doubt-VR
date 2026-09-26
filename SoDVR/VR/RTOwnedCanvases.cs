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
        "MinimapCanvas",
        ConversationSpeechPanel.CanvasName,
        VRKeyboardPanel.CanvasName,
        VRSettingsPanel.CanvasName,
        ClueMessagePanel.CanvasName,
        WorldMarksPanel.CanvasName,
        InteractLabelPanel.CanvasName,
        RadialMenuPanel.CanvasName,
        // The HUD: GameCanvas and the HUD canvases nested in it (the legacy scanner has treated
        // those as roots of their own).
        "GameCanvas",
        "StatusDisplayCanvas",
        "CentreDisplayCanvas",
        "MessageSystemCanvas",
        "InteractionProgressCanvas",
        "OverlayCanvas",
        "GameWorldDisplayCanvas",
        "ControlsDisplayCanvas",
    };

    // Canvases claimed one by one as they are found: ones with no stable name of their own.
    private static readonly HashSet<int> s_claimed = new();

    public static void Claim(Canvas canvas) => s_claimed.Add(canvas.GetInstanceID());

    public static bool IsOwned(Canvas canvas) =>
        s_names.Contains(canvas.gameObject.name) || s_claimed.Contains(canvas.GetInstanceID());

    /// <summary>True if <paramref name="t"/> is an owned canvas or anything inside one.</summary>
    public static bool Contains(Transform t)
    {
        for (var tr = t; tr != null; tr = tr.parent)
        {
            if (s_claimed.Count == 0 && !s_names.Contains(tr.gameObject.name)) continue;
            var canvas = tr.GetComponent<Canvas>();
            if (canvas != null && IsOwned(canvas)) return true;
        }
        return false;
    }
}
