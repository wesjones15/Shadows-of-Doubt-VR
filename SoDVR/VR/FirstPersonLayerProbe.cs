using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// TEMPORARY evidence for retiring the legacy scanner's handling of 3DUI: which layers the
/// first-person objects (3DUI, and the held-item rig LagPivot that HeldItemTracker moves out of it)
/// sit on before the scanner's layer fix moves them onto the UI layer, and whether the eye cameras
/// draw those layers at all. Logged whenever the picture changes. Removed once 3DUI is decided.
/// </summary>
internal static class FirstPersonLayerProbe
{
    private static ManualLogSource Log => Plugin.Log;

    private static string? s_last;

    /// <summary>Runs right before the legacy scan, so the first look sees the layers it would change.</summary>
    public static void Tick(Camera? eye)
    {
        if (eye == null) return;
        try
        {
            var report = new StringBuilder();
            Describe(report, "3DUI", GameObject.Find("3DUI"), eye.cullingMask);
            Describe(report, "LagPivot", GameObject.Find("LagPivot"), eye.cullingMask);
            var text = report.ToString();
            if (text == s_last) return;
            s_last = text;
            Log.LogInfo($"[FirstPersonLayers] eye mask=0x{eye.cullingMask:X8}{text}");
        }
        catch (Exception ex) { Log.LogWarning($"[FirstPersonLayers] {ex.Message}"); }
    }

    private static void Describe(StringBuilder report, string label, GameObject? root, int eyeMask)
    {
        if (root == null) { report.Append($"\n  {label}: not found (or inactive)"); return; }
        var byLayer = new SortedDictionary<int, (int count, List<string> samples)>();
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t == null) continue;
            int layer = t.gameObject.layer;
            if (!byLayer.TryGetValue(layer, out var entry)) entry = (0, new List<string>());
            entry.count++;
            if (entry.samples.Count < 4) entry.samples.Add(t.name);
            byLayer[layer] = entry;
        }
        var parent = root.transform.parent;
        report.Append($"\n  {label} (parent '{(parent != null ? parent.name : "root")}'):");
        foreach (var (layer, entry) in byLayer)
        {
            bool drawn = (eyeMask & (1 << layer)) != 0;
            report.Append($"\n    layer {layer} '{LayerMask.LayerToName(layer)}' drawn by eyes={drawn}: {entry.count} object(s), e.g. {string.Join(", ", entry.samples)}");
        }
    }
}
