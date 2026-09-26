using BepInEx.Configuration;

namespace SoDVR.VR;

/// <summary>
/// The mod's own player settings, kept in the BepInEx config: edited in the file or on the VR tab of
/// the VR Settings panel, which writes straight back to it. Bound in Plugin.Load, before any VR code
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

        s_menuDistance = config.Bind("Windows", "MenuDistance", 1.8f,
            "How far in front of the head menus and windows open, in metres.");

        s_hudDistance = config.Bind("HUD", "Distance", 2.5f,
            "How far in front of the eyes the HUD sits, in metres. Only changes its depth: the HUD " +
            "looks the same size at any distance.");
        s_hudSize = config.Bind("HUD", "Size", 1f,
            "HUD size: 1 spans about 60 degrees of view, 0.75 is smaller, 1.25 larger.");
        s_hudVerticalOffset = config.Bind("HUD", "VerticalOffset", -0.15f,
            "Height of the HUD's centre relative to the eyes, in metres (negative is lower).");
    }

    public static bool SmoothTurn { get => s_smoothTurn!.Value; set => s_smoothTurn!.Value = value; }
    public static float SnapTurnAngle { get => s_snapTurnAngle!.Value; set => s_snapTurnAngle!.Value = value; }
    public static float SmoothTurnSpeed { get => s_smoothTurnSpeed!.Value; set => s_smoothTurnSpeed!.Value = value; }
    public static float MoveSpeed { get => s_moveSpeed!.Value; set => s_moveSpeed!.Value = value; }
    public static float SprintMultiplier { get => s_sprintMultiplier!.Value; set => s_sprintMultiplier!.Value = value; }
    public static bool LeftLaser { get => s_leftLaser!.Value; set => s_leftLaser!.Value = value; }
    public static bool ItemHandRight { get => s_itemHandRight!.Value; set => s_itemHandRight!.Value = value; }
    public static float MenuDistance { get => s_menuDistance!.Value; set => s_menuDistance!.Value = value; }
    public static float HudDistance { get => s_hudDistance!.Value; set => s_hudDistance!.Value = value; }
    public static float HudSize { get => s_hudSize!.Value; set => s_hudSize!.Value = value; }
    public static float HudVerticalOffset { get => s_hudVerticalOffset!.Value; set => s_hudVerticalOffset!.Value = value; }
}
