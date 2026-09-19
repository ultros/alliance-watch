using System.Drawing.Drawing2D;

namespace AllianceWatch;

internal static class UiTheme
{
    public static readonly Color Void = Color.FromArgb(2, 8, 10);
    public static readonly Color Surface = Color.FromArgb(4, 17, 20);
    public static readonly Color Raised = Color.FromArgb(6, 27, 31);
    public static readonly Color Grid = Color.FromArgb(20, 74, 78);
    public static readonly Color CyanDim = Color.FromArgb(45, 142, 148);
    public static readonly Color Cyan = Color.FromArgb(53, 222, 224);
    public static readonly Color CyanHot = Color.FromArgb(155, 255, 250);
    public static readonly Color Text = Color.FromArgb(184, 226, 223);
    public static readonly Color Muted = Color.FromArgb(82, 131, 131);
    public static readonly Color Red = Color.FromArgb(255, 70, 74);
    public static readonly Color Orange = Color.FromArgb(255, 164, 57);
    public static readonly Color Yellow = Color.FromArgb(242, 215, 75);
    public static readonly Font Micro = new("Consolas", 7.5f, FontStyle.Bold);
    public static readonly Font Small = new("Consolas", 8.5f, FontStyle.Regular);
    public static readonly Font Label = new("Consolas", 9f, FontStyle.Bold);
    public static readonly Font Heading = new("Consolas", 13f, FontStyle.Bold);
    public static readonly Font Display = new("Consolas", 30f, FontStyle.Bold);

    public static Color Severity(string value) => value switch
    {
        "RED" => Red,
        "ORANGE" => Orange,
        "YELLOW" => Yellow,
        _ => CyanDim
    };

    public static Button Button(string text)
    {
        var button = new ThemeButton
        {
            Text = text,
            AutoSize = false,
            Height = 30,
            FlatStyle = FlatStyle.Flat,
            BackColor = Surface,
            ForeColor = Cyan,
            Font = Label,
            Cursor = Cursors.Hand,
            TabStop = false,
            Margin = new Padding(3)
        };
        button.FlatAppearance.BorderColor = Grid;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = Raised;
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(9, 43, 47);
        return button;
    }
}

internal sealed class ThemeButton : Button
{
    protected override void OnPaint(PaintEventArgs e)
    {
        if(Enabled) { base.OnPaint(e); return; }
        e.Graphics.Clear(BackColor);
        using var border=new Pen(UiTheme.Grid);
        e.Graphics.DrawRectangle(border,0,0,Math.Max(0,Width-1),Math.Max(0,Height-1));
        TextRenderer.DrawText(e.Graphics,Text,Font,ClientRectangle,UiTheme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

internal sealed class TelemetryPanel : Panel
{
    public string Caption { get; set; } = "";

    public TelemetryPanel()
    {
        DoubleBuffered = true;
        BackColor = UiTheme.Surface;
        Padding = new Padding(12, 25, 12, 12);
        Margin = new Padding(4);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var border = new Pen(UiTheme.Grid);
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        using var hot = new Pen(UiTheme.Cyan, 2);
        e.Graphics.DrawLine(hot, 0, 0, Math.Min(42, Width), 0);
        e.Graphics.DrawLine(hot, 0, 0, 0, Math.Min(18, Height));
        e.Graphics.DrawLine(hot, Math.Max(0, Width - 18), Height - 1, Width - 1, Height - 1);

        if (!string.IsNullOrEmpty(Caption))
        {
            TextRenderer.DrawText(e.Graphics, Caption.ToUpperInvariant(), UiTheme.Micro,
                new Rectangle(11, 7, Width - 22, 15), UiTheme.CyanDim,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            e.Graphics.DrawLine(border, 11, 21, Width - 11, 21);
        }
    }
}

internal sealed class ThreatMeter : Control
{
    private int _score;
    public int Score
    {
        get => _score;
        set { _score = Math.Clamp(value, 0, 100); Invalidate(); }
    }

    public ThreatMeter()
    {
        DoubleBuffered = true;
        BackColor = UiTheme.Surface;
        MinimumSize = new Size(180, 140);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var centerX = Width / 2f;
        var radius = Math.Min(Width * .34f, Height * .38f);
        var arcRect = new RectangleF(centerX - radius, 34, radius * 2, radius * 2);
        using var track = new Pen(UiTheme.Grid, 8) { StartCap = LineCap.Flat, EndCap = LineCap.Flat };
        e.Graphics.DrawArc(track, arcRect, 180, 180);
        var color = Score >= 80 ? UiTheme.Red : Score >= 60 ? UiTheme.Orange : Score >= 40 ? UiTheme.Yellow : UiTheme.Cyan;
        using var active = new Pen(color, 8) { StartCap = LineCap.Flat, EndCap = LineCap.Flat };
        e.Graphics.DrawArc(active, arcRect, 180, Math.Min(180, Score / 100f * 180f));

        for (var i = 0; i <= 10; i++)
        {
            var angle = Math.PI + Math.PI * i / 10;
            var inner = radius + 7;
            var outer = radius + (i % 5 == 0 ? 15 : 11);
            var p1 = new PointF(centerX + (float)Math.Cos(angle) * inner, 34 + radius + (float)Math.Sin(angle) * inner);
            var p2 = new PointF(centerX + (float)Math.Cos(angle) * outer, 34 + radius + (float)Math.Sin(angle) * outer);
            using var tick = new Pen(i <= Math.Min(10, Score / 10) ? color : UiTheme.Grid);
            e.Graphics.DrawLine(tick, p1, p2);
        }

        var scoreText = Score.ToString("00");
        var scoreSize = TextRenderer.MeasureText(scoreText, UiTheme.Display);
        TextRenderer.DrawText(e.Graphics, scoreText, UiTheme.Display,
            new Point((Width - scoreSize.Width) / 2, 63), color, TextFormatFlags.NoPadding);
        var classification = Score >= 80 ? "EXTREME" : Score >= 60 ? "HIGH" : Score >= 40 ? "ELEVATED" : Score >= 20 ? "GUARDED" : "LOW";
        var statusSize = TextRenderer.MeasureText(classification, UiTheme.Label);
        TextRenderer.DrawText(e.Graphics, classification, UiTheme.Label,
            new Point((Width - statusSize.Width) / 2, 112), color, TextFormatFlags.NoPadding);
    }
}

internal sealed class SignalGraph : Control
{
    private IReadOnlyList<int> _values = [];
    public IReadOnlyList<int> Values
    {
        get => _values;
        set { _values = value; Invalidate(); }
    }

    public SignalGraph()
    {
        DoubleBuffered = true;
        BackColor = UiTheme.Surface;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var plot = new Rectangle(8, 8, Math.Max(1, Width - 16), Math.Max(1, Height - 18));
        using var gridPen = new Pen(Color.FromArgb(14, UiTheme.CyanDim));
        for (var x = plot.Left; x < plot.Right; x += 22) e.Graphics.DrawLine(gridPen, x, plot.Top, x, plot.Bottom);
        for (var y = plot.Top; y < plot.Bottom; y += 18) e.Graphics.DrawLine(gridPen, plot.Left, y, plot.Right, y);
        if (_values.Count < 2) return;

        var values = _values.Reverse().ToArray();
        var max = Math.Max(20, values.Max());
        var points = values.Select((value, index) => new PointF(
            plot.Left + index * plot.Width / (float)Math.Max(1, values.Length - 1),
            plot.Bottom - value * plot.Height / (float)max)).ToArray();
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var glow = new Pen(Color.FromArgb(60, UiTheme.Cyan), 5);
        using var line = new Pen(UiTheme.Cyan, 1.5f);
        e.Graphics.DrawLines(glow, points);
        e.Graphics.DrawLines(line, points);
        using var brush = new SolidBrush(UiTheme.CyanHot);
        foreach (var point in points) e.Graphics.FillRectangle(brush, point.X - 1, point.Y - 1, 3, 3);
    }
}

internal enum WindowIcon
{
    NextMonitor,
    Minimize,
    Maximize,
    Restore,
    Close
}

internal sealed class WindowIconButton : Button
{
    private WindowIcon _icon;
    private bool _hovered;
    private bool _pressed;

    public WindowIcon Icon
    {
        get => _icon;
        set { _icon = value; Invalidate(); }
    }

    public WindowIconButton(WindowIcon icon)
    {
        _icon = icon;
        Text = "";
        Dock = DockStyle.Fill;
        BackColor = UiTheme.Surface;
        ForeColor = UiTheme.Cyan;
        Cursor = Cursors.Hand;
        TabStop = true;
        Margin = new Padding(3);
        DoubleBuffered = true;
        AccessibleRole = AccessibleRole.PushButton;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var background = new SolidBrush(_pressed ? Color.FromArgb(12, 55, 59) : _hovered ? UiTheme.Raised : UiTheme.Surface);
        e.Graphics.FillRectangle(background, ClientRectangle);
        using var border = new Pen(_hovered ? UiTheme.Cyan : UiTheme.Grid, Math.Max(1f, DeviceDpi / 96f));
        e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));

        var color = Enabled ? UiTheme.CyanHot : UiTheme.Muted;
        using var iconBrush = new SolidBrush(color);
        var size = Math.Clamp(Math.Min(ClientSize.Width, ClientSize.Height) - 14, 14, 22);
        var left = (ClientSize.Width - size) / 2;
        var top = (ClientSize.Height - size) / 2;
        var thickness = Math.Max(2, size / 7);

        void Box(int x, int y, int width, int height)
        {
            e.Graphics.FillRectangle(iconBrush, x, y, width, thickness);
            e.Graphics.FillRectangle(iconBrush, x, y + height - thickness, width, thickness);
            e.Graphics.FillRectangle(iconBrush, x, y, thickness, height);
            e.Graphics.FillRectangle(iconBrush, x + width - thickness, y, thickness, height);
        }

        switch (_icon)
        {
            case WindowIcon.Minimize:
                e.Graphics.FillRectangle(iconBrush, left, top + size - thickness, size, thickness);
                break;
            case WindowIcon.Maximize:
                Box(left, top, size, size);
                e.Graphics.FillRectangle(iconBrush, left + thickness, top + thickness * 2, size - thickness * 2, thickness);
                break;
            case WindowIcon.Restore:
                Box(left + size / 4, top, size * 3 / 4, size * 3 / 4);
                using (var cover = new SolidBrush(_pressed ? Color.FromArgb(12, 55, 59) : _hovered ? UiTheme.Raised : UiTheme.Surface))
                    e.Graphics.FillRectangle(cover, left, top + size / 4, size * 3 / 4 + 1, size * 3 / 4 + 1);
                Box(left, top + size / 4, size * 3 / 4, size * 3 / 4);
                break;
            case WindowIcon.Close:
                e.Graphics.FillPolygon(iconBrush,
                new Point[]
                {
                    new Point(left, top + thickness), new Point(left + thickness, top),
                    new Point(left + size, top + size - thickness), new Point(left + size - thickness, top + size)
                });
                e.Graphics.FillPolygon(iconBrush,
                new Point[]
                {
                    new Point(left + size - thickness, top), new Point(left + size, top + thickness),
                    new Point(left + thickness, top + size), new Point(left, top + size - thickness)
                });
                break;
            case WindowIcon.NextMonitor:
                Box(left, top, size * 3 / 4, size * 2 / 3);
                Box(left + size / 3, top + size / 3, size * 2 / 3, size * 2 / 3);
                break;
        }

        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4), UiTheme.CyanHot, BackColor);
    }

    protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
}
