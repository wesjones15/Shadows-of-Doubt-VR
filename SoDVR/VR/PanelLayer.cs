namespace SoDVR.VR;

/// <summary>
/// Stacking for RT panels, as the flat game stacks its canvases: a higher layer is drawn over a lower
/// one and takes the laser first, whatever their distances. Within a layer the nearer panel wins.
/// Distance alone can't be trusted across layers: notes on the case board may sit in front of the
/// pause menu, and a small tooltip in front of a large panel's edge is often farther from the eye
/// than that panel's centre.
/// </summary>
internal enum PanelLayer
{
    /// <summary>Marks tied to things in the world (objective pointers, speech bubbles, reaction
    /// indicators): under all screen UI, as the flat game draws them.</summary>
    World,
    Normal,
    /// <summary>The pause/main menu: over the case board and everything else in the scene.</summary>
    Menu,
    /// <summary>Popups, tooltips, the keyboard and VR Settings: over the menu that raised them.</summary>
    Top,
}
