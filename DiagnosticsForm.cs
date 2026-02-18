using System.Windows.Forms;

namespace BoltMacro;

public sealed class DiagnosticsForm : Form
{
    private readonly RichTextBox _rtb = new();
    private readonly CheckBox _chkAutoScroll = new();

    public DiagnosticsForm()
    {
        Text = "Diagnostics";
        Width = 1100;
        Height = 700;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        Controls.Add(top);

        var btnCopyAll = new Button { Text = "Copy all", Width = 90 };
        btnCopyAll.Click += (_, __) =>
        {
            try { Clipboard.SetText(_rtb.Text); } catch { }
        };
        top.Controls.Add(btnCopyAll);

        var btnCopySelected = new Button { Text = "Copy selected", Width = 110 };
        btnCopySelected.Click += (_, __) =>
        {
            try { Clipboard.SetText(_rtb.SelectedText); } catch { }
        };
        top.Controls.Add(btnCopySelected);

        _chkAutoScroll.Text = "Auto-scroll";
        _chkAutoScroll.Checked = true;
        top.Controls.Add(_chkAutoScroll);

        _rtb.Dock = DockStyle.Fill;
        _rtb.ReadOnly = true;
        _rtb.WordWrap = true;
        _rtb.ScrollBars = RichTextBoxScrollBars.Vertical;
        _rtb.Font = new Font(FontFamily.GenericMonospace, 10);
        Controls.Add(_rtb);
    }

    public void SetFontSize(float size)
    {
        _rtb.Font = new Font(FontFamily.GenericMonospace, Math.Clamp(size, 8f, 28f));
    }

    public void SetLines(IEnumerable<string> lines)
    {
        _rtb.Lines = lines.ToArray();
        if (_chkAutoScroll.Checked)
        {
            _rtb.SelectionStart = _rtb.TextLength;
            _rtb.ScrollToCaret();
        }
    }
}
