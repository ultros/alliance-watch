using System.Drawing.Drawing2D;
using System.Diagnostics;

namespace AllianceWatch;

internal sealed record TheaterMarker(ScenarioDefinition Definition, ScenarioScore? Score, int[] Protocols, int RecentEvents, int RecentSources)
{
    public string[] Categories => CategoryCatalog.All.Where(pair => Protocols.Intersect(pair.Protocols).Any()).Select(pair => pair.Name).ToArray();
}

internal sealed record SignalCategory(string Name, Color Color, int[] Protocols);

internal static class CategoryCatalog
{
    public static readonly SignalCategory[] All =
    [
        new("DIPLOMATIC", UiTheme.Cyan, [1, 2, 3, 4, 5, 6, 22, 23]),
        new("FORCE / LOGISTICS", UiTheme.Orange, [7, 8, 15, 17, 18]),
        new("STRATEGIC", UiTheme.Red, [9, 24, 29, 30]),
        new("RESILIENCE", UiTheme.Yellow, [10, 11, 12, 13, 14, 16, 19, 20, 21, 25, 26, 27, 28])
    ];
}

internal sealed class MapForm : Form
{
    private readonly Storage _storage;
    private readonly AppConfig _config;
    private readonly TheaterMapControl _map = new() { Dock = DockStyle.Fill };
    private readonly WrappingToolbar _categoryBar = new() { MinimumToolbarHeight = 46, Padding = new Padding(6, 5, 6, 3) };
    private readonly Label _status = new() { Dock = DockStyle.Top, Height = 48, Padding = new Padding(9, 6, 9, 3), ForeColor = UiTheme.Cyan, Font = UiTheme.Small };
    private readonly TextBox _detail = new() { Dock = DockStyle.Top, Height = 190, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, BackColor = UiTheme.Surface, ForeColor = UiTheme.Text, BorderStyle = BorderStyle.None, Font = UiTheme.Small, Padding = new Padding(8), Tag = "metric-detail" };
    private readonly ToolTip _detailToolTip = new() { InitialDelay = 0, ReshowDelay = 0, AutoPopDelay = 16000, ShowAlways = true };
    private readonly DataGridView _events = new();
    private readonly Dictionary<string, Button> _categoryButtons = new();
    private Assessment? _assessment;
    private EvidenceEvent[] _evidence = [];
    private TheaterMarker[] _markers = [];
    private string? _selectedScenario;
    private string? _lastMetricHelp;

    public MapForm(Storage storage, AppConfig config)
    {
        _storage = storage;
        _config = config;
        Text = "AllianceWatch // Global Theater Map and Alert Categories";
        Size = new Size(1560, 920);
        MinimumSize = new Size(1000, 650);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = UiTheme.Void;
        ForeColor = UiTheme.Text;
        Font = UiTheme.Small;

        ConfigureEvents();
        BuildCategoryBar();
        var refresh = UiTheme.Button("REFRESH MAP");
        refresh.Width = 116;
        refresh.Click += async (_, _) => await LoadAsync();
        _categoryBar.Controls.Add(refresh);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 700, BackColor = UiTheme.Grid };
        UiTheme.KeepSplitReadable(split, .68, 480, 290);
        split.Panel1.Controls.Add(_map);
        split.Panel2.Padding = new Padding(7);
        split.Panel2.Controls.Add(_events);
        split.Panel2.Controls.Add(new Label { Dock = DockStyle.Top, Height = 26, Text = "RECENT SUPPORTING RECORDS", ForeColor = UiTheme.Cyan, Font = UiTheme.Label, Padding = new Padding(3, 4, 3, 0) });
        split.Panel2.Controls.Add(_detail);
        split.Panel2.Controls.Add(new Label { Dock = DockStyle.Top, Height = 25, Text = "SELECTED THEATER", ForeColor = UiTheme.Cyan, Font = UiTheme.Label, Padding = new Padding(3, 4, 3, 0) });
        Controls.Add(split);
        Controls.Add(_status);
        Controls.Add(_categoryBar);

        _map.TheaterSelected += SelectScenario;
        _detail.MouseMove += ShowMetricDefinition;
        _detail.MouseLeave += (_, _) => { _detailToolTip.Hide(_detail); _lastMetricHelp = null; };
        UiToolTips.Enable(this);
        Shown += async (_, _) => await LoadAsync();
    }

    private void BuildCategoryBar()
    {
        foreach (var category in CategoryCatalog.All)
        {
            var button = UiTheme.Button(category.Name);
            button.Width = category.Name.Length > 12 ? 155 : 115;
            button.ForeColor = category.Color;
            button.Tag = category.Name;
            button.Click += (sender, _) =>
            {
                if (sender is Button selected && selected.Tag is string name)
                {
                    _map.ToggleCategory(name);
                    UpdateCategoryButtons();
                }
            };
            _categoryButtons.Add(category.Name, button);
            _categoryBar.Controls.Add(button);
        }
    }

    private async Task LoadAsync()
    {
        _status.Text = "LOADING CURRENT ASSESSMENT AND EVIDENCE…";
        try
        {
            var data = await Task.Run(() => (_storage.AssessmentHistory(1).FirstOrDefault(), _storage.LoadEvidence(), _storage.ActionableAssessmentAlerts(40)));
            if (IsDisposed) return;
            _assessment = data.Item1;
            _evidence = data.Item2;
            _markers = BuildMarkers(_assessment, _evidence);
            _map.Markers = _markers;
            _map.AlertCount = data.Item3.Length;
            _map.AssessmentTimestamp = _assessment?.Timestamp ?? DateTimeOffset.UtcNow;
            _status.Text = _assessment is null
                ? "NO ASSESSMENT YET // Run an active scan to populate theater signals."
                : $"CURRENT ASSESSMENT {_assessment.Timestamp:yyyy-MM-dd HH:mm:ss} ZULU // {_markers.Length} THEATERS // {data.Item3.Length} UNREAD ALERTS // click a theater marker to inspect evidence";
            UpdateCategoryButtons();
            SelectScenario(_selectedScenario ?? _markers.OrderByDescending(marker => marker.Score?.Risk ?? 0).FirstOrDefault()?.Definition.Id);
        }
        catch (Exception ex) { if (!IsDisposed) _status.Text = "MAP LOAD FAILED // " + ex.Message; }
    }

    private TheaterMarker[] BuildMarkers(Assessment? assessment, EvidenceEvent[] evidence)
    {
        var now = assessment?.Timestamp ?? DateTimeOffset.UtcNow;
        return _config.Assessment.Scenarios.Select(definition =>
        {
            var relevant = assessment?.Contributions.Where(contribution => contribution.Scenarios.Contains(definition.Id) && contribution.Recency >= .25).ToArray() ?? [];
            var recent = evidence.Where(item => item.Scenarios.Contains(definition.Id) && item.Source.PublishedAt >= now.AddHours(-72)).ToArray();
            return new TheaterMarker(definition, assessment?.Scenarios.FirstOrDefault(score => score.Id == definition.Id), relevant.SelectMany(contribution => contribution.Protocols).Distinct().Order().ToArray(), recent.Select(item => item.ClusterId).Distinct().Count(), recent.Select(item => item.Source.Origin).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }).ToArray();
    }

    private void SelectScenario(string? scenarioId)
    {
        if (string.IsNullOrWhiteSpace(scenarioId)) return;
        var marker = _markers.FirstOrDefault(item => item.Definition.Id == scenarioId);
        if (marker is null) return;
        _selectedScenario = scenarioId;
        _map.SelectedScenario = scenarioId;
        var categories = marker.Categories.Length == 0 ? "NO ACTIVE QUALIFIED CATEGORY" : string.Join(" // ", marker.Categories);
        _detail.Text = $"{marker.Definition.Name.ToUpperInvariant()}\r\n\r\n" +
            $"INDEX {marker.Score?.Risk.ToString("F1") ?? "—"} / 100\r\n" +
            $"CONFIDENCE {marker.Score?.Confidence.ToString("F1") ?? "—"} / 100\r\n" +
            $"INDEPENDENT SOURCES {marker.Score?.IndependentSources.ToString() ?? "0"}\r\n" +
            $"EVENT FAMILIES / 72H {marker.RecentEvents}\r\n" +
            $"SOURCE ORIGINS / 72H {marker.RecentSources}\r\n" +
            $"ACTIVE CATEGORIES {categories}\r\n" +
            $"PROTOCOLS {(marker.Protocols.Length == 0 ? "—" : string.Join(", ", marker.Protocols.Select(protocol => $"P{protocol:D2}")))}\r\n\r\n" +
            "HOLD CTRL + HOVER A METRIC FOR A FULL DEFINITION.\r\n" +
            "Map locations are theater anchors, not event coordinates. Category rings indicate qualified protocol families, not confirmed operations.";
        var visibleProtocols = CategoryCatalog.All.Where(category => _map.EnabledCategories.Contains(category.Name)).SelectMany(category => category.Protocols).ToHashSet();
        var records = _evidence.Where(item => item.Scenarios.Contains(scenarioId) && item.Protocols.Intersect(visibleProtocols).Any()).OrderByDescending(item => item.Source.PublishedAt).Take(250)
            .Select(item => new { PublishedZulu = item.Source.PublishedAt.UtcDateTime, item.Source.Publisher, item.Source.Title, Protocols = string.Join(",", item.Protocols.Select(protocol => $"P{protocol:D2}")), Origins = item.Source.Origin, item.Source.Url }).ToArray();
        _events.DataSource = records;
    }

    private void UpdateCategoryButtons()
    {
        foreach (var category in CategoryCatalog.All)
        {
            var enabled = _map.EnabledCategories.Contains(category.Name);
            var button = _categoryButtons[category.Name];
            button.BackColor = enabled ? UiTheme.Raised : UiTheme.Surface;
            button.ForeColor = enabled ? category.Color : UiTheme.Muted;
        }
        if (!string.IsNullOrEmpty(_selectedScenario)) SelectScenario(_selectedScenario);
    }

    private void ShowMetricDefinition(object? sender, MouseEventArgs eventArgs)
    {
        if ((ModifierKeys & Keys.Control) != Keys.Control)
        {
            _detailToolTip.Hide(_detail);
            _lastMetricHelp = null;
            return;
        }

        var characterIndex = _detail.GetCharIndexFromPosition(eventArgs.Location);
        var lineIndex = _detail.GetLineFromCharIndex(characterIndex);
        var line = _detail.Lines.ElementAtOrDefault(lineIndex)?.Trim() ?? string.Empty;
        var definition = MetricDefinition(line);
        if (definition is null)
        {
            _detailToolTip.Hide(_detail);
            _lastMetricHelp = null;
            return;
        }

        definition = UiToolTips.ExpandDefinitions(definition);
        if (definition == _lastMetricHelp) return;
        _lastMetricHelp = definition;
        _detailToolTip.Show(definition, _detail, new Point(Math.Min(eventArgs.X + 16, _detail.Width - 32), Math.Min(eventArgs.Y + 16, _detail.Height - 28)), 16000);
    }

    private static string? MetricDefinition(string line)
    {
        if (line.StartsWith("INDEX ", StringComparison.Ordinal))
            return "INDEX — composite analytic indicator (0–100)\n\nA weighted summary of the qualified evidence assigned to this theater. It combines recency, severity, source diversity, and the relevant protocol families. It is descriptive situational awareness—not a probability of war, a forecast, or a statement of intent.";
        if (line.StartsWith("CONFIDENCE ", StringComparison.Ordinal))
            return "CONFIDENCE — evidence quality indicator (0–100)\n\nConfidence rises when the system has clearer, more recent, and more independent supporting material. It rates the support for this assessment, not the seriousness of the event. A low-risk item can have high confidence, and a high-risk item can have low confidence.";
        if (line.StartsWith("INDEPENDENT SOURCES ", StringComparison.Ordinal))
            return "INDEPENDENT SOURCES\n\nCount of distinct publishers or source streams contributing to the active theater evidence after duplicate and near-duplicate material is consolidated. Reposts and wire copies are not intended to inflate this number. More sources help corroborate a signal; they do not alone verify it.";
        if (line.StartsWith("EVENT FAMILIES / 72H ", StringComparison.Ordinal))
            return "EVENT FAMILIES / 72 HOURS\n\nNumber of separate, de-duplicated evidence clusters associated with this theater during the rolling 72-hour window. This is a diversity-of-events measure, not a raw article count. One heavily reported incident should generally remain one family.";
        if (line.StartsWith("SOURCE ORIGINS / 72H ", StringComparison.Ordinal))
            return "SOURCE ORIGINS / 72 HOURS\n\nNumber of distinct source-origin labels behind the recent supporting records. This is a quick diversity check alongside the publisher count, helping distinguish broad corroboration from a single-source reporting cascade.";
        if (line.StartsWith("ACTIVE CATEGORIES ", StringComparison.Ordinal))
            return "ACTIVE CATEGORIES\n\nThe high-level protocol families currently represented by qualified evidence: diplomatic, force/logistics, strategic, or resilience. Colored rings on the map correspond to these families. A category is an analytic classification—not confirmation that an operation is underway.";
        if (line.StartsWith("PROTOCOLS ", StringComparison.Ordinal))
            return "PROTOCOLS\n\nSpecific internal evidence rules that matched the theater’s qualified material. Use Help / Definitions to read each P-code’s complete meaning and inclusion criteria. A protocol match is a cue for review; it does not establish truth or attribution by itself.";
        if (line.StartsWith("Map locations are", StringComparison.Ordinal))
            return "THEATER ANCHOR\n\nThe marker marks a broad regional focus so that related evidence can be compared on one map. It is deliberately not a geolocation of an article, an incident, a unit, or an assessed operation. The application does not present force tracking.";
        return null;
    }

    private void ConfigureEvents()
    {
        _events.Dock = DockStyle.Fill;
        _events.ReadOnly = true;
        _events.AllowUserToAddRows = false;
        _events.AllowUserToDeleteRows = false;
        _events.RowHeadersVisible = false;
        _events.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells;
        _events.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _events.MultiSelect = false;
        _events.EnableHeadersVisualStyles = false;
        _events.BackgroundColor = UiTheme.Surface;
        _events.BorderStyle = BorderStyle.None;
        _events.GridColor = UiTheme.Grid;
        _events.DefaultCellStyle = new DataGridViewCellStyle { BackColor = UiTheme.Surface, ForeColor = UiTheme.Text, SelectionBackColor = UiTheme.Raised, SelectionForeColor = UiTheme.CyanHot, Font = UiTheme.Small, Padding = new Padding(4) };
        _events.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = UiTheme.Void, ForeColor = UiTheme.Cyan, Font = UiTheme.Label, SelectionBackColor = UiTheme.Void };
        _events.CellDoubleClick += (_, eventArgs) =>
        {
            if (eventArgs.RowIndex >= 0 && _events.Rows[eventArgs.RowIndex].Cells["Url"].Value is string url && Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl))
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        };
    }
}

internal sealed class TheaterMapControl : Control
{
    private TheaterMarker[] _markers = [];
    private readonly HashSet<string> _enabledCategories = CategoryCatalog.All.Select(category => category.Name).ToHashSet();
    private readonly Dictionary<string, PointF> _locations = new()
    {
        // Geographic theater anchors (longitude, latitude); they describe a theater,
        // not the location of a reported event or a real-world force position.
        ["russia-nato"] = new(31f, 52f), ["taiwan"] = new(121f, 24f), ["korea"] = new(127f, 37f),
        ["middle-east"] = new(44f, 32f), ["india-pakistan"] = new(70f, 29f),
        ["africa-regional"] = new(20f, 5f), ["north-america-regional"] = new(-101f, 43f),
        ["central-america-regional"] = new(-88f, 14f), ["south-america-regional"] = new(-59f, -16f)
    };
    private readonly ToolTip _toolTip = new() { InitialDelay = 250, ReshowDelay = 100, AutoPopDelay = 5000 };
    public event Action<string>? TheaterSelected;
    public int AlertCount { get; set; }
    public DateTimeOffset AssessmentTimestamp { get; set; }
    public string? SelectedScenario { get; set; }
    public IReadOnlySet<string> EnabledCategories => _enabledCategories;
    public TheaterMarker[] Markers { get => _markers; set { _markers = value; Invalidate(); } }

    public TheaterMapControl()
    {
        DoubleBuffered = true;
        BackColor = UiTheme.Surface;
        Cursor = Cursors.Hand;
        MouseClick += (_, eventArgs) =>
        {
            var nearest = _markers.Select(marker => (Marker: marker, Distance: Distance(eventArgs.Location, MapPoint(marker.Definition.Id))))
                .Where(item => item.Distance < 48).OrderBy(item => item.Distance).FirstOrDefault();
            if (nearest.Marker is not null) TheaterSelected?.Invoke(nearest.Marker.Definition.Id);
        };
        MouseMove += (_, eventArgs) =>
        {
            var nearest = _markers.Select(marker => (Marker: marker, Distance: Distance(eventArgs.Location, MapPoint(marker.Definition.Id))))
                .Where(item => item.Distance < 44).OrderBy(item => item.Distance).FirstOrDefault();
            var markerTip = nearest.Marker is null
                ? ""
                : $"{nearest.Marker.Definition.Name}\nIndex {nearest.Marker.Score?.Risk:F1}/100  •  {nearest.Marker.RecentEvents} recent event families\nClick to inspect supporting records";
            _toolTip.SetToolTip(this, (ModifierKeys & Keys.Control) == Keys.Control ? UiToolTips.ExpandDefinitions(markerTip) : markerTip);
        };
        MouseLeave += (_, _) => _toolTip.SetToolTip(this, "");
    }

    public void ToggleCategory(string category)
    {
        if (!_enabledCategories.Add(category)) _enabledCategories.Remove(category);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        DrawGrid(e.Graphics);
        DrawWorldLand(e.Graphics);
        DrawHeader(e.Graphics);
        // Render primary-focus theaters last so their labels remain legible where anchors are close.
        foreach (var marker in _markers.OrderByDescending(marker => TheaterCatalog.Find(marker.Definition.Theater).Priority)) DrawMarker(e.Graphics, marker);
        TextRenderer.DrawText(e.Graphics, "NATURAL EARTH COASTLINES // regional anchors only — not event coordinates or force positions // hover or click a marker", UiTheme.Micro, new Point(12, Height - 24), UiTheme.Muted);
    }

    private void DrawGrid(Graphics graphics)
    {
        var map = MapBounds;
        using var longitude = new Pen(Color.FromArgb(34, UiTheme.CyanDim));
        using var latitude = new Pen(Color.FromArgb(24, UiTheme.CyanDim));
        for (var longitudeIndex = -150; longitudeIndex <= 150; longitudeIndex += 30)
        {
            var x = Project(longitudeIndex, 0, map).X;
            graphics.DrawLine(longitude, x, map.Top, x, map.Bottom);
        }
        for (var latitudeIndex = -60; latitudeIndex <= 60; latitudeIndex += 30)
        {
            var y = Project(0, latitudeIndex, map).Y;
            graphics.DrawLine(latitude, map.Left, y, map.Right, y);
        }
        using var equator = new Pen(Color.FromArgb(45, UiTheme.CyanDim));
        graphics.DrawLine(equator, map.Left, Project(0, 0, map).Y, map.Right, Project(0, 0, map).Y);
        using var border = new Pen(UiTheme.Grid);
        graphics.DrawRectangle(border, map);
    }

    private void DrawWorldLand(Graphics graphics)
    {
        var map = MapBounds;
        var graphicsState = graphics.Save();
        graphics.SetClip(map);
        using var fill = new SolidBrush(Color.FromArgb(24, 33, 107, 111));
        using var outline = new Pen(Color.FromArgb(95, UiTheme.CyanDim), 1.1f);
        foreach (var coastline in WorldMapGeometry.Land)
        {
            var screenPoints = coastline.Select(point => Project(point.X, point.Y, map)).ToArray();
            if (screenPoints.Length < 3) continue;
            graphics.FillPolygon(fill, screenPoints);
            graphics.DrawPolygon(outline, screenPoints);
        }
        graphics.Restore(graphicsState);
    }

    private void DrawHeader(Graphics graphics)
    {
        var summary = $"GLOBAL THEATER MAP // FOCUS: ASIA • EUROPE • MIDDLE EAST // {AssessmentTimestamp:yyyy-MM-dd HH:mm} ZULU // {AlertCount:D2} UNREAD ALERTS";
        TextRenderer.DrawText(graphics, summary, UiTheme.Label, new Point(12, 12), UiTheme.CyanHot);
        var x = 12;
        foreach (var category in CategoryCatalog.All)
        {
            var selected = _enabledCategories.Contains(category.Name);
            using var brush = new SolidBrush(selected ? category.Color : UiTheme.Muted);
            graphics.FillRectangle(brush, x, 38, 10, 10);
            TextRenderer.DrawText(graphics, category.Name, UiTheme.Micro, new Point(x + 15, 36), selected ? category.Color : UiTheme.Muted);
            x += category.Name.Length * 7 + 42;
        }
    }

    private void DrawMarker(Graphics graphics, TheaterMarker marker)
    {
        var point = MapPoint(marker.Definition.Id);
        var risk = marker.Score?.Risk ?? 0;
        var baseColor = risk >= 70 ? UiTheme.Red : risk >= 45 ? UiTheme.Orange : risk >= 20 ? UiTheme.Yellow : UiTheme.Cyan;
        var radius = 16 + (float)Math.Min(17, risk / 5);
        using var halo = new SolidBrush(Color.FromArgb(26, baseColor));
        graphics.FillEllipse(halo, point.X - radius * 1.7f, point.Y - radius * 1.7f, radius * 3.4f, radius * 3.4f);
        using var core = new SolidBrush(Color.FromArgb(220, UiTheme.Void));
        using var ring = new Pen(baseColor, marker.Definition.Id == SelectedScenario ? 4 : 2);
        graphics.FillEllipse(core, point.X - radius, point.Y - radius, radius * 2, radius * 2);
        graphics.DrawEllipse(ring, point.X - radius, point.Y - radius, radius * 2, radius * 2);

        var active = CategoryCatalog.All.Where(category => _enabledCategories.Contains(category.Name) && marker.Protocols.Intersect(category.Protocols).Any()).ToArray();
        for (var index = 0; index < active.Length; index++)
        {
            using var categoryPen = new Pen(active[index].Color, 4);
            graphics.DrawArc(categoryPen, point.X - radius - 7, point.Y - radius - 7, (radius + 7) * 2, (radius + 7) * 2, -90 + 90 * index, 70);
        }
        var title = marker.Definition.Name.ToUpperInvariant();
        var rect = new Rectangle((int)point.X - 112, (int)point.Y + (int)radius + 10, 224, 36);
        TextRenderer.DrawText(graphics, title, UiTheme.Label, rect, baseColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(graphics, $"IDX {risk:F0} // {marker.RecentEvents} EVENT FAMILIES", UiTheme.Micro, new Rectangle(rect.X, rect.Y + 17, rect.Width, 18), UiTheme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
    }

    private RectangleF MapBounds
    {
        get
        {
            const float top = 66f;
            const float bottom = 38f;
            const float aspect = 360f / 170f;
            var usableWidth = Math.Max(1f, Width - 36f);
            var usableHeight = Math.Max(1f, Height - top - bottom);
            var mapWidth = Math.Min(usableWidth, usableHeight * aspect);
            var mapHeight = mapWidth / aspect;
            return new RectangleF((Width - mapWidth) / 2f, top + (usableHeight - mapHeight) / 2f, mapWidth, mapHeight);
        }
    }

    private PointF Project(float longitude, float latitude)
    {
        return Project(longitude, latitude, MapBounds);
    }

    private static PointF Project(float longitude, float latitude, RectangleF map)
    {
        return new PointF(
            map.Left + (longitude + 180f) / 360f * map.Width,
            map.Top + (85f - Math.Clamp(latitude, -85f, 85f)) / 170f * map.Height);
    }

    private PointF MapPoint(string scenario) => _locations.TryGetValue(scenario, out var location)
        ? Project(location.X, location.Y)
        : new(MapBounds.Left + MapBounds.Width / 2f, MapBounds.Top + MapBounds.Height / 2f);
    private float Distance(Point point, PointF destination) => (float)Math.Sqrt(Math.Pow(point.X - destination.X, 2) + Math.Pow(point.Y - destination.Y, 2));
}
