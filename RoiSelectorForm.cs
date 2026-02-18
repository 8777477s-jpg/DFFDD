using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace BoltMacro;

public sealed class RoiSelectorForm : Form
{
    private Point _startScreen;
    private Point _endScreen;
    private bool _dragging;

    public RoiRect? Result { get; private set; }

    public RoiSelectorForm()
    {
        DoubleBuffered = true;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = false;
        Cursor = Cursors.Cross;

        var vs = SystemInformation.VirtualScreen;
        Bounds = vs;
        BackColor = Color.Black;
        Opacity = 0.25;

        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                Result = null;
                DialogResult = DialogResult.Cancel;
                Close();
            }
            else if (e.KeyCode == Keys.Enter)
            {
                FinalizeSelection();
            }
        };

        MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            _dragging = true;
            _startScreen = PointToScreen(e.Location);
            _endScreen = _startScreen;
            Invalidate();
        };

        MouseMove += (_, e) =>
        {
            if (!_dragging) return;
            _endScreen = PointToScreen(e.Location);
            Invalidate();
        };

        MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            _dragging = false;
            _endScreen = PointToScreen(e.Location);
            Invalidate();
        };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!_dragging && _startScreen == _endScreen) return;

        var screenRect = MakeRect(_startScreen, _endScreen);
        screenRect = Rectangle.Intersect(screenRect, SystemInformation.VirtualScreen);
        if (screenRect.Width <= 0 || screenRect.Height <= 0) return;

        var clientRect = new Rectangle(screenRect.X - Bounds.X, screenRect.Y - Bounds.Y, screenRect.Width, screenRect.Height);

        e.Graphics.SmoothingMode = SmoothingMode.None;
        using var pen = new Pen(Color.Lime, 2);
        e.Graphics.DrawRectangle(pen, clientRect);

        using var brush = new SolidBrush(Color.FromArgb(60, Color.Lime));
        e.Graphics.FillRectangle(brush, clientRect);

        using var infoBrush = new SolidBrush(Color.White);
        string txt = $"ROI: {screenRect.X},{screenRect.Y} {screenRect.Width}x{screenRect.Height} (Enter=OK, Esc=Cancel)";
        var f = Font ?? SystemFonts.DefaultFont;
        e.Graphics.DrawString(txt, f, infoBrush, new PointF(10, 10));
    }

    private void FinalizeSelection()
    {
        var rect = MakeRect(_startScreen, _endScreen);
        rect = Rectangle.Intersect(rect, SystemInformation.VirtualScreen);

        if (rect.Width <= 5 || rect.Height <= 5)
        {
            Result = null;
            DialogResult = DialogResult.Cancel;
            Close();
            return;
        }

        Result = new RoiRect
        {
            X = rect.X,
            Y = rect.Y,
            W = rect.Width,
            H = rect.Height,
            MonitorId = "Virtual"
        };

        DialogResult = DialogResult.OK;
        Close();
    }

    private static Rectangle MakeRect(Point a, Point b)
    {
        int x1 = Math.Min(a.X, b.X);
        int y1 = Math.Min(a.Y, b.Y);
        int x2 = Math.Max(a.X, b.X);
        int y2 = Math.Max(a.Y, b.Y);
        return new Rectangle(x1, y1, x2 - x1, y2 - y1);
    }
}
