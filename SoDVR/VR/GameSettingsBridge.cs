using System;
using BepInEx.Logging;

namespace SoDVR.VR;

/// <summary>
/// The game's own settings, by identifier, through its PlayerPrefsController: current values,
/// ranges, defaults and dropdown labels come from the game's settings list and the controls of its
/// options menu, and a value is applied by driving that same control — exactly what the flat
/// options menu does, so the game applies, stores and remembers it its own way.
/// </summary>
internal static class GameSettingsBridge
{
    private static ManualLogSource Log => Plugin.Log;

    public enum Kind { Missing, Toggle, Slider, Dropdown }

    public static Kind KindOf(string id)
    {
        var s = Find(id);
        if (s == null) return Kind.Missing;
        if (s.toggle != null) return Kind.Toggle;
        if (s.slider != null && s.slider.slider != null) return Kind.Slider;
        if (s.dropdown != null && s.dropdown.dropdown != null) return Kind.Dropdown;
        return Kind.Missing;
    }

    /// <summary>The setting's current value: on/off as 1/0, a slider's value, a dropdown's index.</summary>
    public static int Current(string id)
    {
        var s = Find(id);
        if (s == null) return 0;
        if (s.dropdown != null && s.dropdown.dropdown != null && !s.useDropdownInt) return s.dropdown.dropdown.value;
        return s.intValue;
    }

    public static int? Default(string id)
    {
        var s = Find(id);
        if (s == null) return null;
        if (s.dropdown != null && !s.useDropdownInt) return null;
        return s.GetDefaultInt();
    }

    public static (int min, int max) Range(string id)
    {
        var s = Find(id);
        var slider = s?.slider?.slider;
        if (slider != null) return ((int)slider.minValue, (int)slider.maxValue);
        var dropdown = s?.dropdown?.dropdown;
        if (dropdown != null) return (0, dropdown.options.Count - 1);
        return (0, 1);
    }

    public static string OptionLabel(string id, int index)
    {
        var options = Find(id)?.dropdown?.dropdown?.options;
        if (options != null && index >= 0 && index < options.Count) return options[index].text;
        return (index + 1).ToString();
    }

    /// <summary>Sets the setting through its own options-menu control, which applies it.</summary>
    public static void Apply(string id, int value)
    {
        var s = Find(id);
        if (s == null) { Log.LogWarning($"[GameSettings] No setting '{id}'"); return; }
        int before = Current(id);
        try
        {
            if (s.toggle != null)
            {
                if (value != 0) s.toggle.SetOn(); else s.toggle.SetOff();
            }
            else if (s.slider != null)
            {
                s.slider.SetValueWithoutNotify(value);
                s.slider.OnValueChange();
            }
            else if (s.dropdown != null && s.dropdown.dropdown != null)
            {
                s.dropdown.dropdown.SetValueWithoutNotify(value);
                s.dropdown.OnValueChange();
            }
            Log.LogInfo($"[GameSettings] {id}: {before} → {value} (game now {Current(id)})");
        }
        catch (Exception ex) { Log.LogWarning($"[GameSettings] Apply '{id}' = {value}: {ex.Message}"); }
    }

    private static PlayerPrefsController.GameSetting? Find(string id)
    {
        try
        {
            var controls = PlayerPrefsController.Instance?.gameSettingControls;
            if (controls == null) return null;
            for (int i = 0; i < controls.Count; i++)
            {
                var s = controls[i];
                if (s != null && s.identifier == id) return s;
            }
        }
        catch { }
        return null;
    }
}
