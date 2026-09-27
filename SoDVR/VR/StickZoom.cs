using UnityEngine;
using UnityEngine.UI;

namespace SoDVR.VR;

/// <summary>
/// The game's wheel zoom (<c>ZoomContent</c>, on the map and the case board) driven by the stick,
/// about the point under the laser rather than the OS mouse the game would use.
/// </summary>
internal static class StickZoom
{
    // Zoom factor per unit of stick scroll (RTPanelInput gives about 6 units a second at full tilt).
    private const float ZoomPerScrollUnit = 0.25f;

    /// <returns>False when the zoom is already at its limit that way.</returns>
    public static bool ZoomAbout(ZoomContent zoom, RectTransform content, RectTransform viewport, ScrollRect? scroll,
        float delta, in RTPointerSample sample)
    {
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(content, sample.ScreenPosition, sample.EventCamera, out var before))
            return false;
        float target = Mathf.Clamp(zoom.zoom * Mathf.Exp(delta * ZoomPerScrollUnit), zoom.zoomLimit.x, zoom.zoomLimit.y);
        if (Mathf.Approximately(target, zoom.zoom)) return false;
        // The game eases zoom towards desiredZoom every frame; set both or it pulls back.
        zoom.desiredZoom = target;
        zoom.SetZoom(target);

        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(content, sample.ScreenPosition, sample.EventCamera, out var after))
            return true;
        // The same content point moved away from the laser; slide the content back under it.
        var parent = content.parent;
        var drift = parent.InverseTransformPoint(content.TransformPoint(after)) - parent.InverseTransformPoint(content.TransformPoint(before));
        content.anchoredPosition += new Vector2(drift.x, drift.y);
        content.anchoredPosition += ScrollCover.Shift(content, viewport);
        if (scroll != null) scroll.velocity = Vector2.zero;
        return true;
    }
}
