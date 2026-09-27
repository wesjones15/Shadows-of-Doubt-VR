using System;
using System.Collections.Generic;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// The canvases a dedicated panel of the mod owns, by name: the generic panels
/// (<see cref="LooseCanvasPanels"/>) leave these alone.
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
        VRControlsPanel.CanvasName,
        WorldMarksPanel.CanvasName,
        InteractLabelPanel.CanvasName,
        RadialMenuPanel.CanvasName,
        CompassDisplay.CanvasName,
        // The HUD: GameCanvas and the HUD canvases nested in it.
        "GameCanvas",
        "StatusDisplayCanvas",
        "CentreDisplayCanvas",
        "MessageSystemCanvas",
        "InteractionProgressCanvas",
        "OverlayCanvas",
        "GameWorldDisplayCanvas",
        "ControlsDisplayCanvas",
    };

    public static bool IsOwned(Canvas canvas) => s_names.Contains(canvas.gameObject.name);
}
