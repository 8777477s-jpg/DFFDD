using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace BoltMacro;

public sealed class RecordingOverlayForm : Form
{
    private readonly Timer _timer = new();
    private readonly Label _label = new();
    private DateTime _startUtc;

    public RecordingOverlayForm()
    {
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Width = 220;
        Height = 70;

        _label.Dock = DockStyle.Fill;
        _label.TextAlign = ContentAlignment.MiddleCenter;
        _label.Font = new Font(FontFamily.GenericSansSerif, 11, FontStyle.Bold);
        Controls.Add(_label);

        _timer.Interval = 200;
        _timer.Tick += (_, __) => UpdateText();
    }

    public void StartAt(Point location)
    {
        Location = location;
        _startUtc = DateTime.UtcNow;
        UpdateText();
        _timer.Start();
        Show();
    }

    private void UpdateText()
    {
        var elapsed = DateTime.UtcNow - _startUtc;
        _label.Text = $"REC  {elapsed:mm\\:ss}  (Ctrl+Shift+Esc=STOP)";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        try
        {
            _timer.Stop();
            _timer.Dispose();
        }
        catch { }
        base.OnFormClosing(e);
    }
}
