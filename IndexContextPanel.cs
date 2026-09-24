namespace AllianceWatch;

// A compact explanation of the current assessment, using the same event-family
// contributions and historical deltas as the assessment console.
internal sealed class IndexContextPanel : UserControl
{
    private readonly TableLayoutPanel _layout = new()
    {
        Dock = DockStyle.Top, ColumnCount = 1, RowCount = 5,
        AutoSize = true, BackColor = UiTheme.Surface, Margin = Padding.Empty
    };
    private readonly Label _asOf = new() { Dock = DockStyle.Fill, Font = UiTheme.Micro, ForeColor = UiTheme.Muted };
    private readonly Label[] _deltas = new Label[3];
    private readonly DriverButton[] _drivers = new DriverButton[2];
    private readonly Label _support = new() { Dock = DockStyle.Fill, Font = UiTheme.Micro, ForeColor = UiTheme.Muted };
    private readonly ToolTip _details = new() { InitialDelay = 250, AutoPopDelay = 15000 };
    private static readonly string[] Windows = ["6H", "24H", "7D"];

    public event Action<string>? DriverSelected;

    public IndexContextPanel()
    {
        BackColor = UiTheme.Surface;
        AutoScroll = true;
        AccessibleName = "Index context and leading evidence";
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 30, 42, 92, 92, 46 })
            _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        _layout.Controls.Add(_asOf, 0, 0);
        var trends = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        for (var i = 0; i < Windows.Length; i++)
        {
            trends.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
            _deltas[i] = new Label { Dock = DockStyle.Fill, Font = UiTheme.Micro, ForeColor = UiTheme.Cyan,
                TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty };
            trends.Controls.Add(_deltas[i], i, 0);
        }
        _layout.Controls.Add(trends, 0, 1);
        for (var i = 0; i < _drivers.Length; i++)
        {
            var button = new DriverButton();
            button.Dock = DockStyle.Fill;
            button.Font = UiTheme.Micro;
            button.TextAlign = ContentAlignment.MiddleLeft;
            button.Padding = new Padding(5, 2, 5, 2);
            button.Margin = new Padding(0, 2, 0, 4);
            button.AutoEllipsis = true;
            button.UseMnemonic = false;
            button.Click += (_, _) => { if (button.Tag is string eventId) DriverSelected?.Invoke(eventId); };
            _drivers[i] = button;
            _layout.Controls.Add(button, 0, i + 2);
        }
        _layout.Controls.Add(_support, 0, 4);
        Controls.Add(_layout);
        UpdateAssessment(null);
    }

    public void UpdateAssessment(Assessment? assessment)
    {
        _asOf.Text = assessment is null ? "WAITING FOR ASSESSMENT" :
            $"{assessment.Timestamp.UtcDateTime:dd MMM HH:mm} UTC\r\nINDEX CHANGE / POINTS";
        for (var i = 0; i < Windows.Length; i++)
        {
            var delta = assessment?.Vector.GetValueOrDefault(Windows[i]);
            _deltas[i].Text = $"{Windows[i]}\r\n{(delta is { } value ? value.ToString("+0.0;-0.0;0.0") : "—")}";
            _deltas[i].ForeColor = delta > 0 ? UiTheme.Yellow : delta < 0 ? UiTheme.Cyan : UiTheme.Muted;
            _details.SetToolTip(_deltas[i], delta is null ? $"No assessment is available at or before the {Windows[i]} comparison time." :
                $"Change in index points from the latest saved assessment at or before {Windows[i]} ago. This is a historical comparison, not a forecast.");
        }

        var contributions = assessment?.Contributions.Where(c => double.IsFinite(c.RawScore) && c.RawScore > 0)
            .OrderByDescending(c => c.RawScore).ThenBy(c => c.EventId, StringComparer.Ordinal).ToArray() ?? [];
        var evidenceWeight = contributions.Sum(c => c.RawScore);
        for (var i = 0; i < _drivers.Length; i++)
        {
            var button = _drivers[i];
            var contribution = contributions.ElementAtOrDefault(i);
            button.Enabled = contribution is not null;
            button.Tag = contribution?.EventId;
            if (contribution is null)
            {
                button.Text = assessment is null ? "Awaiting the first assessment" : i == 0 ?
                    "No evidence currently contributes to the index" : "No second contributing signal";
                button.Headline = button.Text;
                button.AccessibleName = button.Text;
                _details.SetToolTip(button, button.Text);
                continue;
            }

            var share = 100 * contribution.RawScore / evidenceWeight;
            var sources = contribution.IndependentSources > 1 ? $"{contribution.IndependentSources} origins" : "single origin";
            button.ForeColor = contribution.Contradiction > 0 ? UiTheme.Yellow : UiTheme.Text;
            button.Heading = $"#{i + 1} · {share:F1}% WEIGHT";
            button.Headline = contribution.Title;
            button.Support = $"CONF {contribution.Confidence:F0}/100\r\n{sources.ToUpperInvariant()}";
            button.Text = $"{button.Heading}\r\n{button.Headline}\r\n{button.Support}";
            button.AccessibleName = $"Leading signal {i + 1}: {contribution.Title}. {share:F1}% of evidence weight. " +
                $"Confidence {contribution.Confidence:F0} out of 100, {sources}. Open supporting evidence.";
            _details.SetToolTip(button, $"{contribution.Title}\n\n" +
                $"{share:F1}% of the current raw evidence weight, before convergence and 0–100 scaling. " +
                $"Evidence confidence: {contribution.Confidence:F0}/100. " +
                (contribution.IndependentSources > 1 ? $"{contribution.IndependentSources} independent reporting origins." :
                    "Independent corroboration is not established; the engine uses a minimum of one origin.") +
                (contribution.Contradiction > 0 ? " Disputed or conflicting reporting reduces this contribution." : "") +
                "\nClick to inspect the supporting records.");
        }

        var singleOrigin = contributions.Count(c => c.IndependentSources <= 1);
        var disputed = contributions.Count(c => c.Contradiction > 0);
        _support.Text = assessment is null ? "Run a scan to populate this panel." : contributions.Length == 0 ?
            "No contributing signal families.\r\n— means no historical baseline." :
            $"{singleOrigin}/{contributions.Length} SINGLE-ORIGIN\r\n{disputed} DISPUTED · CLICK TO OPEN";
        _details.SetToolTip(_support, "Counts describe contributing event families, after duplicate grouping. " +
            "Single-origin families have no established independent corroboration. Disputed families include conflicting reporting. " +
            "The percentages compare raw evidence weight; they are not probabilities or percentage-point changes in the index.");
        Invalidate(true);
    }

    // Reserve separate lines for the headline and support so long news titles
    // cannot push confidence and origin counts out of the compact card.
    private sealed class DriverButton : Button
    {
        public string Heading { get; set; } = "";
        public string Headline { get; set; } = "";
        public string Support { get; set; } = "";

        public DriverButton()
        {
            BackColor = UiTheme.Raised;
            ForeColor = UiTheme.Text;
            Cursor = Cursors.Hand;
            FlatStyle = FlatStyle.Flat;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            using var border = new Pen(Focused ? UiTheme.Cyan : UiTheme.Grid);
            e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
            var textWidth = Math.Max(1, Width - 12);
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
            if (!Enabled)
            {
                TextRenderer.DrawText(e.Graphics, Headline, Font, new Rectangle(6, 6, textWidth, Math.Max(1, Height - 12)),
                    UiTheme.Muted, flags | TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter);
                return;
            }
            TextRenderer.DrawText(e.Graphics, Heading, Font, new Rectangle(6, 5, textWidth, 16), UiTheme.Cyan, flags);
            TextRenderer.DrawText(e.Graphics, Headline, Font, new Rectangle(6, 23, textWidth, Math.Max(1, Height - 57)),
                ForeColor, flags | TextFormatFlags.WordBreak);
            TextRenderer.DrawText(e.Graphics, Support, Font, new Rectangle(6, Height - 32, textWidth, 28), UiTheme.Muted, flags);
            if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, new Rectangle(3, 3, Math.Max(1, Width - 6), Math.Max(1, Height - 6)));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _details.Dispose();
        base.Dispose(disposing);
    }
}
