using System;
using System.Text;
using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// Temporary: the game's world-tied marks — objective pointers, NPC reaction "!"/"?" and overheard
/// speech bubbles — about every two seconds while any exist: where each lives, whether the game is
/// showing it, where the game put it on screen, and the world target it follows. Evidence for
/// placing them at their targets instead of the game's controller-based screen positions.
/// </summary>
internal static class WorldMarksCapture
{
    private static ManualLogSource Log => Plugin.Log;

    private const int IntervalFrames = 120;
    private const int MaxLogs = 30;
    private static int s_logs;

    public static void Tick(int frame, Camera? head)
    {
        if (s_logs >= MaxLogs || frame % IntervalFrames != 0 || head == null) return;
        try
        {
            var sb = new StringBuilder();
            int count = 0;
            var headPos = head.transform.position;

            var container = InterfaceController.Instance?.uiPointerContainer;
            if (container != null)
                for (int i = 0; i < container.childCount; i++)
                {
                    var upc = container.GetChild(i).GetComponent<UIPointerController>();
                    if (upc == null) continue;
                    Vector3? target = null;
                    try { if (upc.objective?.queueElement != null) target = upc.objective.queueElement.pointerPosition; } catch { }
                    Describe(sb, "Pointer", upc.rect, upc.gameObject, null, null, target, headPos);
                    count++;
                }

            foreach (var ric in Resources.FindObjectsOfTypeAll<ReactionIndicatorController>())
            {
                if (ric == null || !ric.gameObject.scene.IsValid()) continue;
                Vector3? target = null;
                try { var look = ric.actor?.lookAtThisTransform; if (look != null) target = look.position; } catch { }
                Describe(sb, $"Reaction({ric.actor?.name})", ric.rect, ric.gameObject, ric.displayOnScreen, ric.desiredPosition, target, headPos);
                count++;
            }

            foreach (var sbc in Resources.FindObjectsOfTypeAll<SpeechBubbleController>())
            {
                if (sbc == null || !sbc.gameObject.scene.IsValid()) continue;
                Vector3? target = null;
                try { var look = sbc.speechController?.actor?.lookAtThisTransform; if (look != null) target = look.position; } catch { }
                Describe(sb, $"Speech(player={sbc.isPlayer})", sbc.rect, sbc.gameObject, sbc.displayOnScreen, sbc.desiredPosition, target, headPos);
                count++;
            }

            if (count == 0) return;
            s_logs++;
            Log.LogInfo($"[WorldMarks] f{frame} {count} mark(s):{sb}");
        }
        catch (Exception ex) { Log.LogWarning($"[WorldMarks] Capture: {ex.Message}"); }
    }

    private static void Describe(StringBuilder sb, string kind, RectTransform? rect, GameObject go, bool? onScreen,
        Vector2? desired, Vector3? target, Vector3 headPos)
    {
        float alpha = -1f;
        try
        {
            var graphic = go.GetComponentInChildren<UnityEngine.UI.Graphic>();
            if (graphic != null) alpha = graphic.color.a * graphic.canvasRenderer.GetAlpha() * graphic.canvasRenderer.GetInheritedAlpha();
        }
        catch { }
        var canvas = go.GetComponentInParent<Canvas>();
        sb.Append($"\n  {kind} '{go.name}' active={go.activeInHierarchy} canvas='{canvas?.rootCanvas?.name}/{canvas?.name}' " +
                  $"alpha={alpha:F2} onScreen={onScreen?.ToString() ?? "-"} desired={desired?.ToString() ?? "-"} " +
                  $"anchored={rect?.anchoredPosition.ToString() ?? "-"} target={target?.ToString() ?? "-"} " +
                  $"dist={(target.HasValue ? Vector3.Distance(headPos, target.Value).ToString("F1") : "-")}");
    }
}
