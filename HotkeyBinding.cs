using System.Windows.Forms;

namespace BoltMacro;

public readonly record struct HotkeyBinding(Keys Key, bool Ctrl = false, bool Alt = false, bool Shift = false, bool Win = false)
{
    public bool IsEmpty => Key == Keys.None;

    public uint RegisterModifiers
    {
        get
        {
            uint mods = 0;
            if (Ctrl) mods |= NativeMethods.MOD_CONTROL;
            if (Alt) mods |= NativeMethods.MOD_ALT;
            if (Shift) mods |= NativeMethods.MOD_SHIFT;
            if (Win) mods |= NativeMethods.MOD_WIN;
            return mods;
        }
    }

    public bool Matches(KeyEventArgs e)
        => e.KeyCode == Key && e.Control == Ctrl && e.Alt == Alt && e.Shift == Shift;

    public bool IsReservedCombo()
        => Ctrl && Shift && Key == Keys.Escape;

    public override string ToString()
    {
        if (IsEmpty) return "(none)";

        var parts = new List<string>(5);
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(Key.ToString());
        return string.Join("+", parts);
    }

    public static HotkeyBinding FromKeyEvent(KeyEventArgs e)
    {
        var key = e.KeyCode;
        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin)
            return default;

        return new HotkeyBinding(key, e.Control, e.Alt, e.Shift, (Control.ModifierKeys & Keys.LWin) == Keys.LWin || (Control.ModifierKeys & Keys.RWin) == Keys.RWin);
    }

    public static bool TryParse(string? raw, out HotkeyBinding hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        bool ctrl = false, alt = false, shift = false, win = false;
        Keys key = Keys.None;

        foreach (var token in raw.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || token.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                ctrl = true;
                continue;
            }
            if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                alt = true;
                continue;
            }
            if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                shift = true;
                continue;
            }
            if (token.Equals("Win", StringComparison.OrdinalIgnoreCase) || token.Equals("Windows", StringComparison.OrdinalIgnoreCase))
            {
                win = true;
                continue;
            }

            if (!Enum.TryParse(token, true, out Keys parsedKey)) return false;
            key = parsedKey;
        }

        if (key == Keys.None) return false;
        hotkey = new HotkeyBinding(key, ctrl, alt, shift, win);
        return true;
    }
}
