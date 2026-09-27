using BepInEx.Configuration;

namespace SoDVR.VR;

/// <summary>
/// The mod's own player settings, kept in the BepInEx config: edited in the file or on the VR tab of
/// the VR Settings panel, whose Apply writes back to it. Bound in Plugin.Load, before any VR code
/// reads them.
/// </summary>
internal static class VRSettings
{
    private static ConfigEntry<bool>? s_smoothTurn;
    private static ConfigEntry<float>? s_snapTurnAngle;
    private static ConfigEntry<float>? s_smoothTurnSpeed;
    private static ConfigEntry<float>? s_moveSpeed;
    private static ConfigEntry<float>? s_sprintMultiplier;
    private static ConfigEntry<float>? s_jumpSpeed;
    private static ConfigEntry<float>? s_gravity;
    private static ConfigEntry<bool>? s_fallKnockdown;
    private static ConfigEntry<bool>? s_worldLaser;
    private static ConfigEntry<bool>? s_mainHandRight;
    private static ConfigEntry<float>? s_menuDistance;
    private static ConfigEntry<float>? s_hudDistance;
    private static ConfigEntry<float>? s_hudSize;
    private static ConfigEntry<float>? s_hudVerticalOffset;
    private static ConfigEntry<bool>? s_controlsInWorld;
    private static ConfigEntry<float>? s_renderScale;
    private static ConfigEntry<bool>? s_monitorMirror;
    private static ConfigEntry<bool>? s_stallFrames;

    public static void Bind(ConfigFile config)
    {
        s_smoothTurn = config.Bind("Turning", "SmoothTurn", false,
            "Turn smoothly with the right stick instead of in snaps.");
        s_snapTurnAngle = config.Bind("Turning", "SnapTurnAngle", 30f,
            "Degrees per snap turn.");
        s_smoothTurnSpeed = config.Bind("Turning", "SmoothTurnSpeed", 120f,
            "Degrees per second of smooth turning at full stick.");

        s_moveSpeed = config.Bind("Movement", "MoveSpeed", 4f,
            "Walking speed in metres per second at full stick.");
        s_sprintMultiplier = config.Bind("Movement", "SprintMultiplier", 1.8f,
            "How much faster sprinting (left stick click) is than walking.");
        s_jumpSpeed = config.Bind("Movement", "JumpSpeed", 5f,
            "Upward speed of a jump in metres per second.");
        s_gravity = config.Bind("Movement", "Gravity", 15f,
            "Downward acceleration in metres per second squared.");
        s_fallKnockdown = config.Bind("Movement", "FallKnockdown", true,
            "A fall that hurts knocks you down as in the flat game: the view drops to the floor and " +
            "gets back up. Off: the same damage without the view moving.");

        s_worldLaser = config.Bind("Controls", "WorldLaser", false,
            "Draw a pointer line from the main hand into the world.");
        s_mainHandRight = config.Bind("Controls", "MainHandRight", true,
            "The right hand points, interacts and holds items (false: the left). A trigger press on the other hand swaps.");

        s_menuDistance = config.Bind("Windows", "MenuDistance", 1.5f,
            "How far in front of the head menus and windows open, in metres.");

        s_hudDistance = config.Bind("HUD", "Distance", 2.5f,
            "How far in front of the eyes the HUD sits, in metres. Only changes its depth: the HUD " +
            "looks the same size at any distance.");
        s_hudSize = config.Bind("HUD", "Size", 1f,
            "HUD size: 1 spans about 60 degrees of view, 0.75 is smaller, 1.25 larger.");
        s_hudVerticalOffset = config.Bind("HUD", "VerticalOffset", -0.15f,
            "Height of the HUD's centre relative to the eyes, in metres (negative is lower).");
        s_controlsInWorld = config.Bind("HUD", "ControlsInWorld", false,
            "Show the VR controls list (the one under the key hints on the case board) in the world too.");

        s_renderScale = config.Bind("Rendering", "RenderScale", 0.7f,
            "Eye resolution as a fraction of what the headset asks for (1 = full; above 1 supersamples). " +
            "Higher is sharper, panel edges included, and costs more GPU. Applies on the next launch.");
        s_monitorMirror = config.Bind("Rendering", "MonitorMirror", true,
            "Show the headset's view (the left eye) on the monitor.");
        s_stallFrames = config.Bind("Rendering", "StallFrames", true,
            "Keep the headset smooth through the loading screens' freezes: a captured view of the loading screen stays fixed in the room while the game can't draw.");
    }

    public static bool SmoothTurn => s_smoothTurn!.Value;
    public static float SnapTurnAngle => s_snapTurnAngle!.Value;
    public static float SmoothTurnSpeed => s_smoothTurnSpeed!.Value;
    public static float MoveSpeed => s_moveSpeed!.Value;
    public static float SprintMultiplier => s_sprintMultiplier!.Value;
    public static float JumpSpeed => s_jumpSpeed!.Value;
    public static float Gravity => s_gravity!.Value;
    public static bool FallKnockdown => s_fallKnockdown!.Value;
    public static bool WorldLaser => s_worldLaser!.Value;
    public static bool MainHandRight => s_mainHandRight!.Value;
    public static float MenuDistance => s_menuDistance!.Value;
    public static float HudDistance => s_hudDistance!.Value;
    public static float HudSize => s_hudSize!.Value;
    public static float HudVerticalOffset => s_hudVerticalOffset!.Value;
    public static bool ControlsInWorld => s_controlsInWorld!.Value;
    public static float RenderScale => s_renderScale!.Value;
    public static bool MonitorMirror => s_monitorMirror!.Value;
    public static bool StallFrames => s_stallFrames!.Value;

    // The entries themselves, for the VR tab: it edits pending values and resets to their defaults.
    public static ConfigEntry<bool> SmoothTurnEntry => s_smoothTurn!;
    public static ConfigEntry<float> SnapTurnAngleEntry => s_snapTurnAngle!;
    public static ConfigEntry<float> SmoothTurnSpeedEntry => s_smoothTurnSpeed!;
    public static ConfigEntry<float> MoveSpeedEntry => s_moveSpeed!;
    public static ConfigEntry<float> SprintMultiplierEntry => s_sprintMultiplier!;
    public static ConfigEntry<float> JumpSpeedEntry => s_jumpSpeed!;
    public static ConfigEntry<float> GravityEntry => s_gravity!;
    public static ConfigEntry<bool> FallKnockdownEntry => s_fallKnockdown!;
    public static ConfigEntry<bool> WorldLaserEntry => s_worldLaser!;
    public static ConfigEntry<bool> MainHandRightEntry => s_mainHandRight!;
    public static ConfigEntry<float> MenuDistanceEntry => s_menuDistance!;
    public static ConfigEntry<float> HudDistanceEntry => s_hudDistance!;
    public static ConfigEntry<float> HudSizeEntry => s_hudSize!;
    public static ConfigEntry<float> HudVerticalOffsetEntry => s_hudVerticalOffset!;
    public static ConfigEntry<bool> ControlsInWorldEntry => s_controlsInWorld!;
    public static ConfigEntry<float> RenderScaleEntry => s_renderScale!;
    public static ConfigEntry<bool> MonitorMirrorEntry => s_monitorMirror!;
    public static ConfigEntry<bool> StallFramesEntry => s_stallFrames!;
}
