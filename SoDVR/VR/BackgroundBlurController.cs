namespace SoDVR.VR;

// InterfaceController.SetBackgroundBlur(bool) confirmed via PE metadata inspection of
// Assembly-CSharp.dll (NativeMethodInfoPtr_SetBackgroundBlur_Public_Void_Boolean_0) —
// see CLAUDE.md verification discipline.
internal static class BackgroundBlurController
{
    public static void Tick()
    {
        if (!Plugin.DisableGameBackgroundBlurConfig.Value) return;

        InterfaceController.Instance?.SetBackgroundBlur(false);
    }
}
