using System.Windows.Forms;

namespace BoltMacro;

public sealed class DiagnosticsViewerForm : Form
{
    private readonly RichTextBox _rtb = new();
    private readonly ContextMenuStrip _ctx = new();
    private HotkeyBinding _zoomInKey = new(Keys.Add);
    private HotkeyBinding _zoomOutKey = new(Keys.Subtract);
    private HotkeyBinding _pageUpKey = new(Keys.PageUp);
    private HotkeyBinding _pageDownKey = new(Keys.PageDown);
    private HotkeyBinding _closeKey = new(Keys.Escape);

    public DiagnosticsViewerForm()
    {
        Text = "Diagnostics";
        ShowInTaskbar = false;
        WindowState = FormWindowState.Maximized;
        KeyPreview = true;

        _rtb.Dock = DockStyle.Fill;
        _rtb.ReadOnly = true;
        _rtb.WordWrap = true;
        _rtb.ScrollBars = RichTextBoxScrollBars.Vertical;
        _rtb.DetectUrls = false;
        _rtb.Font = new Font(FontFamily.GenericMonospace, 11);
        _rtb.ShortcutsEnabled = true;
        _rtb.KeyPress += (_, e) =>
        {
            if (!char.IsControl(e.KeyChar)) e.Handled = true;
        };

        _ctx.Items.Add("Copy", null, (_, __) => { try { Clipboard.SetText(_rtb.SelectedText); } catch { } });
        _ctx.Items.Add("Copy All", null, (_, __) => { try { Clipboard.SetText(_rtb.Text); } catch { } });
        _rtb.ContextMenuStrip = _ctx;
        Controls.Add(_rtb);

        KeyDown += (_, e) =>
        {
            if (_closeKey.Matches(e))
            {
                Hide();
                e.Handled = true;
                return;
            }

            if (_zoomInKey.Matches(e))
            {
                SetFontSize(_rtb.Font.Size + 1);
                e.Handled = true;
                return;
            }

            if (_zoomOutKey.Matches(e))
            {
                SetFontSize(_rtb.Font.Size - 1);
                e.Handled = true;
                return;
            }

            if (_pageUpKey.Matches(e))
            {
                ScrollByPage(-1);
                e.Handled = true;
                return;
            }

            if (_pageDownKey.Matches(e))
            {
                ScrollByPage(1);
                e.Handled = true;
            }
        };

        FormClosing += (_, e) =>
        {
            if (e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;
            Hide();
        };
    }

    public void SetFontSize(float size)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<float>(SetFontSize), size);
            return;
        }
        if (!CanUpdateUi()) return;
        _rtb.Font = new Font(FontFamily.GenericMonospace, Math.Clamp(size, 8f, 40f));
    }

    public void SetKeyBindings(HotkeyBinding zoomIn, HotkeyBinding zoomOut, HotkeyBinding pageUp, HotkeyBinding pageDown, HotkeyBinding closeKey)
    {
        _zoomInKey = zoomIn;
        _zoomOutKey = zoomOut;
        _pageUpKey = pageUp;
        _pageDownKey = pageDown;
        _closeKey = closeKey;
    }

    public void SetLines(IEnumerable<string> lines)
    {
        if (IsDisposed || Disposing) return;

        if (InvokeRequired)
        {
            BeginInvoke(new Action<IEnumerable<string>>(SetLines), lines);
            return;
        }

        if (!CanUpdateUi() || !Visible) return;

        _rtb.Lines = lines.ToArray();
        _rtb.SelectionStart = _rtb.TextLength;
        _rtb.ScrollToCaret();
    }

    private void ScrollByPage(int direction)
    {
        if (!CanUpdateUi()) return;

        int charIndex = _rtb.GetCharIndexFromPosition(new Point(2, 2));
        int currentLine = _rtb.GetLineFromCharIndex(charIndex);
        int linesPerPage = Math.Max(1, _rtb.ClientSize.Height / Math.Max(1, _rtb.Font.Height));
        int targetLine = Math.Clamp(currentLine + (direction * linesPerPage), 0, Math.Max(0, _rtb.Lines.Length - 1));
        int targetIndex = _rtb.GetFirstCharIndexFromLine(targetLine);
        if (targetIndex < 0) return;

        _rtb.SelectionStart = targetIndex;
        _rtb.SelectionLength = 0;
        _rtb.ScrollToCaret();
    }

    private bool CanUpdateUi()
        => !IsDisposed && !Disposing && !_rtb.IsDisposed;
}
