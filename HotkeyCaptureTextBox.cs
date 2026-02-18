using System.Windows.Forms;

namespace BoltMacro;

public sealed class HotkeyCaptureTextBox : TextBox
{
    private HotkeyBinding _value;
    private HotkeyBinding _pending;
    private bool _listening;

    public event Action<HotkeyBinding>? HotkeyConfirmed;

    public HotkeyBinding Value
    {
        get => _value;
        set
        {
            _value = value;
            _pending = default;
            _listening = false;
            ReadOnly = true;
            Text = _value.ToString();
        }
    }

    public HotkeyCaptureTextBox()
    {
        ReadOnly = true;
        ShortcutsEnabled = false;
        TabStop = true;

        Enter += (_, __) => StartListening();
        Click += (_, __) => StartListening();
        Leave += (_, __) =>
        {
            _listening = false;
            _pending = default;
            Text = _value.ToString();
        };

        KeyDown += (_, e) =>
        {
            if (!_listening)
            {
                StartListening();
                e.SuppressKeyPress = true;
                return;
            }

            if (e.KeyCode == Keys.Enter)
            {
                if (_pending.IsEmpty)
                {
                    System.Media.SystemSounds.Beep.Play();
                    e.SuppressKeyPress = true;
                    return;
                }

                Value = _pending;
                HotkeyConfirmed?.Invoke(_value);
                e.SuppressKeyPress = true;
                return;
            }

            if (e.KeyCode == Keys.Escape)
            {
                _pending = default;
                _listening = false;
                Text = _value.ToString();
                e.SuppressKeyPress = true;
                return;
            }

            _pending = HotkeyBinding.FromKeyEvent(e);
            Text = _pending.IsEmpty ? "Press combo, then Enter" : $"{_pending} (Enter to confirm)";
            e.SuppressKeyPress = true;
        };
    }

    private void StartListening()
    {
        _listening = true;
        _pending = default;
        Text = "Press combo, then Enter";
        SelectAll();
    }
}
