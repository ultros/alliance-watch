using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AllianceWatch;

internal sealed class MainForm : Form
{
    private readonly string _appDirectory;
    private readonly AppConfig _config;
    private readonly Storage _storage;
    private readonly FeedMonitor _monitor;
    private readonly System.Windows.Forms.Timer _clockTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _pollTimer = new();
    private readonly System.Windows.Forms.Timer _archiveStatsTimer = new() { Interval = 5000 };
    private readonly CancellationTokenSource _shutdown = new();

    private readonly Label _clockLabel = HeaderLabel("--:--:--");
    private readonly Label _gmtLabel = HeaderLabel("--:--:--", UiTheme.CyanHot);
    private readonly Label _dateLabel = HeaderLabel("----.--.--");
    private readonly Label _scanStateLabel = HeaderLabel("SYSTEM READY", UiTheme.Cyan);
    private readonly Label _nextScanLabel = HeaderLabel("NEXT SCAN // --:--");
    private readonly Label _articleCount = ValueLabel("000000");
    private readonly Label _matchCount = ValueLabel("0000");
    private readonly Label _redCount = ValueLabel("000");
    private readonly Label _nodeCount = ValueLabel("0/0");
    private readonly Label _cycleSummary = new() { AutoSize = false, Dock = DockStyle.Fill, Font = UiTheme.Small, ForeColor = UiTheme.Muted, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ThreatMeter _threatMeter = new() { Dock = DockStyle.Fill };
    private readonly IndexContextPanel _indexContext = new() { Dock = DockStyle.Fill };
    private readonly DataGridView _eventsGrid = new();
    private readonly DataGridView _flashpointGrid = new();
    private readonly FlowLayoutPanel _feedList = new();
    private readonly FlowLayoutPanel _alertList = new();
    private readonly FlowLayoutPanel _briefingList = new();
    private readonly FlowLayoutPanel _postureList = new();
    private readonly FlowLayoutPanel _resilienceList = new();
    private readonly FlowLayoutPanel _watchlistList = new();
    private readonly FlowLayoutPanel _relationshipList = new();
    private readonly Label[] _ladderLabels = Enumerable.Range(0, 7).Select(_ => new Label()).ToArray();
    private readonly TableLayoutPanel _contentLayout = new();
    private TableLayoutPanel? _headerLayout;
    private readonly Label _alertCount = new() { Dock = DockStyle.Fill, Font = UiTheme.Micro, ForeColor = UiTheme.Muted, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _coverageBadge = new() { Dock = DockStyle.Fill, Font = UiTheme.Micro, ForeColor = UiTheme.Cyan,
        TextAlign = ContentAlignment.MiddleLeft, Cursor = Cursors.Hand, Text = "COVERAGE // CHECKING" };
    private readonly Button _scanButton = UiTheme.Button("[ RUN ACTIVE SCAN ]");
    private readonly Button _allFilter = UiTheme.Button("ALL SIGNALS");
    private readonly Button _criticalFilter = UiTheme.Button("CRITICAL");
    private readonly TextBox _archiveSearch = new() { Width = 260, MaxLength = 512, PlaceholderText = "Search all archived articles…", Tag = "archive-search" };
    private readonly WindowIconButton _restoreButton = new(WindowIcon.Restore);
    private readonly ToolTip _windowToolTip = new() { InitialDelay = 350, ReshowDelay = 100, AutoPopDelay = 4000 };

    private IReadOnlyList<DashboardEvent> _events = [];
    private readonly List<DashboardEvent> _localAlerts = [];
    private bool _scanning;
    private bool _archiveStatsRefreshing;
    private int _scanVersion;
    private int _lastScanNewArticles;
    private int _lastScanAlertCount;
    private long _archiveStatsRevision = -1;
    private string? _archiveSummaryText;
    private Assessment? _assessment;
    private readonly bool _offline;
    private readonly Label _assessmentStatus = new() { Dock = DockStyle.Bottom, Height = 70, Font = UiTheme.Micro, ForeColor = UiTheme.Cyan, Text = "ASSESSMENT LOADING…", TextAlign = ContentAlignment.MiddleLeft };
    private bool _criticalOnly;
    private DateTime _nextScanUtc;
    private string _sortColumn = "Detected";
    private SortOrder _sortDirection = SortOrder.Descending;

    public MainForm(string appDirectory, AppConfig config, Storage storage, bool offline = false)
    {
        _offline = offline;
        _appDirectory = appDirectory;
        _config = config;
        _storage = storage;
        _monitor = new FeedMonitor(config, storage, Path.Combine(appDirectory, "alliance_watch.log"));

        Text = "AllianceWatch // Strategic Indicator Network";
        BackColor = UiTheme.Void;
        ForeColor = UiTheme.Text;
        Font = UiTheme.Small;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        DoubleBuffered = true;
        MinimumSize = new Size(900, 600);

        BuildInterface();
        UiToolTips.Enable(this);
        _threatMeter.Cursor = Cursors.Hand;
        _threatMeter.Click += (_, _) => OpenAssessment();
        _indexContext.DriverSelected += eventId => OpenAssessment(eventId: eventId);
        _coverageBadge.Click += (_, _) => OpenOperations("COVERAGE");
        _windowToolTip.SetToolTip(_coverageBadge,"Feed freshness is a collection measure, not proof of complete world coverage. Click for theatre details.");
        _threatMeter.Parent?.Controls.Add(_assessmentStatus);
        ConfigureTimers();
        KeyDown += MainForm_KeyDown;
        Shown += MainForm_Shown;
        FormClosing += (_, _) => _shutdown.Cancel();
        SizeChanged += (_, _) => AdjustResponsiveLayout();
        AdjustResponsiveLayout();
    }

    private void BuildInterface()
    {
        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Void,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(8)
        };
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        Controls.Add(shell);

        shell.Controls.Add(BuildHeader(), 0, 0);
        shell.Controls.Add(BuildContent(), 0, 1);
        shell.Controls.Add(BuildFooter(), 0, 2);
        var windowControls = BuildWindowControls();
        Controls.Add(windowControls);
        windowControls.BringToFront();
    }

    private Control BuildHeader()
    {
        var header = new TelemetryPanel { Dock = DockStyle.Fill, Padding = new Padding(12, 8, 12, 8), Margin = new Padding(4) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 8, RowCount = 1, BackColor = Color.Transparent };
        _headerLayout = layout;
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        // Reserve this space for the independently overlaid window controls.
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 244));

        var mark = new Label
        {
            Text = "AW",
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 22, FontStyle.Bold),
            ForeColor = UiTheme.CyanHot,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.FromArgb(7, 40, 43)
        };
        layout.Controls.Add(mark, 0, 0);

        var titlePanel = new Panel { Dock = DockStyle.Fill };
        titlePanel.Controls.Add(new Label
        {
            Text = "ALLIANCEWATCH",
            Dock = DockStyle.Top,
            Height = 29,
            Font = new Font("Consolas", 17, FontStyle.Bold),
            ForeColor = UiTheme.CyanHot,
            TextAlign = ContentAlignment.BottomLeft
        });
        titlePanel.Controls.Add(new Label
        {
            Text = "STRATEGIC ALIGNMENT INDICATOR NETWORK // LIVE INTELLIGENCE CONSOLE",
            Dock = DockStyle.Bottom,
            Height = 21,
            Font = UiTheme.Micro,
            ForeColor = UiTheme.CyanDim,
            TextAlign = ContentAlignment.TopLeft
        });
        EnableWindowDrag(titlePanel);
        foreach (Control child in titlePanel.Controls) EnableWindowDrag(child);
        layout.Controls.Add(titlePanel, 1, 0);

        layout.Controls.Add(StackedHeader("LOCAL TIME", _clockLabel), 2, 0);
        layout.Controls.Add(StackedHeader("GMT / ZULU", _gmtLabel), 3, 0);
        layout.Controls.Add(StackedHeader("UTC DATE", _dateLabel), 4, 0);
        layout.Controls.Add(StackedHeader("NETWORK STATE", _scanStateLabel), 5, 0);
        layout.Controls.Add(StackedHeader("AUTOMATED CYCLE", _nextScanLabel), 6, 0);

        header.Controls.Add(layout);
        return header;
    }

    private Control BuildWindowControls()
    {
        var host = new TableLayoutPanel
        {
            Width = 232,
            Height = 48,
            Top = 14,
            Left = Math.Max(0, ClientSize.Width - 244),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = UiTheme.Void,
            Padding = new Padding(2)
        };
        host.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (var i = 0; i < 4; i++) host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

        var moveScreen = new WindowIconButton(WindowIcon.NextMonitor);
        moveScreen.AccessibleName = "Move to next monitor";
        moveScreen.Click += (_, _) => MoveToNextScreen();
        _windowToolTip.SetToolTip(moveScreen, "Move to next monitor (Ctrl+Shift+Right)");
        host.Controls.Add(moveScreen, 0, 0);

        var minimize = new WindowIconButton(WindowIcon.Minimize);
        minimize.AccessibleName = "Minimize";
        minimize.Click += (_, _) => WindowState = FormWindowState.Minimized;
        _windowToolTip.SetToolTip(minimize, "Minimize");
        host.Controls.Add(minimize, 1, 0);

        _restoreButton.AccessibleName = "Restore or maximize";
        _restoreButton.Click += (_, _) => ToggleFullScreen();
        _windowToolTip.SetToolTip(_restoreButton, "Restore / maximize (F11)");
        host.Controls.Add(_restoreButton, 2, 0);

        var close = new WindowIconButton(WindowIcon.Close);
        close.AccessibleName = "Close";
        close.Click += (_, _) => Close();
        _windowToolTip.SetToolTip(close, "Close (Esc)");
        host.Controls.Add(close, 3, 0);
        return host;
    }

    private Control BuildContent()
    {
        _contentLayout.Dock = DockStyle.Fill;
        _contentLayout.ColumnCount = 3;
        _contentLayout.BackColor = UiTheme.Void;
        _contentLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270));
        _contentLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _contentLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        _contentLayout.Controls.Add(BuildLeftRail(), 0, 0);
        _contentLayout.Controls.Add(BuildCommandCenter(), 1, 0);
        _contentLayout.Controls.Add(BuildRightRail(), 2, 0);
        return _contentLayout;
    }

    private Control BuildLeftRail()
    {
        var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = UiTheme.Void };
        var rail = new TableLayoutPanel { Dock = DockStyle.Top, RowCount = 4, BackColor = UiTheme.Void };
        rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 238));
        rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 144));
        rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 128));
        rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 350));
        // Keep the evidence card compact on tall screens and reachable on short ones.
        rail.Height = 860;
        viewport.Controls.Add(rail);

        var meterCard = new TelemetryPanel { Caption = "GLOBAL INDICATOR LEVEL", Dock = DockStyle.Fill };
        meterCard.Controls.Add(_threatMeter);
        rail.Controls.Add(meterCard, 0, 0);

        var ladder = new TelemetryPanel { Caption = "ESCALATION LADDER // DESCRIPTIVE", Dock = DockStyle.Fill };
        var ladderLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 7, Padding = new Padding(5, 2, 5, 2) };
        for (var i = 0; i < 7; i++)
        {
            ladderLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 7));
            _ladderLabels[i] = new Label { Dock = DockStyle.Fill, Font = UiTheme.Micro, ForeColor = UiTheme.Muted, TextAlign = ContentAlignment.MiddleLeft };
            ladderLayout.Controls.Add(_ladderLabels[i], 0, i);
        }
        ladder.Controls.Add(ladderLayout);
        rail.Controls.Add(ladder, 0, 1);

        var relationships = new TelemetryPanel { Caption = "ACTOR SIGNAL LINKS // LAST 72H", Dock = DockStyle.Fill };
        ConfigureFlow(_relationshipList);
        relationships.Controls.Add(_relationshipList);
        rail.Controls.Add(relationships, 0, 2);

        var context = new TelemetryPanel { Caption = "INDEX CONTEXT // LEADING SIGNALS", Dock = DockStyle.Fill };
        context.Controls.Add(_indexContext);
        rail.Controls.Add(context, 0, 3);
        return viewport;
    }

    private Control BuildEventConsole()
    {
        var card = new TelemetryPanel { Caption = "DETECTED ALIGNMENT SIGNALS", Dock = DockStyle.Fill };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, BackColor = UiTheme.Surface };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0, 2, 0, 2) };
        _allFilter.Width = 115;
        _criticalFilter.Width = 105;
        _allFilter.Click += (_, _) => { _criticalOnly = false; UpdateFilterButtons(); PopulateGrid(); };
        _criticalFilter.Click += (_, _) => { _criticalOnly = true; UpdateFilterButtons(); PopulateGrid(); };
        toolbar.Controls.Add(_allFilter);
        toolbar.Controls.Add(_criticalFilter);
        var searchArchive = UiTheme.Button("SEARCH ARCHIVE");
        searchArchive.Width = 130;
        _archiveSearch.AccessibleName = "Search every saved article, including old articles";
        _archiveSearch.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            OpenArchiveSearch();
        };
        searchArchive.Click += (_, _) => OpenArchiveSearch();
        toolbar.Controls.Add(_archiveSearch);
        toolbar.Controls.Add(searchArchive);
        var evidenceHint = new Label
        {
            Text = "DOUBLE-CLICK RECORD FOR EVIDENCE",
            AutoSize = false,
            Width = 300,
            Height = 30,
            Font = UiTheme.Micro,
            ForeColor = UiTheme.Muted,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(12, 3, 0, 0)
        };
        toolbar.Controls.Add(evidenceHint);
        toolbar.SizeChanged += (_, _) =>
        {
            var compact = toolbar.ClientSize.Width < 650;
            _allFilter.Width = compact ? 78 : 115;
            _criticalFilter.Width = compact ? 78 : 105;
            _archiveSearch.Width = compact ? 150 : 260;
            searchArchive.Width = compact ? 58 : 130;
            searchArchive.Text = compact ? "GO" : "SEARCH ARCHIVE";
            evidenceHint.Visible = toolbar.ClientSize.Width >= 950;
        };
        layout.Controls.Add(toolbar, 0, 0);

        ConfigureGrid();
        layout.Controls.Add(_eventsGrid, 0, 1);
        card.Controls.Add(layout);
        UpdateFilterButtons();
        return card;
    }

    private Control BuildCommandCenter()
    {
        var host = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, BackColor = UiTheme.Void };
        host.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));
        host.RowStyles.Add(new RowStyle(SizeType.Absolute, 182));
        host.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var briefing = new TelemetryPanel { Caption = "WHAT CHANGED // EVIDENCE BRIEFING", Dock = DockStyle.Fill };
        ConfigureFlow(_briefingList);
        _briefingList.FlowDirection = FlowDirection.LeftToRight;
        _briefingList.WrapContents = true;
        briefing.Controls.Add(_briefingList);
        host.Controls.Add(briefing, 0, 0);

        var flashpoints = new TelemetryPanel { Caption = "GLOBAL THEATER MATRIX // QUALIFIED SIGNALS", Dock = DockStyle.Fill };
        ConfigureFlashpointGrid();
        flashpoints.Controls.Add(_flashpointGrid);
        host.Controls.Add(flashpoints, 0, 1);

        host.Controls.Add(BuildEventConsole(), 0, 2);
        return host;
    }

    private Control BuildRightRail()
    {
        // The right rail is taller than a small desktop window. Scroll the rail
        // instead of compressing its actions, feeds and alerts to unusable rows.
        var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = UiTheme.Void };
        var rail = new TableLayoutPanel { Dock = DockStyle.Top, RowCount = 5, BackColor = UiTheme.Void };
        rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 142));
        rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        rail.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        rail.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 360));
        const int minimumRailHeight = 932; // 142 + 150 + 140 + 140 + 360
        viewport.Controls.Add(rail);
        viewport.SizeChanged += (_, _) => rail.Height = Math.Max(minimumRailHeight, viewport.ClientSize.Height);
        rail.Height = minimumRailHeight;

        var strategic = new TelemetryPanel { Caption = "STRATEGIC POSTURE / RESILIENCE", Dock = DockStyle.Fill };
        var strategicLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        strategicLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        strategicLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        ConfigureFlow(_postureList);
        ConfigureFlow(_resilienceList);
        strategicLayout.Controls.Add(_postureList, 0, 0);
        strategicLayout.Controls.Add(_resilienceList, 1, 0);
        strategic.Controls.Add(strategicLayout);
        rail.Controls.Add(strategic, 0, 0);

        var watchlists = new TelemetryPanel { Caption = "THEATER FOCUS / ACTIVITY WINDOW", Dock = DockStyle.Fill };
        ConfigureFlow(_watchlistList);
        watchlists.Controls.Add(_watchlistList);
        rail.Controls.Add(watchlists, 0, 1);

        var feeds = new TelemetryPanel { Caption = "REMOTE COLLECTION NODES", Dock = DockStyle.Fill };
        _feedList.Dock = DockStyle.Fill;
        _feedList.FlowDirection = FlowDirection.TopDown;
        _feedList.WrapContents = false;
        _feedList.AutoScroll = true;
        _feedList.BackColor = UiTheme.Surface;
        feeds.Controls.Add(_feedList);
        rail.Controls.Add(feeds, 0, 2);
        RenderFeedStates(_config.Feeds.Select(x => new FeedState(x.Name, 0, true, "STANDBY")).ToList());

        var alerts = new TelemetryPanel { Caption = "ACTIVE ALERT CENTER", Dock = DockStyle.Fill };
        var alertsLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
        alertsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        alertsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var alertToolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        alertToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        alertToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        alertToolbar.Controls.Add(_alertCount, 0, 0);
        var dismiss = UiTheme.Button("CLEAR ALL");
        dismiss.Dock = DockStyle.Fill;
        dismiss.Font = UiTheme.Micro;
        dismiss.Click += (_, _) =>
        {
            foreach(var alert in _storage.AssessmentAlerts().Where(a=>a.State=="UNREAD")) _storage.SetAlertState(alert.Id,"ACKNOWLEDGED");
            _storage.MarkAllAlerted();
            _localAlerts.Clear();
            RenderAlertCenter();
        };
        alertToolbar.Controls.Add(dismiss, 1, 0);
        alertsLayout.Controls.Add(alertToolbar, 0, 0);
        _alertList.Dock = DockStyle.Fill;
        _alertList.FlowDirection = FlowDirection.TopDown;
        _alertList.WrapContents = false;
        _alertList.AutoScroll = true;
        _alertList.BackColor = UiTheme.Surface;
        alertsLayout.Controls.Add(_alertList, 0, 1);
        alerts.Controls.Add(alertsLayout);
        rail.Controls.Add(alerts, 0, 3);

        var control = new TelemetryPanel { Caption = "OPERATOR CONTROL", Dock = DockStyle.Fill };
        var controlLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 9 };
        for (var index = 0; index < 8; index++) controlLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        controlLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        _scanButton.Dock = DockStyle.Fill;
        _scanButton.Click += async (_, _) => await RunScanAsync();
        controlLayout.Controls.Add(_scanButton, 0, 0);
        var testAlert = UiTheme.Button("[ ASSESSMENT CONSOLE ]");
        testAlert.Dock = DockStyle.Fill;
        testAlert.Click += (_, _) =>
        {
            OpenAssessment();
        };
        controlLayout.Controls.Add(testAlert, 0, 1);
        var editConfig = UiTheme.Button("[ OPEN CONFIGURATION ]");
        editConfig.Dock = DockStyle.Fill;
        editConfig.Click += (_, _) => OpenFile(Path.Combine(_appDirectory, "config.json"));
        controlLayout.Controls.Add(editConfig, 0, 2);
        var help = UiTheme.Button("[ HELP / DEFINITIONS ]");
        help.Dock = DockStyle.Fill;
        help.Click += (_, _) => new HelpForm(_config.Assessment.Protocols).Show(this);
        var database = UiTheme.Button("[ DATABASE BROWSER ]");
        database.Dock = DockStyle.Fill;
        database.Click += (_, _) => new DatabaseBrowserForm(_storage).Show(this);
        var mapMode = UiTheme.Button("[ MAP / ALERT CATEGORIES ]");
        mapMode.Dock = DockStyle.Fill;
        mapMode.Click += (_, _) => new MapForm(_storage, _config).Show(this);
        var operations = UiTheme.Button("[ OPERATIONS WORKSPACE ]");
        operations.Dock = DockStyle.Fill;
        operations.Click += (_, _) => OpenOperations();
        var ignoredNews = UiTheme.Button("[ IGNORED NEWS / RESTORE ]");
        ignoredNews.Dock = DockStyle.Fill;
        ignoredNews.Click += (_, _) => OpenIgnoredNews();
        controlLayout.Controls.Add(database, 0, 3);
        controlLayout.Controls.Add(mapMode, 0, 4);
        controlLayout.Controls.Add(operations, 0, 5);
        controlLayout.Controls.Add(help, 0, 6);
        controlLayout.Controls.Add(ignoredNews, 0, 7);
        controlLayout.Controls.Add(new Label
        {
            Text = "ESC EXIT // F11 SIZE // CTRL+SHIFT+→ SCREEN",
            Dock = DockStyle.Fill,
            ForeColor = UiTheme.Muted,
            Font = UiTheme.Micro,
            TextAlign = ContentAlignment.BottomCenter
        }, 0, 8);
        control.Controls.Add(controlLayout);
        rail.Controls.Add(control, 0, 4);
        return viewport;
    }

    private Control BuildFooter()
    {
        var footer = new TelemetryPanel { Dock = DockStyle.Fill, Padding = new Padding(10, 2, 10, 2), Margin = new Padding(4), Caption = "" };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        layout.Controls.Add(_coverageBadge, 0, 0);
        layout.Controls.Add(_cycleSummary, 1, 0);
        layout.Controls.Add(new Label
        {
            Text = "ALLIANCEWATCH // BUILD 3.0 C#",
            Dock = DockStyle.Fill,
            ForeColor = UiTheme.CyanDim,
            Font = UiTheme.Micro,
            TextAlign = ContentAlignment.MiddleRight
        }, 2, 0);
        footer.Controls.Add(layout);
        return footer;
    }

    private void ConfigureGrid()
    {
        _eventsGrid.Dock = DockStyle.Fill;
        _eventsGrid.BackgroundColor = UiTheme.Surface;
        _eventsGrid.BorderStyle = BorderStyle.None;
        _eventsGrid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _eventsGrid.GridColor = UiTheme.Grid;
        _eventsGrid.EnableHeadersVisualStyles = false;
        _eventsGrid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        _eventsGrid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(7, 34, 38),
            ForeColor = UiTheme.Cyan,
            Font = UiTheme.Micro,
            SelectionBackColor = Color.FromArgb(7, 34, 38),
            Alignment = DataGridViewContentAlignment.MiddleLeft,
            Padding = new Padding(4)
        };
        _eventsGrid.ColumnHeadersHeight = 30;
        _eventsGrid.RowHeadersVisible = false;
        _eventsGrid.AllowUserToAddRows = false;
        _eventsGrid.AllowUserToDeleteRows = false;
        _eventsGrid.AllowUserToResizeRows = false;
        _eventsGrid.ReadOnly = true;
        _eventsGrid.MultiSelect = false;
        _eventsGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _eventsGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        _eventsGrid.RowTemplate.Height = 54;
        _eventsGrid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = UiTheme.Surface,
            ForeColor = UiTheme.Text,
            SelectionBackColor = Color.FromArgb(14, 57, 61),
            SelectionForeColor = UiTheme.CyanHot,
            Font = UiTheme.Small,
            Padding = new Padding(4),
            NullValue = "—"
        };
        _eventsGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Id", HeaderText = "RECORD ID", Width = 132, Frozen = true,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(8, 38, 42),
                ForeColor = UiTheme.CyanHot,
                SelectionBackColor = Color.FromArgb(17, 74, 78),
                SelectionForeColor = Color.White,
                Font = new Font("Consolas", 14f, FontStyle.Bold),
                Alignment = DataGridViewContentAlignment.MiddleCenter
            }
        });
        _eventsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Severity", HeaderText = "LEVEL", Width = 72, Frozen = true });
        _eventsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Score", HeaderText = "SCORE", Width = 56, Frozen = true });
        _eventsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Published", HeaderText = "PUBLISHED", Width = 132 });
        _eventsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Detected", HeaderText = "DETECTED", Width = 132 });
        _eventsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Actors", HeaderText = "ACTOR MATRIX", Width = 165 });
        _eventsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Indicators", HeaderText = "MATCHED INDICATORS", Width = 220 });
        _eventsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Event", HeaderText = "SIGNAL / EVENT", Width = 520, MinimumWidth = 260 });
        _eventsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Source", HeaderText = "SOURCE", Width = 175 });
        _eventsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Weight", HeaderText = "WT", Width = 46 });
        var fillWeights = new Dictionary<string, float>
        {
            ["Id"] = 72, ["Severity"] = 52, ["Score"] = 42, ["Published"] = 90, ["Detected"] = 90,
            ["Actors"] = 108, ["Indicators"] = 150, ["Event"] = 360, ["Source"] = 135, ["Weight"] = 38
        };
        foreach (DataGridViewColumn column in _eventsGrid.Columns) column.FillWeight = fillWeights[column.Name];
        foreach (DataGridViewColumn column in _eventsGrid.Columns)
            column.SortMode = DataGridViewColumnSortMode.Programmatic;
        _eventsGrid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && _eventsGrid.Rows[e.RowIndex].Tag is DashboardEvent item)
                OpenAssessment(item.Url);
        };
        DashboardEvent? contextNews = null;
        var menu = new ContextMenuStrip();
        var inspect = new ToolStripMenuItem("INSPECT NEWS / EVIDENCE");
        inspect.Click += (_, _) => { if (contextNews is { } item) OpenAssessment(item.Url); };
        var openSource = new ToolStripMenuItem("OPEN ORIGINAL SOURCE");
        openSource.Click += (_, _) => { if (contextNews is { } item) OpenFile(item.Url); };
        var ignore = new ToolStripMenuItem("IGNORE THIS NEWS ITEM FOR SCORE…");
        ignore.Click += async (_, _) => { if (contextNews is { } item) await IgnoreNewsAsync(item); };
        var review = new ToolStripMenuItem("VIEW IGNORED NEWS / RESTORE…");
        review.Click += (_, _) => OpenIgnoredNews();
        menu.Items.AddRange([inspect, openSource, new ToolStripSeparator(), ignore, review]);
        menu.Opening += (_, _) =>
        {
            var available = contextNews is { ArticleHash.Length: > 0 };
            inspect.Enabled = available;
            openSource.Enabled = available && Uri.TryCreate(contextNews!.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
            ignore.Enabled = available;
        };
        _eventsGrid.CellMouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            contextNews = e.RowIndex >= 0 ? _eventsGrid.Rows[e.RowIndex].Tag as DashboardEvent : null;
            if (e.RowIndex >= 0)
            {
                _eventsGrid.ClearSelection();
                _eventsGrid.Rows[e.RowIndex].Selected = true;
                _eventsGrid.CurrentCell = _eventsGrid.Rows[e.RowIndex].Cells[Math.Max(0, e.ColumnIndex)];
            }
        };
        _eventsGrid.ContextMenuStrip = menu;
        _eventsGrid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.CellStyle is not null &&
                _eventsGrid.Rows[e.RowIndex].Tag is DashboardEvent item && e.ColumnIndex is 1 or 2)
                e.CellStyle.ForeColor = UiTheme.Severity(item.Severity);
        };
        _eventsGrid.ColumnHeaderMouseClick += (_, e) =>
        {
            var selected = _eventsGrid.Columns[e.ColumnIndex].Name;
            if (_sortColumn == selected)
                _sortDirection = _sortDirection == SortOrder.Ascending ? SortOrder.Descending : SortOrder.Ascending;
            else
            {
                _sortColumn = selected;
                _sortDirection = selected is "Event" or "Source" or "Actors" or "Indicators"
                    ? SortOrder.Ascending : SortOrder.Descending;
            }
            PopulateGrid();
        };
    }

    private static void ConfigureFlow(FlowLayoutPanel panel)
    {
        panel.Dock = DockStyle.Fill;
        panel.FlowDirection = FlowDirection.TopDown;
        panel.WrapContents = false;
        panel.AutoScroll = true;
        panel.BackColor = UiTheme.Surface;
        panel.Padding = new Padding(4, 3, 4, 3);
    }

    private void ConfigureFlashpointGrid()
    {
        _flashpointGrid.Dock = DockStyle.Fill;
        _flashpointGrid.BackgroundColor = UiTheme.Surface;
        _flashpointGrid.BorderStyle = BorderStyle.None;
        _flashpointGrid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _flashpointGrid.GridColor = UiTheme.Grid;
        _flashpointGrid.EnableHeadersVisualStyles = false;
        _flashpointGrid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        _flashpointGrid.ColumnHeadersHeight = 25;
        _flashpointGrid.RowTemplate.Height = 25;
        _flashpointGrid.RowHeadersVisible = false;
        _flashpointGrid.ReadOnly = true;
        _flashpointGrid.AllowUserToAddRows = false;
        _flashpointGrid.AllowUserToDeleteRows = false;
        _flashpointGrid.AllowUserToResizeRows = false;
        _flashpointGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _flashpointGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _flashpointGrid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(7, 34, 38), ForeColor = UiTheme.Cyan, Font = UiTheme.Micro,
            SelectionBackColor = Color.FromArgb(7, 34, 38), Alignment = DataGridViewContentAlignment.MiddleLeft, Padding = new Padding(4)
        };
        _flashpointGrid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = UiTheme.Surface, ForeColor = UiTheme.Text, Font = UiTheme.Micro,
            SelectionBackColor = Color.FromArgb(14, 57, 61), SelectionForeColor = UiTheme.CyanHot, Padding = new Padding(4)
        };
        // Fill sizing uses the full monitor width; WinForms does not permit frozen columns in this mode.
        _flashpointGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Theater", HeaderText = "THEATER", MinimumWidth = 72, FillWeight = 82 });
        _flashpointGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Flashpoint", HeaderText = "FLASHPOINT", MinimumWidth = 130, FillWeight = 145 });
        _flashpointGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Risk", HeaderText = "IDX", MinimumWidth = 42, FillWeight = 48 });
        _flashpointGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Force", HeaderText = "FORCE", MinimumWidth = 72, FillWeight = 90 });
        _flashpointGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Diplomacy", HeaderText = "DIPLOMACY", MinimumWidth = 82, FillWeight = 106 });
        _flashpointGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Strategic", HeaderText = "STRATEGIC", MinimumWidth = 82, FillWeight = 106 });
        _flashpointGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Resilience", HeaderText = "RESILIENCE", MinimumWidth = 82, FillWeight = 106 });
        _flashpointGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Sources", HeaderText = "SRC", MinimumWidth = 40, FillWeight = 44 });
    }

    private static Label CommandLabel(string text, Color color, int height = 28)
    {
        return new Label
        {
            Text = text,
            Width = 244,
            Height = height,
            Margin = new Padding(0, 0, 0, 3),
            Padding = new Padding(5, 2, 5, 2),
            BackColor = Color.FromArgb(6, 30, 34),
            ForeColor = color,
            Font = UiTheme.Micro,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft
        };
    }

    private void RenderCommandCenter()
    {
        RenderLadder();
        RenderBriefing();
        RenderFlashpointMatrix();
        RenderPostureAndResilience();
        RenderWatchlists();
        RenderRelationships();
    }

    private void RenderLadder()
    {
        var captions = new[]
        {
            "BASELINE / ROUTINE", "DIPLOMATIC STRAIN", "ELEVATED TENSION", "CRISIS SIGNALS",
            "LIMITED CONFLICT", "REGIONAL WAR RISK", "MULTI-THEATER RISK"
        };
        var active = Math.Clamp(_assessment?.Ladder ?? 0, 0, captions.Length - 1);
        for (var index = 0; index < captions.Length; index++)
        {
            var current = index == active;
            _ladderLabels[index].Text = $"{(current ? "▶" : "·")} {index} // {captions[index]}";
            _ladderLabels[index].ForeColor = current ? UiTheme.Severity(index switch { >= 6 => "RED", >= 4 => "ORANGE", >= 2 => "YELLOW", _ => "GREEN" }) : UiTheme.Muted;
            _ladderLabels[index].BackColor = current ? Color.FromArgb(10, 48, 52) : Color.Transparent;
        }
    }

    private void RenderBriefing()
    {
        _briefingList.SuspendLayout();
        _briefingList.Controls.Clear();
        var cardWidth = Math.Max(220, (_briefingList.ClientSize.Width - 24) / 3);
        var changes = _assessment?.Changes
            .Where(change => change.EventId is not "normalization" and not "convergence" && Math.Abs(change.Delta) >= .1)
            .OrderByDescending(change => Math.Abs(change.Delta)).Take(3).ToArray() ?? [];
        if (changes.Length == 0)
        {
            var noChange = CommandLabel("NO MATERIAL EVIDENCE DELTA // NEW COLLECTION OR STABLE ASSESSMENT", UiTheme.Muted, 44);
            noChange.Width = cardWidth;
            _briefingList.Controls.Add(noChange);
        }
        foreach (var change in changes)
        {
            var direction = change.Delta >= 0 ? "+" : "";
            var card = CommandLabel($"Δ {direction}{change.Delta:F1}  //  {change.Reason.ToUpperInvariant()}",
                change.Delta > 0 ? UiTheme.Yellow : UiTheme.Cyan, 44);
            card.Width = cardWidth;
            card.Cursor=Cursors.Hand;
            card.Click+=(_,_)=>OpenOperations("SINCE LAST SCAN");
            _briefingList.Controls.Add(card);
        }
        var notice = CommandLabel("INDEX IS DESCRIPTIVE, NOT A FORECAST // VERIFY HIGH-IMPACT CLAIMS", UiTheme.CyanDim, 26);
        notice.Width = cardWidth;
        _briefingList.Controls.Add(notice);
        _briefingList.ResumeLayout();
    }

    private void RenderFlashpointMatrix()
    {
        _flashpointGrid.Rows.Clear();
        var scenarios = _config.Assessment.Scenarios
            .OrderBy(definition => TheaterCatalog.Find(definition.Theater).Priority)
            .ThenByDescending(definition => _assessment?.Scenarios.FirstOrDefault(item => item.Id == definition.Id)?.Risk ?? 0);
        foreach (var definition in scenarios)
        {
            var scenario = _assessment?.Scenarios.FirstOrDefault(item => item.Id == definition.Id);
            var protocols = _assessment?.Contributions.Where(item => item.Recency >= .25 && item.Scenarios.Contains(definition.Id))
                .SelectMany(item => item.Protocols).Distinct().ToHashSet() ?? [];
            var row = _flashpointGrid.Rows.Add(
                definition.Theater.ToUpperInvariant(),
                definition.Name.ToUpperInvariant(),
                scenario is null ? "—" : scenario.Risk.ToString("F0"),
                ProtocolState(protocols, [7, 8, 15, 17, 18]),
                ProtocolState(protocols, [1, 2, 3, 4, 5, 6, 22, 23]),
                ProtocolState(protocols, [9, 24, 29, 30]),
                ProtocolState(protocols, [10, 11, 12, 13, 16, 19, 20, 21, 25, 26, 27, 28]),
                scenario?.IndependentSources.ToString() ?? "—");
            _flashpointGrid.Rows[row].Cells[2].Style.ForeColor = UiTheme.Severity((scenario?.Risk ?? 0) switch { >= 70 => "RED", >= 45 => "ORANGE", >= 20 => "YELLOW", _ => "GREEN" });
        }
    }

    private static string ProtocolState(IReadOnlySet<int> protocols, int[] category)
    {
        var hits = category.Where(protocols.Contains).ToArray();
        return hits.Length == 0 ? "—" : string.Join("/", hits.Take(2).Select(id => $"P{id:D2}")) + (hits.Length > 2 ? $" +{hits.Length - 2}" : "");
    }

    private void RenderPostureAndResilience()
    {
        var protocols = _assessment?.Contributions.Where(item => item.Recency >= .25).SelectMany(item => item.Protocols).Distinct().ToHashSet() ?? [];
        RenderSignalCards(_postureList,
        [
            ("STRATEGIC", new[] { 9, 24, 29, 30 }),
            ("FORCE / LOG", new[] { 7, 8, 15, 17, 18 })
        ], protocols);
        RenderSignalCards(_resilienceList,
        [
            ("CYBER / SPACE", new[] { 19, 21 }),
            ("CIVIL / INFRA", new[] { 10, 11, 12, 13, 16, 20, 25, 27, 28 })
        ], protocols);
    }

    private static void RenderSignalCards(FlowLayoutPanel host, (string Caption, int[] Protocols)[] categories, IReadOnlySet<int> active)
    {
        host.SuspendLayout();
        host.Controls.Clear();
        foreach (var (caption, protocols) in categories)
        {
            var state = ProtocolState(active, protocols);
            host.Controls.Add(new Label
            {
                Text = $"{caption}\r\n{(state == "—" ? "NO ACTIVE SIGNAL" : state)}",
                Width = host.ClientSize.Width > 20 ? Math.Max(92, host.ClientSize.Width - 10) : 120,
                Height = 52, Margin = new Padding(0, 0, 2, 4), Padding = new Padding(5, 4, 3, 3),
                BackColor = Color.FromArgb(6, 30, 34), ForeColor = state == "—" ? UiTheme.Muted : UiTheme.Yellow,
                Font = UiTheme.Micro, TextAlign = ContentAlignment.MiddleLeft
            });
        }
        host.ResumeLayout();
    }

    private void RenderWatchlists()
    {
        _watchlistList.SuspendLayout();
        _watchlistList.Controls.Clear();
        var scoreById = _assessment?.Scenarios.ToDictionary(item => item.Id) ?? new Dictionary<string, ScenarioScore>();
        foreach (var theater in TheaterCatalog.All)
        {
            var definitions = _config.Assessment.Scenarios.Where(definition => definition.Theater.Equals(theater.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            var scores = definitions.Select(definition => scoreById.GetValueOrDefault(definition.Id)).Where(score => score is not null).Cast<ScenarioScore>().ToArray();
            var risk = scores.Select(score => score.Risk).DefaultIfEmpty(0).Max();
            var active = scores.Count(score => score.Protocols.Length > 0);
            var focus = theater.IsPrimaryFocus ? " // FOCUS" : "";
            var text = $"{theater.Name.ToUpperInvariant()}{focus}\r\nIDX {risk:F0} // {definitions.Length} WATCHES // {active} ACTIVE";
            _watchlistList.Controls.Add(CommandLabel(text, UiTheme.Severity(risk switch { >= 70 => "RED", >= 45 => "ORANGE", >= 20 => "YELLOW", _ => "GREEN" }), 37));
        }
        _watchlistList.ResumeLayout();
    }

    private void RenderRelationships()
    {
        _relationshipList.SuspendLayout();
        _relationshipList.Controls.Clear();
        var edges = _events.Where(item => item.DetectedAt >= DateTime.Now.AddHours(-72))
            .Select(item => item.Actors.Split(" / ", StringSplitOptions.RemoveEmptyEntries).Distinct().Order().ToArray())
            .Where(actors => actors.Length >= 2)
            .Select(actors => string.Join(" ↔ ", actors.Take(2)))
            .GroupBy(edge => edge).OrderByDescending(group => group.Count()).Take(3).ToArray();
        if (edges.Length == 0)
            _relationshipList.Controls.Add(CommandLabel("NO MULTI-ACTOR SIGNAL LINKS IN CURRENT WINDOW", UiTheme.Muted, 36));
        foreach (var edge in edges)
            _relationshipList.Controls.Add(CommandLabel($"{edge.Key}\r\n{edge.Count()} RECENT LINKED SIGNAL{(edge.Count() == 1 ? "" : "S")}", UiTheme.Cyan, 36));
        _relationshipList.ResumeLayout();
    }

    private void ConfigureTimers()
    {
        _clockTimer.Tick += (_, _) => UpdateClock();
        _pollTimer.Interval = Math.Max(1000, (int)Math.Min(int.MaxValue, _config.PollMinutes * 60_000));
        _pollTimer.Tick += async (_, _) => await RunScanAsync();
        _archiveStatsTimer.Tick += async (_, _) => await RefreshArchiveStatsAsync();
    }

    internal void QueueTestAlert()
    {
        _localAlerts.Insert(0, new DashboardEvent(-1, "YELLOW", 5,
            "Embedded alert center test successful", "LOCAL SYSTEM", DateTimeOffset.Now.ToString("O"),
            "SYSTEM", "operator test", 10, "", DateTime.Now, false));
        RenderAlertCenter();
    }

    private async void MainForm_Shown(object? sender, EventArgs e)
    {
        if(_offline) ExitFullScreen(); else EnterFullScreen();
        _clockTimer.Start();
        UpdateClock();
        RefreshDashboard();
        try { _assessment = await Task.Run(() => _storage.RefreshAssessment(new AssessmentEngine(_config.Assessment), _config,"STARTUP")); }
        catch(Exception ex) { if (!IsDisposed && !Disposing && !_shutdown.IsCancellationRequested) _cycleSummary.Text="ASSESSMENT INITIALIZATION FAILED // "+ex.Message; }
        if (IsDisposed || Disposing || _shutdown.IsCancellationRequested) return;
        RefreshDashboard();
        if(_offline) { _cycleSummary.Text="SYNTHETIC UI TEST // NETWORK COLLECTION DISABLED"; _scanButton.Enabled=false; return; }
        _archiveStatsTimer.Start();
        await RunScanAsync();
        if (IsDisposed || Disposing || _shutdown.IsCancellationRequested) return;
        _pollTimer.Start();
    }

    private async Task RunScanAsync()
    {
        if (_scanning || _offline || IsDisposed || Disposing || _shutdown.IsCancellationRequested) return;
        var cancellationToken = _shutdown.Token;
        var scanVersion = ++_scanVersion;
        _scanning = true;
        _archiveSummaryText = null;
        _scanButton.Enabled = false;
        _scanStateLabel.Text = "ACTIVE COLLECTION";
        _scanStateLabel.ForeColor = UiTheme.Yellow;
        _cycleSummary.Text = "LINKING REMOTE NODES // RETRIEVING SOURCE MATERIAL";
        var progress = new Progress<string>(message =>
        {
            if (!IsDisposed && !Disposing && !cancellationToken.IsCancellationRequested && _scanning && _scanVersion == scanVersion)
                _cycleSummary.Text = message;
        });

        try
        {
            var result = await Task.Run(() => _monitor.ScanAsync(progress, cancellationToken), cancellationToken);
            _assessment = await Task.Run(() => _storage.RefreshAssessment(new AssessmentEngine(_config.Assessment), _config,"SCAN"), cancellationToken);
            var archiveRevision = _monitor.ArchiveRevision;
            var archive = await Task.Run(() => _storage.ArchiveStats(), cancellationToken);
            if (IsDisposed || Disposing || cancellationToken.IsCancellationRequested) return;
            RenderFeedStates(result.FeedStates);
            RefreshDashboard();
            _lastScanNewArticles = result.NewArticles;
            _lastScanAlertCount = result.Alerts.Count;
            _archiveStatsRevision = archiveRevision;
            _archiveSummaryText = FormatCycleSummary(_lastScanNewArticles, _lastScanAlertCount, archive);
            _cycleSummary.Text = _archiveSummaryText;

            RenderAlertCenter();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (IsDisposed || Disposing || cancellationToken.IsCancellationRequested) return;
            _cycleSummary.Text = $"CYCLE FAULT // {ex.Message.ToUpperInvariant()}";
            _localAlerts.Insert(0, new DashboardEvent(-1, "RED", 15,
                $"Monitoring cycle fault: {ex.Message}", "LOCAL SYSTEM", DateTimeOffset.Now.ToString("O"),
                "SYSTEM", "scan failure", 10, "", DateTime.Now, false));
            RenderAlertCenter();
        }
        finally
        {
            _scanning = false;
            if (!IsDisposed && !Disposing && !cancellationToken.IsCancellationRequested)
            {
                _scanButton.Enabled = true;
                _scanStateLabel.Text = "SYSTEM READY";
                _scanStateLabel.ForeColor = UiTheme.Cyan;
                _nextScanUtc = DateTime.UtcNow.AddMinutes(_config.PollMinutes);
            }
        }
    }

    private async Task RefreshArchiveStatsAsync()
    {
        if (_offline || _scanning || _archiveStatsRefreshing || IsDisposed || Disposing ||
            _shutdown.IsCancellationRequested || _archiveSummaryText is null || _cycleSummary.Text != _archiveSummaryText) return;

        var archiveRevision = _monitor.ArchiveRevision;
        if (archiveRevision == _archiveStatsRevision) return;
        var cancellationToken = _shutdown.Token;
        var scanVersion = _scanVersion;
        var summaryText = _archiveSummaryText;
        _archiveStatsRefreshing = true;
        try
        {
            var archive = await Task.Run(() => _storage.ArchiveStats(), cancellationToken);
            // A scan or another operation may have replaced the footer while the database query ran.
            if (IsDisposed || Disposing || cancellationToken.IsCancellationRequested || _scanning ||
                _scanVersion != scanVersion || _cycleSummary.Text != summaryText) return;
            _archiveStatsRevision = archiveRevision;
            _archiveSummaryText = FormatCycleSummary(_lastScanNewArticles, _lastScanAlertCount, archive);
            _cycleSummary.Text = _archiveSummaryText;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"Archive statistics refresh failed: {ex.Message}");
        }
        finally
        {
            _archiveStatsRefreshing = false;
        }
    }

    private static string FormatCycleSummary(int newArticles, int alerts,
        (int Complete, int Pending, int Images, int ImageLinks, long CompressedBytes) archive) =>
        $"CYCLE COMPLETE // {newArticles} NEW // {alerts} ALERTS // " +
        $"ARCHIVE {archive.Complete} PAGES + {archive.Images} UNIQUE IMAGES / {archive.ImageLinks} ARTICLE LINKS / {archive.Pending} PENDING / " +
        $"{archive.CompressedBytes / 1048576d:F1} MB COMPRESSED";

    private void RefreshDashboard()
    {
        _events = _storage.RecentEvents();
        var stats = _storage.GetStats();
        _articleCount.Text = stats.TotalArticles.ToString("D6");
        _matchCount.Text = stats.TotalMatches.ToString("D4");
        _redCount.Text = stats.RedCount.ToString("D3");
        _threatMeter.Score = (int)Math.Round(_assessment?.Risk ?? 0);
        if (_assessment is { } a) _assessmentStatus.Text = $"CONFIDENCE {a.Confidence:F0}/100 // {a.Momentum}\r\nSTATE {a.Ladder} // {a.Contributions.SelectMany(c => c.Protocols).Distinct().Count()}/30 PROTOCOLS\r\nARTICLES {_articleCount.Text} // SIGNALS {_matchCount.Text} // HIGH {_redCount.Text}\r\nCLICK GAUGE // WHY SCORE MOVED";
        _indexContext.UpdateAssessment(_assessment);
        RenderCoverageBadge();
        PopulateGrid();
        RenderCommandCenter();
        RenderAlertCenter();
    }

    private void PopulateGrid()
    {
        _eventsGrid.Rows.Clear();
        var filtered = _criticalOnly ? _events.Where(x => x.Severity == "RED") : _events;
        var visible = SortEvents(filtered);
        foreach (var item in visible)
        {
            var detected = item.DetectedAt == default ? "UNKNOWN" : item.DetectedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            var published = DateTimeOffset.TryParse(item.Published, out var publishedAt)
                ? publishedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : item.Published;
            var rowIndex = _eventsGrid.Rows.Add($"AW-{item.Id:D6}", item.Severity, item.Score.ToString("D2"),
                published, detected, string.IsNullOrWhiteSpace(item.Actors) ? "UNATTRIBUTED" : item.Actors,
                string.IsNullOrWhiteSpace(item.Indicators) ? "—" : item.Indicators,
                item.Title.ToUpperInvariant(), item.FeedName.ToUpperInvariant(), item.SourceWeight);
            _eventsGrid.Rows[rowIndex].Tag = item;
        }
        foreach (DataGridViewColumn column in _eventsGrid.Columns)
            column.HeaderCell.SortGlyphDirection = column.Name == _sortColumn ? _sortDirection : SortOrder.None;
    }

    private void OpenAssessment(string? sourceUrl = null, string? eventId = null)
    {
        var console = new AssessmentForm(_storage, _config, sourceUrl, eventId);
        console.AssessmentUpdated += assessment => { _assessment = assessment; RefreshDashboard(); };
        console.Show(this);
    }
    private void OpenIgnoredNews()
    {
        var form = new IgnoredNewsForm(_storage, _config);
        form.AssessmentUpdated += assessment => { _assessment = assessment; RefreshDashboard(); };
        form.Show(this);
    }
    private void OpenArchiveSearch() => new ArticleSearchForm(_storage, _config, _archiveSearch.Text).Show(this);
    private async Task IgnoreNewsAsync(DashboardEvent item)
    {
        if (string.IsNullOrWhiteSpace(item.ArticleHash)) return;
        var reason = IgnoredNewsForm.PromptIgnore(this, item.Title);
        if (reason is null) return;
        var saved = false;
        try
        {
            if (!_storage.SetNewsIgnored(item.ArticleHash, true, reason)) return;
            saved = true;
            RefreshDashboard();
            _cycleSummary.Text = "ARTICLE IGNORED // RECALCULATING SCORE…";
            _assessment = await Task.Run(() => _storage.RefreshAssessment(new AssessmentEngine(_config.Assessment), _config, "MANUAL"));
            if (IsDisposed) return;
            RefreshDashboard();
            _cycleSummary.Text = $"ARTICLE IGNORED FOR SCORE // {_storage.IgnoredNewsCount():N0} IGNORED // INDEX {_assessment.Risk:F2}";
        }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            RefreshDashboard();
            MessageBox.Show(this, (saved ? "The exclusion was saved, but the score could not be recalculated yet. Run a scan to retry." :
                    "The article could not be ignored.") + "\n\n" + ex.Message,
                saved ? "Score refresh failed" : "Ignore failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
    private void OpenOperations(string? tab=null)=>new OperationsForm(_storage,_config,tab).Show(this);

    private void RenderCoverageBadge()
    {
        try
        {
            var health=_storage.FeedHealthRecords().ToDictionary(h=>h.Url,StringComparer.OrdinalIgnoreCase);
            var enabled=_config.Feeds.Where(f=>f.Enabled).ToArray();
            var now=DateTimeOffset.UtcNow;
            var fresh=enabled.Count(f=>health.TryGetValue(f.Url,out var h) && h.LastSuccess is {} last &&
                now-last<=TimeSpan.FromMinutes(Math.Max(60,f.IntervalMinutes*2)));
            _coverageBadge.Text=$"● FEEDS {fresh}/{enabled.Length} FRESH";
            _coverageBadge.ForeColor=fresh==enabled.Length?UiTheme.Cyan:fresh==0?UiTheme.Orange:UiTheme.Yellow;
        }
        catch{_coverageBadge.Text="● COVERAGE UNAVAILABLE";_coverageBadge.ForeColor=UiTheme.Orange;}
    }

    private IEnumerable<DashboardEvent> SortEvents(IEnumerable<DashboardEvent> source)
    {
        IOrderedEnumerable<DashboardEvent> sorted = _sortColumn switch
        {
            "Id" => Order(source, x => x.Id),
            "Severity" => Order(source, x => SeverityRank(x.Severity)),
            "Score" => Order(source, x => x.Score),
            "Published" => Order(source, x => DateTimeOffset.TryParse(x.Published, out var date) ? date : DateTimeOffset.MinValue),
            "Actors" => Order(source, x => x.Actors),
            "Indicators" => Order(source, x => x.Indicators),
            "Event" => Order(source, x => x.Title),
            "Source" => Order(source, x => x.FeedName),
            "Weight" => Order(source, x => x.SourceWeight),
            _ => Order(source, x => x.DetectedAt)
        };
        return sorted.ThenByDescending(x => x.Score).ThenByDescending(x => x.Id);

        IOrderedEnumerable<DashboardEvent> Order<TKey>(IEnumerable<DashboardEvent> items, Func<DashboardEvent, TKey> key) =>
            _sortDirection == SortOrder.Ascending ? items.OrderBy(key) : items.OrderByDescending(key);
    }

    private static int SeverityRank(string severity) => severity switch
    {
        "RED" => 4, "ORANGE" => 3, "YELLOW" => 2, "LOG ONLY" => 1, _ => 0
    };

    private void RenderFeedStates(IReadOnlyList<FeedState> states)
    {
        _feedList.SuspendLayout();
        _feedList.Controls.Clear();
        var online = 0;
        foreach (var state in states)
        {
            if (state.Online) online++;
            var row = new Panel
            {
                Width = Math.Max(220, _feedList.ClientSize.Width - 24),
                Height = 52,
                BackColor = Color.FromArgb(4, 21, 24),
                Margin = new Padding(0, 0, 0, 5),
                Padding = new Padding(10, 5, 8, 4)
            };
            row.Controls.Add(new Label
            {
                Text = state.Online ? $"●  {state.Name.ToUpperInvariant()}" : $"×  {state.Name.ToUpperInvariant()}",
                Dock = DockStyle.Top,
                Height = 22,
                AutoEllipsis = true,
                Font = UiTheme.Label,
                ForeColor = state.Online ? UiTheme.Cyan : UiTheme.Red
            });
            row.Controls.Add(new Label
            {
                Text = state.Online ? $"{state.EntryCount:D3} ENTRIES // {state.Detail}" : "OFFLINE // LINK FAILURE",
                Dock = DockStyle.Bottom,
                Height = 18,
                AutoEllipsis = true,
                Font = UiTheme.Micro,
                ForeColor = UiTheme.Muted
            });
            _feedList.Controls.Add(row);
        }
        _feedList.ResumeLayout();
        _nodeCount.Text = $"{online}/{states.Count}";
    }

    private void RenderAlertCenter()
    {
        var ruleAlerts = _storage.ActionableAssessmentAlerts(20)
            .Select(a => new DashboardEvent(a.Id,"YELLOW",(int)Math.Round(a.Confidence),a.Rule,"ASSESSMENT",a.Timestamp.ToString("O"),"","",0,"",a.Timestamp.UtcDateTime,false,true));
        var signalAlerts = _storage.UnreadAlerts(20);
        var alerts = _localAlerts.Concat(signalAlerts).Concat(ruleAlerts).Take(20).ToList();
        _alertList.SuspendLayout();
        _alertList.Controls.Clear();
        _alertCount.Text = alerts.Count == 0 ? "NO UNREAD ALERTS" : $"{alerts.Count:D2} UNREAD ALERT{(alerts.Count == 1 ? "" : "S")}";

        if (alerts.Count == 0)
        {
            _alertList.Controls.Add(new Label
            {
                Text = "COLLECTION ACTIVE\r\nNO ACTIONABLE SIGNALS PENDING",
                Width = Math.Max(190, _alertList.ClientSize.Width - 20),
                Height = 54,
                Font = UiTheme.Micro,
                ForeColor = UiTheme.Muted,
                TextAlign = ContentAlignment.MiddleCenter
            });
        }

        foreach (var item in alerts)
        {
            var color = UiTheme.Severity(item.Severity);
            var row = new Panel
            {
                Width = Math.Max(210, _alertList.ClientSize.Width - 24),
                Height = 68,
                BackColor = Color.FromArgb(7, 28, 31),
                Margin = new Padding(0, 0, 0, 5),
                Cursor = string.IsNullOrWhiteSpace(item.Url) ? Cursors.Default : Cursors.Hand,
                Tag = item
            };
            row.Controls.Add(new Label { Dock = DockStyle.Left, Width = 4, BackColor = color });
            var dismiss = UiTheme.Button("X");
            dismiss.Dock = DockStyle.Right;
            dismiss.Width = 32;
            dismiss.Font = new Font("Consolas", 10, FontStyle.Bold);
            dismiss.Click += (_, _) =>
            {
                if (item.Id >= 0 && item.IsRuleAlert) _storage.SetAlertState(item.Id,"ACKNOWLEDGED");
                else if (item.Id >= 0) _storage.MarkAlerted(item.Id);
                else _localAlerts.Remove(item);
                RenderAlertCenter();
            };
            row.Controls.Add(dismiss);
            var details = new Label
            {
                Text = $"CONFIDENCE {item.Score:D2}/100 // {item.FeedName.ToUpperInvariant()}",
                Dock = DockStyle.Bottom,
                Height = 21,
                Padding = new Padding(9, 0, 38, 2),
                Font = UiTheme.Micro,
                ForeColor = color
            };
            var title = new Label
            {
                Text = item.Title.ToUpperInvariant(),
                Dock = DockStyle.Fill,
                Padding = new Padding(9, 5, 38, 0),
                Font = UiTheme.Small,
                ForeColor = UiTheme.Text,
                AutoEllipsis = true
            };
            if(item.Id >= 0) { title.Cursor=Cursors.Hand; title.Click += (_,_)=>OpenAssessment(); }
            if (!string.IsNullOrWhiteSpace(item.Url))
            {
                title.Cursor = Cursors.Hand;
                title.Click += (_, _) => OpenFile(item.Url);
                details.Cursor = Cursors.Hand;
                details.Click += (_, _) => OpenFile(item.Url);
            }
            row.Controls.Add(title);
            row.Controls.Add(details);
            _alertList.Controls.Add(row);
        }
        _alertList.ResumeLayout();
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        _clockLabel.Text = now.ToString("HH:mm:ss");
        _gmtLabel.Text = DateTime.UtcNow.ToString("HH:mm:ss");
        _dateLabel.Text = DateTime.UtcNow.ToString("yyyy.MM.dd");
        if (_nextScanUtc == default)
        {
            _nextScanLabel.Text = "NEXT SCAN // INITIAL";
        }
        else
        {
            var remaining = _nextScanUtc - DateTime.UtcNow;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            _nextScanLabel.Text = $"NEXT SCAN // {remaining:mm\\:ss}";
        }
    }

    private void UpdateFilterButtons()
    {
        _allFilter.ForeColor = _criticalOnly ? UiTheme.CyanDim : UiTheme.CyanHot;
        _allFilter.BackColor = _criticalOnly ? UiTheme.Surface : UiTheme.Raised;
        _criticalFilter.ForeColor = _criticalOnly ? UiTheme.Red : UiTheme.CyanDim;
        _criticalFilter.BackColor = _criticalOnly ? UiTheme.Raised : UiTheme.Surface;
    }

    private void MainForm_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.F)
        {
            _archiveSearch.Focus();
            e.SuppressKeyPress = true;
            return;
        }
        if (e.KeyCode == Keys.Escape) Close();
        if (e.KeyCode == Keys.F11) ToggleFullScreen();
        if (e.Control && e.Shift && e.KeyCode is Keys.Right or Keys.Left)
            MoveToNextScreen(e.KeyCode == Keys.Right ? 1 : -1);
    }

    private void ToggleFullScreen()
    {
        if (FormBorderStyle == FormBorderStyle.None) ExitFullScreen();
        else EnterFullScreen();
    }

    private void EnterFullScreen()
    {
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Normal;
        Bounds = Screen.FromControl(this).Bounds;
        TopMost = false;
        _restoreButton.Icon = WindowIcon.Restore;
    }

    private void ExitFullScreen()
    {
        FormBorderStyle = FormBorderStyle.Sizable;
        WindowState = FormWindowState.Normal;
        Size = new Size(1366, 768);
        CenterToScreen();
        _restoreButton.Icon = WindowIcon.Maximize;
    }

    private void MoveToNextScreen(int direction = 1)
    {
        var screens = Screen.AllScreens;
        if (screens.Length < 2) return;
        var current = Array.IndexOf(screens, Screen.FromControl(this));
        var next = screens[(current + direction + screens.Length) % screens.Length];
        if (FormBorderStyle == FormBorderStyle.None)
            Bounds = next.Bounds;
        else
        {
            var width = Math.Min(Width, next.WorkingArea.Width);
            var height = Math.Min(Height, next.WorkingArea.Height);
            Size = new Size(width, height);
            Location = new Point(
                next.WorkingArea.Left + (next.WorkingArea.Width - width) / 2,
                next.WorkingArea.Top + (next.WorkingArea.Height - height) / 2);
        }
    }

    private void EnableWindowDrag(Control control)
    {
        control.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            if (FormBorderStyle == FormBorderStyle.None) ExitFullScreen();
            ReleaseCapture();
            SendMessage(Handle, 0xA1, 0x2, 0);
        };
        control.DoubleClick += (_, _) => ToggleFullScreen();
    }

    private void AdjustResponsiveLayout()
    {
        if (_contentLayout.ColumnStyles.Count < 3) return;
        var compact = ClientSize.Width < 1250;
        _contentLayout.ColumnStyles[0].Width = compact ? 220 : Math.Clamp(ClientSize.Width * .18f, 290, 460);
        _contentLayout.ColumnStyles[2].Width = compact ? 240 : Math.Clamp(ClientSize.Width * .20f, 320, 520);
        if (_headerLayout is { } header)
        {
            // Keep the title, GMT clock, network state and window controls
            // readable when the dashboard is restored on a smaller display.
            header.ColumnStyles[1].Width = compact ? 100 : 40;
            header.ColumnStyles[2].Width = compact ? 0 : 120;
            header.ColumnStyles[4].Width = compact ? 0 : 112;
            header.ColumnStyles[5].SizeType = compact ? SizeType.Absolute : SizeType.Percent;
            header.ColumnStyles[5].Width = compact ? 115 : 28;
            header.ColumnStyles[6].Width = compact ? 0 : 190;
            foreach (var column in new[] { 2, 4, 6 })
                if (header.GetControlFromPosition(column, 0) is { } control) control.Visible = !compact;
        }
        // The event grid retains its frozen identifier columns for rapid operator scrolling.
        // Fill mode is incompatible with frozen DataGridView columns, so only the flashpoint matrix uses it.
        _eventsGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        if (IsHandleCreated) RenderCommandCenter();
    }

    private static Control StackedHeader(string caption, Label value)
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 3, 8, 3) };
        panel.Controls.Add(value);
        panel.Controls.Add(new Label
        {
            Text = caption,
            Dock = DockStyle.Top,
            Height = 17,
            Font = UiTheme.Micro,
            ForeColor = UiTheme.Muted,
            TextAlign = ContentAlignment.MiddleLeft
        });
        return panel;
    }

    private static Control MetricRow(string caption, Label value, Color? color = null)
    {
        if (color.HasValue) value.ForeColor = color.Value;
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(3, 2, 3, 2) };
        panel.Controls.Add(value);
        panel.Controls.Add(new Label
        {
            Text = caption,
            Dock = DockStyle.Fill,
            Font = UiTheme.Micro,
            ForeColor = UiTheme.Muted,
            TextAlign = ContentAlignment.MiddleLeft
        });
        return panel;
    }

    private static Label HeaderLabel(string text, Color? color = null) => new()
    {
        Text = text,
        Dock = DockStyle.Bottom,
        Height = 27,
        Font = UiTheme.Label,
        ForeColor = color ?? UiTheme.Text,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true
    };

    private static Label ValueLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Right,
        Width = 82,
        Font = new Font("Consolas", 13, FontStyle.Bold),
        ForeColor = UiTheme.CyanHot,
        TextAlign = ContentAlignment.MiddleRight
    };

    private static Label HeaderIconLabel(string text)
    {
        var label = new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            Margin = new Padding(3),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = UiTheme.Surface,
            ForeColor = UiTheme.CyanHot,
            Font = new Font("Consolas", 14f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand,
            AutoSize = false
        };
        label.MouseEnter += (_, _) => label.BackColor = UiTheme.Raised;
        label.MouseLeave += (_, _) => label.BackColor = UiTheme.Surface;
        return label;
    }

    private void OpenFile(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _cycleSummary.Text = $"OPEN FAILED // {ex.Message.ToUpperInvariant()}";
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hWnd, int message, int wParam, int lParam);

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            _shutdown.Cancel();
            _clockTimer.Dispose();
            _pollTimer.Dispose();
            _archiveStatsTimer.Dispose();
            _shutdown.Dispose();
            _monitor.Dispose();
            _windowToolTip.Dispose();
        }
        base.Dispose(disposing);
    }
}
