namespace SoDVR.VR;

/// <summary>
/// Which panels become quad layers first when there are more than fit (<see cref="MainLayers"/>),
/// highest first. The rest are drawn into the eye image as before.
/// </summary>
internal enum LayerRank
{
    /// <summary>The pause/main menu and VR Settings.</summary>
    Menu,
    /// <summary>Tooltips, the keyboard, the radial menu and the game's other screens and dialogs.</summary>
    Popup,
    /// <summary>The mod's controls panel and its live row.</summary>
    Controls,
    /// <summary>The HUD with its key hints, and the interact label.</summary>
    Hints,
    /// <summary>The case board's panels, the dialogue and its subtitles, clue messages and the map.</summary>
    Board,
    /// <summary>Open notes and other windows on the board, which the player can bring closer instead.</summary>
    Notes,
    /// <summary>Always in the eye image: the world marks.</summary>
    Never,
}
