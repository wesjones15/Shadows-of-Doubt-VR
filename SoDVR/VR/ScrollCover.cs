using UnityEngine;

namespace SoDVR.VR;

/// <summary>Bounds for scroll content the mod moves itself (the corkboard, the map).</summary>
internal static class ScrollCover
{
    /// <summary>How far to move <paramref name="content"/> (in the viewport's own units, which is what
    /// anchoredPosition moves in) so it still covers the whole viewport — its edges can't be dragged
    /// into view. Content smaller than the viewport (zoomed far out) is centred instead.</summary>
    public static Vector2 Shift(RectTransform content, RectTransform viewport)
    {
        var r = content.rect;
        Vector3 a = viewport.InverseTransformPoint(content.TransformPoint(new Vector3(r.xMin, r.yMin, 0f)));
        Vector3 b = viewport.InverseTransformPoint(content.TransformPoint(new Vector3(r.xMax, r.yMax, 0f)));
        var v = viewport.rect;
        return new Vector2(
            AxisShift(Mathf.Min(a.x, b.x), Mathf.Max(a.x, b.x), v.xMin, v.xMax),
            AxisShift(Mathf.Min(a.y, b.y), Mathf.Max(a.y, b.y), v.yMin, v.yMax));
    }

    private static float AxisShift(float contentMin, float contentMax, float viewMin, float viewMax)
    {
        if (contentMax - contentMin <= viewMax - viewMin) return (viewMin + viewMax - contentMin - contentMax) * 0.5f;
        if (contentMin > viewMin) return viewMin - contentMin;
        if (contentMax < viewMax) return viewMax - contentMax;
        return 0f;
    }
}
