using System;
using System.Runtime.InteropServices;

namespace SoDVR.VR;

/// <summary>
/// Win32 input-injection wrapper, shared by locomotion (menu/crouch/Y/interact/inventory/
/// flashlight button forwarding), case-board mouse-fallback dragging, and the void room's
/// press-any-key click simulation. Genuinely cross-cutting — not owned by any one subsystem.
///
/// Names match the native Win32 API rather than C# convention, matching how they were declared
/// at every call site before this move.
/// </summary>
internal static class NativeInput
{
    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern IntPtr GetActiveWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    /// <summary>
    /// Injected keys and clicks only reach the game while its window has OS focus. After loading a
    /// save the controller menu button did nothing for seconds while in-game actions that don't go
    /// through Windows input kept working — so before injecting, the game window is brought back
    /// to the foreground if something else has it.
    /// </summary>
    public static void EnsureGameFocused(string action)
    {
        try
        {
            var game = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            if (game == IntPtr.Zero || GetForegroundWindow() == game) return;
            bool ok = SetForegroundWindow(game);
            Plugin.Log.LogInfo($"[NativeInput] Game window lacked OS focus before '{action}' — refocus {(ok ? "succeeded" : "refused by Windows")}");
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[NativeInput] EnsureGameFocused: {ex.Message}"); }
    }
}
