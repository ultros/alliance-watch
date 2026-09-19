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
    private readonly CancellationTokenSource _shutdown = new();

    private readonly Label _clockLabel = HeaderLabel("--:--:--");
    private readonly Label _dateLabel = HeaderLabel("----.--.--");
    private readonly Label _scanStateLabel = HeaderLabel("SYSTEM READY", UiTheme.Cyan);
    private readonly Label _nextScanLabel = HeaderLabel("NEXT SCAN // --:--");
    private readonly Label _articleCount = ValueLabel("000000");
    private readonly Label _matchCount = ValueLabel("0000");
    private readonly Label _redCount = ValueLabel("000");
    private readonly Label _nodeCount = ValueLabel("0/0");
    private readonly Label _cycleSummary = new() { AutoSize = false, Dock = DockStyle.Fill, Font = UiTheme.Small, ForeColor = UiTheme.Muted, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ThreatMeter _threatMeter = new() { Dock = DockStyle.Fill };
    private readonly SignalGraph _signalGraph = new() { Dock = DockStyle.Fill };
    private readonly DataGridView _eventsGrid = new();
    private readonly FlowLayoutPanel _feedList = new();
    private readonly FlowLayoutPanel _alertList = new();
    private readonly TableLayoutPanel _contentLayout = new();
    private readonly Label _alertCount = new() { Dock = DockStyle.Fill, Font = UiTheme.Micro, ForeColor = UiTheme.Muted, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button _scanButton = UiTheme.Button("[ RUN ACTIVE SCAN ]");
    private readonly Button _allFilter = UiTheme.Button("ALL SIGNALS");
    private readonly Button _criticalFilter = UiTheme.Button("CRITICAL");
    private readonly WindowIconButton _restoreButton = new(WindowIcon.Restore);
    private readonly ToolTip _windowToolTip = new() { InitialDelay = 350, ReshowDelay = 100, AutoPopDelay = 4000 };

    private IReadOnlyList<DashboardEvent> _events = [];
    private readonly List<DashboardEvent> _localAlerts = [];
    private bool _scanning;
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
        _threatMeter.Cursor = Cursors.Hand;
        _threatMeter.Click += (_, _) => OpenAssessment();
        _threatMeter.Parent?.Controls.Add(_assessmentStatus);
        ConfigureTimers();
        KeyDown += MainForm_KeyDown;
        Shown += MainForm_Shown;
        FormClosing += (_, _) => _shutdown.Cancel();
        SizeChanged += (_, _) => AdjustResponsiveLayout();
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
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 1, BackColor = Color.Transparent };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));

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
        layout.Controls.Add(StackedHeader("DATE / ZULU", _dateLabel), 3, 0);
        layout.Controls.Add(StackedHeader("NETWORK STATE", _scanStateLabel), 4, 0);
        layout.Controls.Add(StackedHeader("AUTOMATED CYCLE", _nextScanLabel), 5, 0);

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
        _contentLayout.Controls.Add(BuildEventConsole(), 1, 0);
        _contentLayout.Controls.Add(BuildRightRail(), 2, 0);
        return _contentLayout;
    }

    private Control BuildLeftRail()
    {
        var rail = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, BackColor = UiTheme.Void };
        rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 275));
        rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 185));
        rail.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var meterCard = new TelemetryPanel { Caption = "GLOBAL INDICATOR LEVEL", Dock = DockStyle.Fill };
        meterCard.Controls.Add(_threatMeter);
        rail.Controls.Add(meterCard, 0, 0);

        var metrics = new TelemetryPanel { Caption = "ACCUMULATED TELEMETRY", Dock = DockStyle.Fill };
        var metricsLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1 };
        for (var i = 0; i < 4; i++) metricsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        metricsLayout.Controls.Add(MetricRow("ARTICLES INDEXED", _articleCount), 0, 0);
        metricsLayout.Controls.Add(MetricRow("INDICATORS FOUND", _matchCount), 0, 1);
        metricsLayout.Controls.Add(MetricRow("LEGACY HIGH SIGNALS", _redCount, UiTheme.Orange), 0, 2);
        metricsLayout.Controls.Add(MetricRow("FEED NODES ONLINE", _nodeCount), 0, 3);
        metrics.Controls.Add(metricsLayout);
        rail.Controls.Add(metrics, 0, 1);

        var graph = new TelemetryPanel { Caption = "SIGNAL INTENSITY / LAST 32", Dock = DockStyle.Fill };
        graph.Controls.Add(_signalGraph);
        rail.Controls.Add(graph, 0, 2);
        return rail;
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
        toolbar.Controls.Add(new Label
        {
            Text = "DOUBLE-CLICK RECORD FOR EVIDENCE",
            AutoSize = false,
            Width = 290,
            Height = 30,
            Font = UiTheme.Micro,
            ForeColor = UiTheme.Muted,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(12, 3, 0, 0)
        });
        layout.Controls.Add(toolbar, 0, 0);

        ConfigureGrid();
        layout.Controls.Add(_eventsGrid, 0, 1);
        card.Controls.Add(layout);
        UpdateFilterButtons();
        return card;
    }

    private Control BuildRightRail()
    {
        var rail = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, BackColor = UiTheme.Void };
        rail.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        rail.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 172));

        var feeds = new TelemetryPanel { Caption = "REMOTE COLLECTION NODES", Dock = DockStyle.Fill };
        _feedList.Dock = DockStyle.Fill;
        _feedList.FlowDirection = FlowDirection.TopDown;
        _feedList.WrapContents = false;
        _feedList.AutoScroll = true;
        _feedList.BackColor = UiTheme.Surface;
        feeds.Controls.Add(_feedList);
        rail.Controls.Add(feeds, 0, 0);
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
        rail.Controls.Add(alerts, 0, 1);

        var protocols = new TelemetryPanel { Caption = "DETECTION PROTOCOLS", Dock = DockStyle.Fill };
        var protocolText = new Label
        {
            Dock = DockStyle.Fill,
            Font = UiTheme.Small,
            ForeColor = UiTheme.Text,
            Text = "30 VERSIONED PROTOCOLS\r\nRISK / CONFIDENCE / MOMENTUM\r\nCLICK GAUGE FOR EVIDENCE\r\nInternal index, not a probability\r\nClaims require verification",
            TextAlign = ContentAlignment.MiddleLeft
        };
        protocols.Controls.Add(protocolText);
        rail.Controls.Add(protocols, 0, 2);

        var control = new TelemetryPanel { Caption = "OPERATOR CONTROL", Dock = DockStyle.Fill };
        var controlLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4 };
        controlLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        controlLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        controlLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        controlLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
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
        controlLayout.Controls.Add(new Label
        {
            Text = "ESC EXIT // F11 SIZE // CTRL+SHIFT+→ SCREEN",
            Dock = DockStyle.Fill,
            ForeColor = UiTheme.Muted,
            Font = UiTheme.Micro,
            TextAlign = ContentAlignment.BottomCenter
        }, 0, 3);
        control.Controls.Add(controlLayout);
        rail.Controls.Add(control, 0, 3);
        return rail;
    }

    private Control BuildFooter()
    {
        var footer = new TelemetryPanel { Dock = DockStyle.Fill, Padding = new Padding(10, 2, 10, 2), Margin = new Padding(4), Caption = "" };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        layout.Controls.Add(new Label
        {
            Text = "●  LOCAL DATABASE SECURE",
            Dock = DockStyle.Fill,
            ForeColor = UiTheme.Cyan,
            Font = UiTheme.Micro,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
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
        foreach (DataGridViewColumn column in _eventsGrid.Columns)
            column.SortMode = DataGridViewColumnSortMode.Programmatic;
        _eventsGrid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && _eventsGrid.Rows[e.RowIndex].Tag is DashboardEvent item)
                OpenAssessment(item.Url);
        };
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

    private void ConfigureTimers()
    {
        _clockTimer.Tick += (_, _) => UpdateClock();
        _pollTimer.Interval = Math.Max(1000, (int)Math.Min(int.MaxValue, _config.PollMinutes * 60_000));
        _pollTimer.Tick += async (_, _) => await RunScanAsync();
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
        try { _assessment = await Task.Run(() => _storage.RefreshAssessment(new AssessmentEngine(_config.Assessment), _config)); }
        catch(Exception ex) { _cycleSummary.Text="ASSESSMENT INITIALIZATION FAILED // "+ex.Message; }
        RefreshDashboard();
        if(_offline) { _cycleSummary.Text="SYNTHETIC UI TEST // NETWORK COLLECTION DISABLED"; _scanButton.Enabled=false; return; }
        await RunScanAsync();
        _pollTimer.Start();
    }

    private async Task RunScanAsync()
    {
        if (_scanning) return;
        _scanning = true;
        _scanButton.Enabled = false;
        _scanStateLabel.Text = "ACTIVE COLLECTION";
        _scanStateLabel.ForeColor = UiTheme.Yellow;
        _cycleSummary.Text = "LINKING REMOTE NODES // RETRIEVING SOURCE MATERIAL";
        var progress = new Progress<string>(message => _cycleSummary.Text = message);

        try
        {
            var result = await Task.Run(() => _monitor.ScanAsync(progress, _shutdown.Token));
            _assessment = await Task.Run(() => _storage.RefreshAssessment(new AssessmentEngine(_config.Assessment), _config));
            RenderFeedStates(result.FeedStates);
            RefreshDashboard();
            var archive = _storage.ArchiveStats();
            _cycleSummary.Text = $"CYCLE COMPLETE // {result.NewArticles} NEW // {result.Alerts.Count} ALERTS // " +
                $"ARCHIVE {archive.Complete} PAGES + {archive.Images} IMAGES / {archive.Pending} PENDING / " +
                $"{archive.CompressedBytes / 1048576d:F1} MB COMPRESSED";

            RenderAlertCenter();
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _cycleSummary.Text = $"CYCLE FAULT // {ex.Message.ToUpperInvariant()}";
            _localAlerts.Insert(0, new DashboardEvent(-1, "RED", 15,
                $"Monitoring cycle fault: {ex.Message}", "LOCAL SYSTEM", DateTimeOffset.Now.ToString("O"),
                "SYSTEM", "scan failure", 10, "", DateTime.Now, false));
            RenderAlertCenter();
        }
        finally
        {
            _scanning = false;
            _scanButton.Enabled = true;
            _scanStateLabel.Text = "SYSTEM READY";
            _scanStateLabel.ForeColor = UiTheme.Cyan;
            _nextScanUtc = DateTime.UtcNow.AddMinutes(_config.PollMinutes);
        }
    }

    private void RefreshDashboard()
    {
        _events = _storage.RecentEvents();
        var stats = _storage.GetStats();
        _articleCount.Text = stats.TotalArticles.ToString("D6");
        _matchCount.Text = stats.TotalMatches.ToString("D4");
        _redCount.Text = stats.RedCount.ToString("D3");
        _threatMeter.Score = (int)Math.Round(_assessment?.Risk ?? 0);
        if (_assessment is { } a) _assessmentStatus.Text = $"CONFIDENCE {a.Confidence:F0}/100\r\n{a.Momentum}\r\nSTATE {a.Ladder} // {a.Contributions.SelectMany(c => c.Protocols).Distinct().Count()}/30 PROTOCOLS\r\nCLICK GAUGE // WHY SCORE MOVED";
        _signalGraph.Values = stats.RecentScores;
        PopulateGrid();
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

    private void OpenAssessment(string? sourceUrl = null)
    {
        var console = new AssessmentForm(_storage, _config, sourceUrl);
        console.Show(this);
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
        var ruleAlerts = _storage.AssessmentAlerts().Where(a => a.State == "UNREAD" || a.State == "SNOOZED" && a.SnoozedUntil <= DateTimeOffset.UtcNow).Take(20)
            .Select(a => new DashboardEvent(a.Id,"YELLOW",(int)Math.Round(a.Confidence),a.Rule,"ASSESSMENT",a.Timestamp.ToString("O"),"","",0,"",a.Timestamp.UtcDateTime,false));
        var alerts = _localAlerts.Concat(ruleAlerts).Take(20).ToList();
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
                if (item.Id >= 0) _storage.SetAlertState(item.Id,"ACKNOWLEDGED");
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
        _contentLayout.ColumnStyles[0].Width = compact ? 220 : 270;
        _contentLayout.ColumnStyles[2].Width = compact ? 240 : 300;
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
        if (disposing)
        {
            _clockTimer.Dispose();
            _pollTimer.Dispose();
            _shutdown.Dispose();
            _monitor.Dispose();
            _windowToolTip.Dispose();
        }
        base.Dispose(disposing);
    }
}
