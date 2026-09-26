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
    private static ConfigEntry<bool>? s_leftLaser;
    private static ConfigEntry<bool>? s_itemHandRight;
    private static ConfigEntry<float>? s_menuDistance;
    private static ConfigEntry<float>? s_hudDistance;
    private static ConfigEntry<float>? s_hudSize;
    private static ConfigEntry<float>? s_hudVerticalOffset;

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

        s_leftLaser = config.Bind("Controls", "LeftLaser", true,
            "Draw a pointer line from the left controller.");
        s_itemHandRight = config.Bind("Controls", "ItemHandRight", false,
            "Hold items in the right hand instead of the left.");

        s_menuDistance = config.Bind("Windows", "MenuDistance", 1.5f,
            "How far in front of the head menus and windows open, in metres.");

        s_hudDistance = config.Bind("HUD", "Distance", 2.5f,
            "How far in front of the eyes the HUD sits, in metres. Only changes its depth: the HUD " +
            "looks the same size at any distance.");
        s_hudSize = config.Bind("HUD", "Size", 1f,
            "HUD size: 1 spans about 60 degrees of view, 0.75 is smaller, 1.25 larger.");
        s_hudVerticalOffset = config.Bind("HUD", "VerticalOffset", -0.15f,
            "Height of the HUD's centre relative to the eyes, in metres (negative is lower).");
    }

    public static bool SmoothTurn => s_smoothTurn!.Value;
    public static float SnapTurnAngle => s_snapTurnAngle!.Value;
    public static float SmoothTurnSpeed => s_smoothTurnSpeed!.Value;
    public static float MoveSpeed => s_moveSpeed!.Value;
    public static float SprintMultiplier => s_sprintMultiplier!.Value;
    public static bool LeftLaser => s_leftLaser!.Value;
    public static bool ItemHandRight => s_itemHandRight!.Value;
    public static float MenuDistance => s_menuDistance!.Value;
    public static float HudDistance => s_hudDistance!.Value;
    public static float HudSize => s_hudSize!.Value;
    public static float HudVerticalOffset => s_hudVerticalOffset!.Value;

    // The entries themselves, for the VR tab: it edits pending values and resets to their defaults.
    public static ConfigEntry<bool> SmoothTurnEntry => s_smoothTurn!;
    public static ConfigEntry<float> SnapTurnAngleEntry => s_snapTurnAngle!;
    public static ConfigEntry<float> SmoothTurnSpeedEntry => s_smoothTurnSpeed!;
    public static ConfigEntry<float> MoveSpeedEntry => s_moveSpeed!;
    public static ConfigEntry<float> SprintMultiplierEntry => s_sprintMultiplier!;
    public static ConfigEntry<bool> LeftLaserEntry => s_leftLaser!;
    public static ConfigEntry<bool> ItemHandRightEntry => s_itemHandRight!;
    public static ConfigEntry<float> MenuDistanceEntry => s_menuDistance!;
    public static ConfigEntry<float> HudDistanceEntry => s_hudDistance!;
    public static ConfigEntry<float> HudSizeEntry => s_hudSize!;
    public static ConfigEntry<float> HudVerticalOffsetEntry => s_hudVerticalOffset!;
}
