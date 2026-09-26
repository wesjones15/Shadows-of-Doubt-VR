using System;
using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// TEMPORARY evidence for the radial menu: whether the inventory, upgrades, notebook and map open
/// through the game's own calls without the case board, and what else opens with them. F7 runs the
/// next call; every change in the screens' open state is logged, whatever caused it (F7, Y, the
/// backpack gesture). Removed once the radial menu is built.
/// </summary>
internal static class ScreenOpenProbe
{
    private static ManualLogSource Log => Plugin.Log;

    private static readonly (string name, Action call)[] s_calls =
    {
        ("InterfaceController.ToggleShowInventory()", () => InterfaceController.Instance.ToggleShowInventory()),
        ("InterfaceController.ToggleUpgrades()", () => InterfaceController.Instance.ToggleUpgrades()),
        ("InterfaceController.ToggleNotebookButton()", () => InterfaceController.Instance.ToggleNotebookButton()),
        ("MapController.OpenMap(false, false)", () => MapController.Instance.OpenMap(false, false)),
    };

    private static int s_next;
    private static string? s_lastState;

    public static void Tick()
    {
        if (Input.GetKeyDown(KeyCode.F7))
        {
            var (name, call) = s_calls[s_next];
            s_next = (s_next + 1) % s_calls.Length;
            try
            {
                call();
                Log.LogInfo($"[ScreenProbe] Called {name}");
            }
            catch (Exception ex) { Log.LogWarning($"[ScreenProbe] {name} threw: {ex.Message}"); }
        }

        if (Time.frameCount % 5 != 0) return;
        string state;
        try { state = State(); }
        catch (Exception ex) { state = $"unreadable ({ex.Message})"; }
        if (state == s_lastState) return;
        s_lastState = state;
        Log.LogInfo($"[ScreenProbe] {state}");
    }

    private static string State()
    {
        var ui = InterfaceController.Instance;
        if (ui == null) return "no InterfaceController";
        return $"board={Showing("ActionPanelCanvas")} cork={Showing("CaseCanvas")} desktopMode={ui.desktopMode} " +
               $"showBoard={ui.showDesktopCaseBoard} showMap={ui.showDesktopMap} " +
               $"inventory={BioScreenController.Instance?.isOpen}/{Showing("BioDisplayCanvas")} " +
               $"upgrades={UpgradesController.Instance?.isOpen}/{Showing("UpgradesDisplayCanvas")} " +
               $"notebook={(ui.detectiveNotebook != null ? ui.detectiveNotebook.name : "none")} windows={ui.activeWindows?.Count} " +
               $"map={Showing("MinimapCanvas")} paused={SessionData.Instance?.play == false}";
    }

    private static string Showing(string canvasName)
    {
        var go = GameObject.Find(canvasName);
        var canvas = go != null ? go.GetComponent<Canvas>() : null;
        return canvas == null ? "?" : RTCanvasPanel.IsShowing(canvas) ? "up" : "down";
    }
}
