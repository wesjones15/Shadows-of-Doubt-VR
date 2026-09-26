using System;
using BepInEx.Logging;
using UnityEngine;
using static SoDVR.VR.NativeInput;

namespace SoDVR.VR;

internal enum SoloScreen { Inventory, Upgrades, Notebook, Map }

/// <summary>
/// One of the case board's screens opened on its own from the radial menu, through the game's own
/// toggle: shown by itself in front of the player, without the corkboard or navbar even if the game
/// opens the board along with it. Ends when the game closes the screen (its close button) or on
/// <see cref="Close"/>.
/// </summary>
internal sealed class SoloScreens
{
    private static ManualLogSource Log => Plugin.Log;

    // A screen may take a few frames to report itself open after its toggle.
    private const int OpenGraceFrames = 30;
    // After the screen closes, how long to wait before deciding the board the game opened with it
    // is still up and needs closing too.
    private const int BoardCheckFrames = 10;
    // The notebook is read up close: its text is small.
    private const float NotebookCloserMeters = 0.3f;

    private int _openedFrame;
    private bool _boardWasOpen;
    private int _boardCheckAt = -1;

    public SoloScreen? Active { get; private set; }

    /// <summary>Where the screen shows: in front of the head at the menu distance, facing back.</summary>
    public Vector3 Position { get; private set; }
    public Quaternion Rotation { get; private set; } = Quaternion.identity;

    /// <summary>The case-board canvas the active screen shows on, if it is one.</summary>
    public string? CaseBoardCanvas => Active switch
    {
        SoloScreen.Inventory => "BioDisplayCanvas",
        SoloScreen.Upgrades => "UpgradesDisplayCanvas",
        _ => null,
    };

    public void Open(SoloScreen screen, Camera head, bool boardOpen)
    {
        if (Active != null) Close();
        var yaw = Quaternion.Euler(0f, head.transform.eulerAngles.y, 0f);
        float distance = VRSettings.MenuDistance - (screen == SoloScreen.Notebook ? NotebookCloserMeters : 0f);
        Position = head.transform.position + yaw * (Vector3.forward * distance);
        Rotation = yaw;
        _boardWasOpen = boardOpen;
        _openedFrame = Time.frameCount;
        _boardCheckAt = -1;
        try
        {
            Toggle(screen);
            Active = screen;
            Log.LogInfo($"[SoloScreens] Opened {screen} on its own (board was {(boardOpen ? "open" : "closed")}).");
        }
        catch (Exception ex) { Log.LogWarning($"[SoloScreens] Opening {screen}: {ex.Message}"); }
    }

    public void Close()
    {
        if (Active is not { } screen) return;
        try { if (IsOpen(screen)) Toggle(screen); }
        catch (Exception ex) { Log.LogWarning($"[SoloScreens] Closing {screen}: {ex.Message}"); }
        End("closed from the controller");
    }

    public void Tick(bool boardOpen)
    {
        if (_boardCheckAt >= 0 && Time.frameCount >= _boardCheckAt)
        {
            _boardCheckAt = -1;
            if (boardOpen && !_boardWasOpen && Active == null) CloseBoard();
        }
        if (Active is not { } screen || Time.frameCount - _openedFrame < OpenGraceFrames) return;
        bool open;
        try { open = IsOpen(screen); }
        catch { open = false; }
        if (!open) End("closed by the game");
    }

    private void End(string why)
    {
        Log.LogInfo($"[SoloScreens] {Active} ended ({why}).");
        Active = null;
        _boardCheckAt = Time.frameCount + BoardCheckFrames;
    }

    private static void Toggle(SoloScreen screen)
    {
        var ui = InterfaceController.Instance;
        switch (screen)
        {
            case SoloScreen.Inventory: ui.ToggleShowInventory(); break;
            case SoloScreen.Upgrades: ui.ToggleUpgrades(); break;
            case SoloScreen.Notebook: ui.ToggleNotebookButton(); break;
            case SoloScreen.Map:
                if (IsOpen(screen)) MapController.Instance.CloseMap(false);
                else MapController.Instance.OpenMap(false, false);
                break;
        }
    }

    private static bool IsOpen(SoloScreen screen)
    {
        var ui = InterfaceController.Instance;
        return screen switch
        {
            SoloScreen.Inventory => BioScreenController.Instance != null && BioScreenController.Instance.isOpen,
            SoloScreen.Upgrades => UpgradesController.Instance != null && UpgradesController.Instance.isOpen,
            SoloScreen.Notebook => ui != null && ui.activeWindows != null && ui.activeWindows.Count > 0,
            SoloScreen.Map => ui != null && ui.minimapCanvas != null && RTCanvasPanel.IsShowing(ui.minimapCanvas),
            _ => false,
        };
    }

    /// <summary>The game opened the board along with the screen: F closes it, as Y would.</summary>
    private static void CloseBoard()
    {
        const byte VK_F = 0x46;
        const uint KEYEVENTF_KEYUP = 0x0002;
        keybd_event(VK_F, 0, 0, UIntPtr.Zero);
        keybd_event(VK_F, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        Log.LogInfo("[SoloScreens] Closed the board the game opened with the screen (F).");
    }
}
