using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AllianceWatch;

internal sealed class AssessmentForm : Form
{
    private readonly Storage _storage;
    private readonly AppConfig _config;
    private readonly string? _sourceUrl;
    private readonly string? _eventId;
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill, Multiline = true, DrawMode = TabDrawMode.OwnerDrawFixed, Padding = new Point(10,6) };
    private readonly Label _status = new() { Dock = DockStyle.Top, Height = 64, ForeColor = UiTheme.Cyan, Padding = new Padding(8), Text = "LOADING ASSESSMENT…" };
    private EvidenceEvent[] _evidence = [];
    private Assessment[] _history = [];
    private Assessment? _current;
    private HashSet<string> _ignoredIds = new(StringComparer.Ordinal);
    private System.Windows.Forms.Timer _debounce = new() { Interval = 250 };
    private System.Windows.Forms.Timer _playback = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 15000 };
    private readonly List<DataGridView> _grids = [];
    private DataGridView? _changesGrid;
    private ComboBox? _changeRange;
    private Label? _changesSummary;
    private bool _loading;
    private bool _checkingLatest;
    private bool _shownSource;
    private int _changesVersion;
    public event Action<Assessment>? AssessmentUpdated;
    public AssessmentForm(Storage storage, AppConfig config, string? sourceUrl = null, string? eventId = null)
    {
        _storage = storage; _config = config; _sourceUrl = sourceUrl; _eventId = eventId;
        Text = "AllianceWatch // Escalation Assessment Console"; Size = new(1280,820); MinimumSize = new(900,600);
        BackColor = UiTheme.Void; ForeColor = UiTheme.Text; Font = UiTheme.Small; StartPosition = FormStartPosition.CenterParent;
        Controls.Add(_tabs); Controls.Add(_status);
        _tabs.DrawItem += (_,e) =>
        {
            var selected=e.Index==_tabs.SelectedIndex;
            using var brush=new SolidBrush(selected?UiTheme.Raised:UiTheme.Void);e.Graphics.FillRectangle(brush,e.Bounds);
            TextRenderer.DrawText(e.Graphics,_tabs.TabPages[e.Index].Text,Font,e.Bounds,selected?UiTheme.CyanHot:UiTheme.Cyan,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
        };
        UiToolTips.Enable(this);
        Shown += async (_, _) => { await LoadAsync(); _refreshTimer.Start(); };
        _tabs.SelectedIndexChanged += async (_, _) => { if (!_loading && _tabs.SelectedTab?.Text == "WHY SCORE MOVED") await CheckForNewAssessmentAsync(); };
        _refreshTimer.Tick += async (_, _) => { if (_tabs.SelectedTab?.Text == "WHY SCORE MOVED") await CheckForNewAssessmentAsync(); };
    }
    internal async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var data = await Task.Run(() => (_storage.LoadEvidence(), _storage.ScoreHistory(), _storage.FeedHealthRecords(), _storage.AssessmentAlerts(), _storage.AssessmentHistory(1).FirstOrDefault(), _storage.IgnoredRecordIds()));
            if (IsDisposed) return;
            var selectedTab = _tabs.SelectedTab?.Text;
            var selectedRange = _changeRange?.Text;
            ResetTabs();
            (_evidence, _history) = (data.Item1, data.Item2); _current = data.Item5; _ignoredIds = data.Item6;
            _status.Text = _current == null ? "NO ASSESSMENT // Run an active scan" : $"ESCALATION {_current.Risk:F1}/100   CONFIDENCE {_current.Confidence:F0}/100   {_current.Momentum}   {_current.Timestamp:u}\r\nInternal observable-indicator index. Not a probability or forecast. Confidence describes evidence support; coverage may be incomplete.";
            if (_current == null) return;
            BuildVector(); BuildChanges(selectedRange); BuildRecords(); BuildScenarios(); BuildTimeline(); BuildNarrative(); BuildGraph(); BuildHotspots();
            AddGrid("FEED HEALTH", data.Item3.Select(h => new { h.Name, h.Status, h.LastSuccess, h.NextFetch, h.LatencyMs, h.Failures, h.Items, h.Duplicates, Unique = h.Items - h.Duplicates, h.Signals, h.ETag, h.LastModified, h.Error }).ToArray());
            BuildBacktest(); BuildAudit(); BuildAlerts(data.Item4); BuildProtocols(); BuildExports();
            AddGrid("HISTORICAL BASELINE",AssessmentAnalytics.Baselines(_history,_current.Timestamp));
            AddGrid("RATE OF CHANGE",AssessmentAnalytics.Rates(_current,_history));
            BuildImport();
            if (selectedTab != null && _tabs.TabPages.Cast<TabPage>().FirstOrDefault(p => p.Text == selectedTab) is { } restore) _tabs.SelectedTab = restore;
            RefreshChanges();
            if (_eventId != null && !_shownSource)
            {
                _shownSource = true;
                ShowCluster(_eventId);
            }
            else if (_sourceUrl != null && !_shownSource)
            {
                _shownSource = true;
                var e = _evidence.FirstOrDefault(e => e.Source.Url == _sourceUrl);
                if (e != null) ShowRecord(e); else _status.Text += "\r\nRecord normalization pending; scan to include this record.";
            }
        }
        catch (Exception ex) { if (!IsDisposed) _status.Text = "ASSESSMENT LOAD FAILED // " + ex.Message; }
        finally { _loading = false; }
    }
    private void ResetTabs()
    {
        ++_changesVersion;
        _debounce.Dispose(); _playback.Dispose();
        _debounce = new() { Interval = 250 }; _playback = new() { Interval = 1000 };
        foreach (var page in _tabs.TabPages.Cast<TabPage>().ToArray()) page.Dispose();
        _tabs.TabPages.Clear(); _grids.Clear();
        _changesGrid = null; _changeRange = null; _changesSummary = null;
    }
    private async Task CheckForNewAssessmentAsync()
    {
        if (_checkingLatest || _loading || IsDisposed) return;
        _checkingLatest = true;
        try
        {
            var latest = await Task.Run(_storage.LatestAssessmentTimestamp);
            if (!IsDisposed && latest != null && latest != _current?.Timestamp) await LoadAsync();
        }
        catch (Exception ex) { if (!IsDisposed) _status.Text = "ASSESSMENT REFRESH FAILED // " + ex.Message; }
        finally { _checkingLatest = false; }
    }
    private TabPage Page(string title)
    {
        var page = new TabPage(title) { BackColor = UiTheme.Surface, ForeColor = UiTheme.Text, Padding = new Padding(8) };
        _tabs.TabPages.Add(page); return page;
    }
    private static TextBox Readout(string text) => new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, BackColor = UiTheme.Surface, ForeColor = UiTheme.Cyan, Font = UiTheme.Small, BorderStyle = BorderStyle.None, Text = text };
    private DataGridView Grid()
    {
        // Content-based width measurement walks many cells whenever a large tab binds.
        // Fixed initial widths keep the analytical console responsive; columns remain user-resizable.
        var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoGenerateColumns = true, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None, BackgroundColor = UiTheme.Surface, BorderStyle = BorderStyle.None, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, EnableHeadersVisualStyles = false };
        grid.DefaultCellStyle = new() { BackColor = UiTheme.Surface, ForeColor = UiTheme.Text, SelectionBackColor = UiTheme.Raised, SelectionForeColor = UiTheme.CyanHot, Font = UiTheme.Small };
        grid.ColumnHeadersDefaultCellStyle = new() { BackColor = UiTheme.Void, ForeColor = UiTheme.Cyan, Font = UiTheme.Small };
        _grids.Add(grid); return grid;
    }
    private DataGridView AddGrid<T>(string name, T[] rows)
    {
        var g = Grid(); g.DataSource = rows; Page(name).Controls.Add(g); return g;
    }
    private void BuildVector()
    {
        var a = _current!;
        var prior = _history.Skip(1).FirstOrDefault();
        var text = new StringBuilder("ESCALATION VECTOR\r\n\r\n");
        foreach (var v in a.Vector) text.AppendLine($"{v.Key,-6} {(v.Value is double d ? d.ToString("+0.00;-0.00;0.00") : "INSUFFICIENT HISTORY")} points");
        text.AppendLine($"\r\nMOMENTUM       {a.Momentum}\r\nCONFIDENCE     {a.Confidence:F1}/100\r\nRISK           {a.Risk:F2}/100\r\nRAW SCORE      {a.RawScore:F4}\r\nCONVERGENCE    {a.Convergence:F2} raw points\r\nCURRENT STATE  {a.Ladder} / {Ladder[a.Ladder]}\r\nPREVIOUS STATE {prior?.Ladder.ToString() ?? "UNKNOWN"}\r\nSTATE CHANGE   {(prior == null ? "UNKNOWN" : (a.Ladder-prior.Ladder).ToString("+0;-0;0"))}");
        var top = a.Contributions.OrderByDescending(c => c.RawScore).FirstOrDefault();
        text.AppendLine($"PRIMARY DRIVER {top?.Title ?? "NO ACTIVE EVIDENCE"}\r\n\r\nLADDER: descriptive rule classification, not a forecast. States 7–10 require analyst verification.\r\nConfidence measures support for collected claims, not completeness of real-world coverage.\r\nAbsence of companion indicators moderates the index; it is not proof of safety.\r\n\r\nRULES {a.Version}\r\nSETTINGS HASH {a.SettingsHash}");
        Page("ESCALATION VECTOR").Controls.Add(Readout(text.ToString()));
    }
    private static readonly string[] Ladder = ["NORMAL COMPETITION", "DIPLOMATIC FRICTION", "COERCIVE SIGNALING", "FORCE POSITIONING", "LIMITED MOBILIZATION", "THEATER PREPARATION", "DIRECT MILITARY CONTACT", "REGIONAL INTERSTATE WAR", "ALLIANCE ACTIVATION", "MULTI-THEATER GREAT-POWER CONFLICT", "SYSTEMIC WAR"];
    internal static DateTimeOffset? ChangeBaseline(Assessment current, IReadOnlyList<Assessment> history, string range)
    {
        var prior = history.Where(a => a.Timestamp < current.Timestamp).ToArray();
        if (range == "LAST GAUGE MOVE")
            return prior.FirstOrDefault(a => Math.Round(a.Risk) != Math.Round(current.Risk))?.Timestamp ?? prior.FirstOrDefault()?.Timestamp;
        if (range == "PREVIOUS SNAPSHOT") return prior.FirstOrDefault()?.Timestamp;
        var hours = range switch { "1 HOUR" => 1, "6 HOURS" => 6, "24 HOURS" => 24, "7 DAYS" => 168, _ => 0 };
        return hours == 0 ? null : prior.FirstOrDefault(a => a.Timestamp <= current.Timestamp.AddHours(-hours))?.Timestamp;
    }
    private void BuildChanges(string? selectedRange)
    {
        // Hidden tab pages may defer binding and auto-generated columns until selected.
        // Define this grid's schema before assigning its data or configuring columns.
        var g = Grid(); _changesGrid = g;
        g.AutoGenerateColumns=false;
        g.AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.None;
        foreach(var name in new[]{"Delta","Reason","Before","After","EventId"})
        {
            var column=new DataGridViewTextBoxColumn{Name=name,HeaderText=name=="Delta"?"RAW Δ":name,DataPropertyName=name,Width=name=="EventId"?160:100};
            if(name=="Reason"){column.AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill;column.MinimumWidth=240;}
            if(name is "Before" or "After" or "Delta")column.DefaultCellStyle.Format="0.00000000";
            g.Columns.Add(column);
        }
        var page=Page("WHY SCORE MOVED"); page.Controls.Add(g);
        g.CellDoubleClick += (_,e) => { if(e.RowIndex >= 0) { var id=Convert.ToString(g.Rows[e.RowIndex].Cells["EventId"].Value); if(id is not ("convergence" or "normalization") && id != null) ShowCluster(id); } };
        var label = new Label { Dock = DockStyle.Top, Height = 62, Text = "LOADING SCORE COMPARISON…", ForeColor = UiTheme.Cyan }; _changesSummary=label; page.Controls.Add(label);
        var bar=new FlowLayoutPanel { Dock=DockStyle.Top, Height=40, WrapContents=false };
        var range=new ComboBox { Width=200, DropDownStyle=ComboBoxStyle.DropDownList }; _changeRange=range;
        range.Items.AddRange(["LAST GAUGE MOVE","PREVIOUS SNAPSHOT","1 HOUR","6 HOURS","24 HOURS","7 DAYS"]);
        range.SelectedItem=range.Items.Cast<string>().FirstOrDefault(x=>x==selectedRange)??"LAST GAUGE MOVE";
        range.SelectedIndexChanged+=(_,_)=>RefreshChanges();
        var refresh=UiTheme.Button("REFRESH SCORE"); refresh.Click+=async (_,_)=>await LoadAsync();
        bar.Controls.Add(range);bar.Controls.Add(refresh);page.Controls.Add(bar);
    }
    private void RefreshChanges()
    {
        if (_current == null || _changeRange == null || _changesGrid == null || _changesSummary == null) return;
        var version=++_changesVersion;
        var current=_current;
        var range=_changeRange.Text;
        var baselineAt=ChangeBaseline(current,_history,range);
        if (baselineAt == null)
        {
            _changesGrid.DataSource=Array.Empty<ScoreChange>();
            _changesSummary.Text=$"LATEST {current.Timestamp:u} // INDEX {current.Risk:F6}/100\r\nNo earlier assessment is available for {range.ToLowerInvariant()}. Select a shorter comparison window.";
            return;
        }
        try
        {
            // One local row (~70 KiB in a large archive) is cheaper than
            // an extra async UI hand-off and cannot leave a half-bound tab.
            var baseline=_storage.AssessmentAt(baselineAt.Value);
            if (IsDisposed || version!=_changesVersion || _changesGrid.IsDisposed) return;
            if (baseline == null)
            {
                _changesGrid.DataSource=Array.Empty<ScoreChange>();
                _changesSummary.Text="Comparison snapshot is unavailable. Choose another interval.";
                return;
            }
            var changes=AssessmentEngine.ExplainChanges(current,baseline);
            _changesGrid.DataSource=changes.OrderBy(c=>c.EventId is "convergence" or "normalization" ? 1 : 0)
                .ThenByDescending(c=>Math.Abs(c.Delta)).ToArray();
            var net=current.Risk-baseline.Risk;
            var fallback=range=="LAST GAUGE MOVE" && Math.Round(current.Risk)==Math.Round(baseline.Risk) ? " // NO DIFFERENT GAUGE READING IN HISTORY" : "";
            _changesSummary.Text=$"{range}: {baseline.Risk:F6} → {current.Risk:F6} / 100   NET {net:+0.000000;-0.000000;0.000000} INDEX POINTS{fallback}\r\n{baseline.Timestamp:u} → {current.Timestamp:u} // Rows show raw points; convergence and normalization reconcile to the index. Double-click an event for sources.";
        }
        catch(Exception ex) { if(!IsDisposed && version==_changesVersion) _changesSummary.Text="COMPARISON FAILED // "+ex.Message; }
    }
    private void BuildRecords()
    {
        var page = Page("EVIDENCE SEARCH"); var grid = Grid(); page.Controls.Add(grid);
        var bar = new WrappingToolbar { MinimumToolbarHeight = 72 };
        var search = new TextBox { Width = 260, PlaceholderText = "Headline, source, actor, ID, protocol…" };
        var scenario = new ComboBox { Width = 150, DropDownStyle = ComboBoxStyle.DropDownList }; scenario.Items.Add("ALL SCENARIOS"); scenario.Items.AddRange(_config.Assessment.Scenarios.Select(s => (object)s.Id).ToArray()); scenario.Items.Add("unassigned"); scenario.SelectedIndex = 0;
        var sourceClass = new ComboBox { Width = 100, DropDownStyle = ComboBoxStyle.DropDownList }; sourceClass.Items.AddRange(["ALL CLASSES", "A", "B", "C", "D"]); sourceClass.SelectedIndex = 0;
        var corroborated = new CheckBox { Text = "Corroborated", AutoSize = true }; var disputed = new CheckBox { Text = "Contradictions", AutoSize = true };
        var minConfidence = new NumericUpDown { Minimum = 0, Maximum = 100, Width = 55 }; var minSeverity = new NumericUpDown { Minimum = 0, Maximum = 10, Width = 45 };
        var next = UiTheme.Button("NEXT 250"); var previous = UiTheme.Button("PREVIOUS"); var count = new Label { AutoSize = true }; int offset = 0;
        bar.Controls.AddRange([search,scenario,sourceClass,corroborated,disputed,new Label{Text="Min confidence",AutoSize=true},minConfidence,new Label{Text="Min severity",AutoSize=true},minSeverity,previous,next,count]); page.Controls.Add(bar);
        EvidenceEvent[] visible = [];
        void Apply()
        {
            var components = _current!.Contributions.ToDictionary(c => c.EventId);
            var rows = _evidence.Where(e =>
                (scenario.SelectedIndex == 0 || e.Scenarios.Contains(scenario.Text)) && (sourceClass.SelectedIndex == 0 || e.Source.Tier == sourceClass.Text) &&
                (!disputed.Checked || e.Disputed || e.Retraction) && (!corroborated.Checked || components.GetValueOrDefault(e.ClusterId)?.IndependentSources >= 2) &&
                (components.GetValueOrDefault(e.ClusterId)?.Confidence ?? 0) >= (double)minConfidence.Value && e.Severity >= (double)minSeverity.Value &&
                ($"{e.Source.Title} {e.Source.Publisher} {e.Source.RecordId} {string.Join(' ', e.Actors)} {string.Join(' ',e.Geography)} {string.Join(' ',e.Scenarios)} {string.Join(' ',e.Protocols.Select(p => _config.Assessment.Protocols.First(x => x.Id == p).Name))}".Contains(search.Text, StringComparison.OrdinalIgnoreCase))).ToArray();
            offset = Math.Min(offset, Math.Max(0,(rows.Length-1)/250*250)); visible = rows.Skip(offset).Take(250).ToArray();
            grid.DataSource = visible.Select(e => new { e.EventId, e.Source.Title, Publisher = e.Source.Publisher, Published = e.Source.PublishedAt, IgnoredForScore = _ignoredIds.Contains(e.Source.RecordId), Actors = string.Join(" / ",e.Actors), Scenarios = string.Join(",",e.Scenarios), Protocols = string.Join(",", e.Protocols), e.Severity, Confidence = components.GetValueOrDefault(e.ClusterId)?.Confidence ?? 0, e.Disputed }).ToArray();
            count.Text = $"{offset + visible.Length}/{rows.Length} // archive loaded {_evidence.Length}";
        }
        _debounce.Tick += (_,_) => { _debounce.Stop(); Apply(); };
        search.TextChanged += (_,_) => { offset=0; _debounce.Stop(); _debounce.Start(); };
        scenario.SelectedIndexChanged += (_,_) => { offset=0; Apply(); }; sourceClass.SelectedIndexChanged += (_,_) => { offset=0; Apply(); };
        corroborated.CheckedChanged += (_,_) => Apply(); disputed.CheckedChanged += (_,_) => Apply(); minConfidence.ValueChanged += (_,_) => Apply(); minSeverity.ValueChanged += (_,_) => Apply();
        next.Click += (_,_) => { offset+=250; Apply(); }; previous.Click += (_,_) => { offset=Math.Max(0,offset-250); Apply(); };
        EvidenceEvent? RecordAtRow(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= grid.Rows.Count) return null;
            var id = grid.Rows[rowIndex].Cells[nameof(EvidenceEvent.EventId)].Value?.ToString();
            return visible.FirstOrDefault(record => record.EventId == id);
        }
        EvidenceEvent? contextRecord = null;
        var menu = new ContextMenuStrip();
        var ignore = new ToolStripMenuItem("IGNORE THIS ARTICLE FOR SCORE…");
        var restore = new ToolStripMenuItem("RESTORE THIS ARTICLE TO SCORE…");
        var reviewIgnored = new ToolStripMenuItem("VIEW IGNORED NEWS / RESTORE…");
        ignore.Click += async (_, _) => { if (contextRecord is { } record) await SetRecordIgnoredAsync(record, true); };
        restore.Click += async (_, _) => { if (contextRecord is { } record) await SetRecordIgnoredAsync(record, false); };
        reviewIgnored.Click += (_, _) =>
        {
            var ignored = new IgnoredNewsForm(_storage, _config);
            ignored.AssessmentUpdated += assessment => { _current = assessment; AssessmentUpdated?.Invoke(assessment); _ = LoadAsync(); };
            ignored.Show(this);
        };
        menu.Items.AddRange([ignore, restore, new ToolStripSeparator(), reviewIgnored]);
        menu.Opening += (_, _) =>
        {
            var selected = contextRecord is not null;
            ignore.Enabled = selected && !_ignoredIds.Contains(contextRecord!.Source.RecordId);
            restore.Enabled = selected && _ignoredIds.Contains(contextRecord!.Source.RecordId);
        };
        grid.CellMouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            contextRecord = RecordAtRow(e.RowIndex);
            if (e.RowIndex >= 0)
            {
                grid.ClearSelection();
                grid.Rows[e.RowIndex].Selected = true;
                grid.CurrentCell = grid.Rows[e.RowIndex].Cells[Math.Max(0,e.ColumnIndex)];
            }
        };
        grid.ContextMenuStrip = menu;
        grid.CellDoubleClick += (_,e) => { if (RecordAtRow(e.RowIndex) is { } record) ShowRecord(record); }; Apply();
    }
    private async Task SetRecordIgnoredAsync(EvidenceEvent record, bool ignored)
    {
        string reason;
        if (ignored)
        {
            var input = IgnoredNewsForm.PromptIgnore(this, record.Source.Title);
            if (input is null) return;
            reason = input;
        }
        else
        {
            if (MessageBox.Show(this, $"Restore this article to new scores?\n\n{record.Source.Title}", "Restore ignored news",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            reason = "Restored by operator";
        }
        var saved = false;
        try
        {
            if (!_storage.SetNewsIgnored(record.Source.RecordId, ignored, reason)) return;
            saved = true;
            _status.Text = ignored ? "ARTICLE IGNORED // RECALCULATING SCORE…" : "ARTICLE RESTORED // RECALCULATING SCORE…";
            var assessment = await Task.Run(() => _storage.RefreshAssessment(new AssessmentEngine(_config.Assessment), _config, "MANUAL"));
            if (IsDisposed) return;
            AssessmentUpdated?.Invoke(assessment);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                if (saved) await LoadAsync();
                MessageBox.Show(this, (saved ? "The change was saved, but the score could not be recalculated yet. Run a scan to retry." :
                        "The selected article could not be changed.") + "\n\n" + ex.Message,
                    saved ? "Score refresh failed" : "Article update failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
    private void ShowCluster(string id)
    {
        var members = _evidence.Where(e => e.ClusterId == id).ToArray();
        var e = members.FirstOrDefault(e => !_ignoredIds.Contains(e.Source.RecordId)) ?? members.FirstOrDefault();
        if (e != null) ShowRecord(e);
        else _status.Text += "\r\nSupporting records are no longer in the loaded evidence window; use archive search for retained history.";
    }
    private void ShowRecord(EvidenceEvent e)
    {
        var c = _current!.Contributions.FirstOrDefault(c => c.EventId == e.ClusterId);
        var family = _evidence.Where(x => x.ClusterId == e.ClusterId).Select(x => new { x.Source.RecordId, x.Source.Publisher, x.Source.Origin, x.Source.Url, x.Disputed, x.Retraction });
        var details = new { Record = e, IgnoredForScore = _ignoredIds.Contains(e.Source.RecordId), CurrentContribution = c, CorroborationAndSyndicationFamily = family, RelatedEvents = _evidence.Where(x => x.ClusterId != e.ClusterId && x.Actors.Intersect(e.Actors).Any() && x.Protocols.Intersect(e.Protocols).Any()).Take(20).Select(x => new{x.EventId,x.Source.Title,x.Source.Url}), ScoringVersion = _current.Version };
        var form = new Form { Text = e.Source.Title, Size = new(1000,700), BackColor = UiTheme.Void, StartPosition = FormStartPosition.CenterParent };
        form.Controls.Add(Readout(JsonSerializer.Serialize(details,new JsonSerializerOptions{WriteIndented=true})));
        var button = UiTheme.Button("OPEN ORIGINAL SOURCE"); button.Dock = DockStyle.Bottom;
        button.Enabled = Uri.TryCreate(e.Source.Url,UriKind.Absolute,out var uri) && uri.Scheme is "http" or "https";
        button.Click += (_,_) => { try { Process.Start(new ProcessStartInfo(e.Source.Url){UseShellExecute=true}); } catch(Exception ex) { button.Text=ex.Message; } };
        form.Controls.Add(button); UiToolTips.Enable(form); form.Show(this);
    }
    private void BuildScenarios()
    {
        AddGrid("SCENARIOS", _current!.Scenarios.Select(s => new { s.Id, Name = _config.Assessment.Scenarios.FirstOrDefault(c=>c.Id==s.Id)?.Name ?? s.Id, s.Risk, s.Confidence, s.RawScore, s.Events, s.IndependentSources, ActiveProtocols=string.Join(",",s.Protocols),s.Convergence }).ToArray());
    }
    private void BuildTimeline()
    {
        var page = Page("TIMELINE"); var grid = Grid(); page.Controls.Add(grid);
        var plot = new AssessmentPlot { Dock=DockStyle.Top, Height=180 }; page.Controls.Add(plot);
        var range = new ComboBox { Dock=DockStyle.Top,DropDownStyle=ComboBoxStyle.DropDownList }; range.Items.AddRange(["6H","24H","72H","7D","30D","90D","1Y","ALL"]); page.Controls.Add(range);
        Assessment[] displayed = [];
        range.SelectedIndexChanged += (_,_) =>
        {
            var hours = new[]{6,24,72,168,720,2160,8760,int.MaxValue}[range.SelectedIndex];
            displayed = _history.Where(a => hours == int.MaxValue || a.Timestamp >= _current!.Timestamp.AddHours(-hours)).OrderBy(a=>a.Timestamp).ToArray();
            plot.Points = displayed;
            grid.DataSource = displayed.Select(a => new {a.Timestamp,a.Risk,a.Confidence,a.Momentum,Articles=a.ArticleCount,UniqueEvents=a.EventCount,a.ProtocolCount}).ToArray();
        };
        grid.CellDoubleClick += (_,e) => { if(e.RowIndex>=0) ShowAssessment(displayed[e.RowIndex]); };
        plot.Selected += ShowAssessment; range.SelectedIndex=1;
    }
    private async void ShowAssessment(Assessment a)
    {
        a=await Task.Run(()=>_storage.AssessmentAt(a.Timestamp))??a;
        if(IsDisposed)return;
        var f = new Form { Text=$"Assessment {a.Timestamp:u}", Size=new(1000,650), StartPosition=FormStartPosition.CenterParent };
        f.Controls.Add(Readout(JsonSerializer.Serialize(a,new JsonSerializerOptions{WriteIndented=true}))); UiToolTips.Enable(f); f.Show(this);
    }
    private void BuildNarrative()
    {
        var assessment = _current!;
        var at = assessment.Timestamp;
        var days = _evidence.GroupBy(e=>e.Source.PublishedAt.UtcDateTime.Date).ToDictionary(g=>g.Key,g=>g.GroupBy(e=>e.ClusterId).Select(x=>x.First()).ToArray());
        var rows = _config.Assessment.NarrativeTerms.Select(term =>
        {
            var current = _evidence.Where(e=>e.Source.PublishedAt>=at.AddHours(-24)).GroupBy(e=>e.ClusterId).Select(g=>g.First()).ToArray();
            var hits = current.Where(e=>e.NarrativeTerms.Contains(term)).ToArray();
            var frequency = current.Length==0 ? 0 : (double)hits.Length/current.Length;
            var baseline = days.Where(d=>d.Key>=at.UtcDateTime.Date.AddDays(-30) && d.Key<at.UtcDateTime.Date).Select(d=>(double)d.Value.Count(e=>e.NarrativeTerms.Contains(term))/d.Value.Length).ToArray();
            var mean = baseline.DefaultIfEmpty().Average(); var sd = Math.Sqrt(baseline.Select(x=>Math.Pow(x-mean,2)).DefaultIfEmpty().Average());
            return new {Term=term,Frequency=frequency,HistoricalMean=mean,StandardDeviation=sd,ZScore=baseline.Length>=7&&sd>0?((frequency-mean)/sd).ToString("F2"):"INSUFFICIENT BASELINE",Percentile=baseline.Length>=7?(100d*baseline.Count(x=>x<=frequency)/baseline.Length).ToString("F1"):"N/A",ObservedDays=baseline.Length,UniqueSources=hits.Select(e=>e.Source.Origin).Distinct().Count(),KnownLanguages=hits.Where(e=>e.Source.Language!="und").Select(e=>e.Source.Language).Distinct().Count()};
        }).ToArray();
        var g = AddGrid("NARRATIVE INTENSITY",rows); g.Parent!.Controls.Add(new Label{Dock=DockStyle.Top,Height=40,Text="Share of unique event families mentioning each term; 30 observed-day baseline. Missing days are not zeros.\r\nNarrative intensity is a separate channel and does not add risk points.",ForeColor=UiTheme.Cyan});
    }
    private void BuildGraph()
    {
        var page=Page("ACTOR GRAPH"); var grid=Grid(); page.Controls.Add(grid);
        var bar=new FlowLayoutPanel{Dock=DockStyle.Top,Height=36}; var scenario=new ComboBox{Width=180,DropDownStyle=ComboBoxStyle.DropDownList}; scenario.Items.Add("ALL SCENARIOS");scenario.Items.AddRange(_config.Assessment.Scenarios.Select(s=>(object)s.Id).ToArray());scenario.SelectedIndex=0;
        var kind=new ComboBox{Width=180,DropDownStyle=ComboBoxStyle.DropDownList};kind.Items.AddRange(["ALL RELATIONSHIPS","treaty","command","logistics","basing","conflict","statement"]);kind.SelectedIndex=0;
        var geography=new TextBox{Width=160,PlaceholderText="Geography"}; var days=new NumericUpDown{Width=65,Minimum=1,Maximum=3650,Value=30};bar.Controls.AddRange([scenario,kind,geography,new Label{Text="Days",AutoSize=true},days]);page.Controls.Add(bar);
        var graph=new ActorGraphControl{Dock=DockStyle.Top,Height=240};page.Controls.Add(graph);bar.BringToFront();
        void Apply()
        {
            var edges=_evidence.Where(e=>e.Source.PublishedAt>=_current!.Timestamp.AddDays(-(double)days.Value) && (scenario.SelectedIndex==0||e.Scenarios.Contains(scenario.Text)) && string.Join(' ',e.Geography).Contains(geography.Text,StringComparison.OrdinalIgnoreCase)).SelectMany(e=>e.Actors.SelectMany((a,i)=>e.Actors.Skip(i+1).Select(b=>new {From=a,To=b,Relationship=e.Protocols.Contains(1)?"treaty":e.Protocols.Contains(2)?"command":e.Protocols.Contains(4)?"logistics":e.Protocols.Contains(5)?"basing":e.Protocols.Contains(29)?"conflict":"statement",e.EventId,e.Source.Url,At=e.Source.PublishedAt}))).Where(e=>kind.SelectedIndex==0||e.Relationship==kind.Text).Take(1000).ToArray();
            grid.DataSource=edges;graph.Edges=edges.Select(e=>(e.From,e.To)).Distinct().ToArray();
        }
        scenario.SelectedIndexChanged+=(_,_)=>Apply();kind.SelectedIndexChanged+=(_,_)=>Apply();geography.TextChanged+=(_,_)=>Apply();days.ValueChanged+=(_,_)=>Apply();Apply();
        grid.CellDoubleClick+=(_,e)=>{if(e.RowIndex>=0){var id=Convert.ToString(grid.Rows[e.RowIndex].Cells["EventId"].Value);var record=_evidence.FirstOrDefault(x=>x.EventId==id);if(record!=null)ShowRecord(record);}};
        page.Controls.Add(new Label{Dock=DockStyle.Bottom,Height=24,Text="Edges are co-mentioned actors with a detected protocol, not verified directional relationships.",ForeColor=UiTheme.Orange});
    }
    private void BuildHotspots()
    {
        var rows=_evidence.SelectMany(e=>e.Geography.Select(g=>(Geo:g,Event:e))).GroupBy(x=>x.Geo).Select(g=>
        {
            var current=g.Where(x=>x.Event.Source.PublishedAt>=_current!.Timestamp.AddHours(-24)).ToArray();var prior=g.Count(x=>x.Event.Source.PublishedAt>=_current!.Timestamp.AddHours(-48)&&x.Event.Source.PublishedAt<_current.Timestamp.AddHours(-24));
            var ids=current.Select(x=>x.Event.ClusterId).ToHashSet();var components=_current!.Contributions.Where(c=>ids.Contains(c.EventId)).ToArray();
            return new {Geography=g.Key,Events=current.Select(x=>x.Event.ClusterId).Distinct().Count(),IndependentSources=current.Select(x=>x.Event.Source.Origin).Distinct().Count(),Diversity=current.SelectMany(x=>x.Event.Protocols).Distinct().Count(),ArticleDelta24H=current.Length-prior,Confidence=components.Length==0?0:components.Average(c=>c.Confidence)};
        }).ToArray();
        var grid=AddGrid("HOTSPOTS",rows);
        var map=new RegionalHotspotControl{Dock=DockStyle.Top,Height=250,Counts=rows.ToDictionary(r=>r.Geography,r=>r.Events)};
        grid.Parent!.Controls.Add(map);
    }
    private void BuildBacktest()
    {
        var page=Page("BACKTEST");var output=Readout("Choose an as-of timestamp. Replay uses publication AND first-seen cutoff; imported old articles are unavailable before ingestion.");page.Controls.Add(output);
        var bar=new WrappingToolbar{MinimumToolbarHeight=72};var at=new DateTimePicker{Format=DateTimePickerFormat.Custom,CustomFormat="yyyy-MM-dd HH:mm",Width=175,Value=DateTime.UtcNow};var run=UiTheme.Button("REPLAY UTC");var play=UiTheme.Button("PLAY / PAUSE");var speed=new NumericUpDown{Minimum=1,Maximum=168,Value=6,Width=50};var outcome=new ComboBox{Width=150,DropDownStyle=ComboBoxStyle.DropDownList};outcome.Items.AddRange(["FALSE POSITIVE","MISSED INDICATOR","DELAYED DETECTION"]);outcome.SelectedIndex=0;var notes=new TextBox{Width=200,PlaceholderText="Event ID / evaluation notes"};var save=UiTheme.Button("SAVE EVALUATION");
        bar.Controls.AddRange([at,run,play,new Label{Text="Hours / tick",AutoSize=true},speed,outcome,notes,save]);page.Controls.Add(bar);
        var scrub=new TrackBar{Dock=DockStyle.Top,Minimum=0,Maximum=1000,Value=1000,TickFrequency=100};page.Controls.Add(scrub);
        var first=_evidence.Select(e=>e.Source.FirstSeenAt).DefaultIfEmpty(DateTimeOffset.UtcNow).Min();
        scrub.Scroll+=(_,_)=>at.Value=first.AddTicks((long)((DateTimeOffset.UtcNow-first).Ticks*(scrub.Value/1000d))).UtcDateTime;
        bool busy=false;
        async Task Replay()
        {
            if(busy)return;busy=true;run.Enabled=false;
            try
            {
                var timestamp=new DateTimeOffset(DateTime.SpecifyKind(at.Value,DateTimeKind.Utc));
                var result=await Task.Run(()=>new AssessmentEngine(_config.Assessment).Evaluate(_evidence,timestamp));
                if(!IsDisposed)output.Text="CALIBRATION REPLAY // current rules applied to contemporaneously available records\r\n"+JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true});
            }
            catch(Exception ex){if(!IsDisposed)output.Text=ex.Message;}
            finally{busy=false;if(!IsDisposed)run.Enabled=true;}
        }
        run.Click+=async(_,_)=>await Replay();play.Click+=(_,_)=>_playback.Enabled=!_playback.Enabled;
        _playback.Tick+=async(_,_)=>{if(busy)return;if(at.Value.AddHours((double)speed.Value)>DateTime.UtcNow){_playback.Stop();return;}at.Value=at.Value.AddHours((double)speed.Value);await Replay();};
        save.Click+=(_,_)=>{_storage.SaveEvaluation(new DateTimeOffset(DateTime.SpecifyKind(at.Value,DateTimeKind.Utc)),"manual",outcome.Text,notes.Text);save.Text="EVALUATION SAVED";};
    }
    private void BuildAudit()
    {
        var g=AddGrid("ASSESSMENT LOG",_history.Select((a,i)=>new{Index=i,a.Timestamp,a.Version,a.Risk,a.RawScore,a.Delta,a.Confidence,a.SettingsHash}).ToArray());
        g.CellDoubleClick+=(_,e)=>{if(e.RowIndex>=0)ShowAssessment(_history[e.RowIndex]);};
    }
    private void BuildAlerts(AssessmentAlert[] alerts)
    {
        var page=Page("RULE ALERTS");var grid=Grid();page.Controls.Add(grid);
        void Refresh(){alerts=_storage.AssessmentAlerts();grid.DataSource=alerts.Select(a=>new{a.Id,a.Rule,a.Timestamp,a.Confidence,a.State,a.SnoozedUntil,Events=string.Join(",",a.EventIds)}).ToArray();}
        var bar=new FlowLayoutPanel{Dock=DockStyle.Top,Height=38};page.Controls.Add(bar);
        foreach(var action in new[]{"ACKNOWLEDGED","MUTED","SNOOZED"}){var b=UiTheme.Button(action);bar.Controls.Add(b);b.Click+=(_,_)=>{if(grid.CurrentRow==null)return;var id=Convert.ToInt64(grid.CurrentRow.Cells["Id"].Value);_storage.SetAlertState(id,action,action=="SNOOZED"?DateTimeOffset.UtcNow.AddHours(1):null);Refresh();};}
        grid.CellDoubleClick+=(_,e)=>{if(e.RowIndex>=0){var a=alerts[e.RowIndex];foreach(var id in a.EventIds.Take(1))ShowCluster(id);}};Refresh();
    }
    private void BuildProtocols() => AddGrid("PROTOCOLS",_config.Assessment.Protocols.Select(p=>new{p.Id,p.Name,p.Severity,p.HalfLifeHours,p.RequiredIndependentSources,p.Version,p.Description,Patterns=string.Join(" | ",p.Patterns),Exclusions=string.Join(" | ",p.Exclusions),p.SourceRequirements}).ToArray());
    private void BuildExports()
    {
        var page=Page("EXPORT");var bar=new FlowLayoutPanel{Dock=DockStyle.Top,Height=50};page.Controls.Add(bar);
        foreach(var type in new[]{"JSON","CSV","TXT"})
        {
            var b=UiTheme.Button("EXPORT "+type);bar.Controls.Add(b);b.Click+=(_,_)=>
            {
                using var dialog=new SaveFileDialog{Filter=$"{type} files|*.{type.ToLowerInvariant()}",FileName=$"AllianceWatch-{DateTime.UtcNow:yyyyMMdd-HHmmss}.{type.ToLowerInvariant()}"};if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                try{File.WriteAllText(dialog.FileName,AssessmentExport.Render(_current!,_evidence,type),new UTF8Encoding(false));b.Text="SAVED "+type;}catch(Exception ex){_status.Text="EXPORT FAILED // "+ex.Message;}
            };
        }
        page.Controls.Add(new Label{Dock=DockStyle.Bottom,Height=60,Text="Exports contain assessment components, metadata and source links. Full article text and archived images are excluded.\r\nCSV text is protected against spreadsheet formula execution.",ForeColor=UiTheme.Cyan});
    }
    private void BuildImport()
    {
        var page=Page("IMPORT / SETTINGS");
        var text=Readout("Import CSV or a JSON array with title, summary, url, id, published fields.\r\nFirst-seen time is the import time; historical replay will not pretend these records were available earlier.\r\n\r\nConfiguration supports feed adapters rss, atom, json, csv, watch and optional provider JSON mappings.\r\nProvider access and field mappings must match your licensed endpoint. Tokens are read from environment variables.\r\nChange source origin / original_reporting only after checking provenance. Unverified sources cannot independently corroborate one another.\r\n\r\n"+JsonSerializer.Serialize(_config.Assessment,new JsonSerializerOptions{WriteIndented=true}));page.Controls.Add(text);
        var import=UiTheme.Button("IMPORT CSV / JSON");import.Dock=DockStyle.Top;page.Controls.Add(import);
        import.Click+=async(_,_)=>
        {
            using var dialog=new OpenFileDialog{Filter="Metadata files|*.csv;*.json"};if(dialog.ShowDialog(this)!=DialogResult.OK)return;
            import.Enabled=false;
            try
            {
                var path=dialog.FileName;
                var count=await Task.Run(()=>
                {
                    if(new FileInfo(path).Length>8*1024*1024)throw new InvalidDataException("Import limit is 8 MiB; split larger imports.");
                    var feed=new FeedConfig{Name="Manual import",Url="https://example.invalid",Adapter=Path.GetExtension(path)==".csv"?"csv":"json",SourceClass="D"};
                    var entries=SourceAdapters.For(feed.Adapter).Parse(File.ReadAllText(path),feed);var inserted=0;
                    foreach(var e in entries)if(!string.IsNullOrWhiteSpace(e.Title)&&_storage.InsertArticle(AssessmentEngine.Hash(e.Url+"|"+e.Title+"|"+e.Published),feed.Name,e.Title,e.Url,e.Published,e.Summary))inserted++;
                    _storage.RefreshAssessment(new AssessmentEngine(_config.Assessment),_config,"IMPORT");return inserted;
                });
                if(!IsDisposed)text.Text=$"Imported {count} new metadata records. Reopen the console to refresh all views.\r\n"+text.Text;
            }
            catch(Exception ex){if(!IsDisposed)text.Text="IMPORT FAILED // "+ex.Message;}
            finally{if(!IsDisposed)import.Enabled=true;}
        };
    }
    protected override void Dispose(bool disposing){if(disposing){_refreshTimer.Dispose();_debounce.Dispose();_playback.Dispose();}base.Dispose(disposing);}
}

internal static class AssessmentExport
{
    public static string Render(Assessment a, EvidenceEvent[] evidence, string type)
    {
        var ids=a.Contributions.SelectMany(c=>c.RecordIds).ToHashSet();
        var records=evidence.Where(e=>ids.Contains(e.Source.RecordId)).Select(e=>new{e.EventId,e.ClusterId,e.Source.RecordId,e.Source.Title,e.Source.Publisher,e.Source.Url,e.Source.PublishedAt,e.Source.FirstSeenAt,e.Source.Origin,e.Source.Tier,e.Source.Language,e.CanonicalHash,e.Protocols,e.Actors,e.Geography,e.Disputed,e.Retraction}).ToArray();
        if(type=="JSON")return JsonSerializer.Serialize(new{Notice="Internal index; not a probability or prediction.",Assessment=a,Provenance=records},new JsonSerializerOptions{WriteIndented=true});
        if(type=="CSV")return "timestamp,risk,confidence,momentum,event_id,cluster_id,title,publisher,url,published,ingested,origin,tier,protocols,disputed,retraction\r\n"+string.Join("\r\n",records.Select(e=>string.Join(",",new[]{a.Timestamp.ToString("O"),a.Risk.ToString(System.Globalization.CultureInfo.InvariantCulture),a.Confidence.ToString(System.Globalization.CultureInfo.InvariantCulture),a.Momentum,e.EventId,e.ClusterId,e.Title,e.Publisher,e.Url,e.PublishedAt.ToString("O"),e.FirstSeenAt.ToString("O"),e.Origin,e.Tier,string.Join(";",e.Protocols),e.Disputed.ToString(),e.Retraction.ToString()}.Select(Csv))));
        return $"ALLIANCEWATCH ASSESSMENT {a.Timestamp:u}\r\nInternal index; not a probability or prediction.\r\nRisk {a.Risk:F2} / Confidence {a.Confidence:F1} / {a.Momentum}\r\nRules {a.Version} / {a.SettingsHash}\r\n\r\nSCENARIOS\r\n"+string.Join("\r\n",a.Scenarios.Select(s=>$"{s.Id}: risk {s.Risk:F2}, confidence {s.Confidence:F1}"))+"\r\n\r\nSCORE CHANGES\r\n"+string.Join("\r\n",a.Changes.Select(c=>$"{c.Delta:+0.000;-0.000;0.000} {c.Reason} [{c.EventId}]"))+"\r\n\r\nPROVENANCE\r\n"+string.Join("\r\n",records.Select(e=>$"{e.RecordId} | {e.Title} | {e.Publisher} | {e.Url} | published {e.PublishedAt:u} | ingested {e.FirstSeenAt:u} | protocols {string.Join(',',e.Protocols)} | disputed {e.Disputed} / retraction {e.Retraction}"));
    }
    private static string Csv(string s){if(s.TrimStart().StartsWith('=')||s.TrimStart().StartsWith('+')||s.TrimStart().StartsWith('-')||s.TrimStart().StartsWith('@'))s="'"+s;return "\""+s.Replace("\"","\"\"")+"\"";}
}

internal sealed class AssessmentPlot : Control
{
    private Assessment[] _points=[];
    public Assessment[] Points{get=>_points;set{_points=value;Invalidate();}}
    public event Action<Assessment>? Selected;
    public AssessmentPlot(){DoubleBuffered=true;BackColor=UiTheme.Void;MouseClick+=(_,e)=>{if(Points.Length>0)Selected?.Invoke(Points[Math.Clamp((int)Math.Round((double)e.X/Math.Max(1,Width-1)*(Points.Length-1)),0,Points.Length-1)]);};}
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);using var risk=new Pen(UiTheme.Orange,2);using var confidence=new Pen(UiTheme.Cyan,2);
        for(int i=1;i<Points.Length;i++){float x0=(i-1)*Width/(float)Math.Max(1,Points.Length-1),x1=i*Width/(float)Math.Max(1,Points.Length-1);e.Graphics.DrawLine(risk,x0,Height-25-(float)Points[i-1].Risk*(Height-45)/100,x1,Height-25-(float)Points[i].Risk*(Height-45)/100);e.Graphics.DrawLine(confidence,x0,Height-25-(float)Points[i-1].Confidence*(Height-45)/100,x1,Height-25-(float)Points[i].Confidence*(Height-45)/100);}
        TextRenderer.DrawText(e.Graphics,"ORANGE: RISK   CYAN: CONFIDENCE // click to inspect assessment",UiTheme.Micro,new Point(8,Height-20),UiTheme.Text);
    }
}
internal sealed class ActorGraphControl : Control
{
    private (string From,string To)[] _edges=[];
    public (string From,string To)[] Edges{get=>_edges;set{_edges=value;Invalidate();}}
    public ActorGraphControl(){DoubleBuffered=true;BackColor=UiTheme.Void;}
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);var actors=Edges.SelectMany(x=>new[]{x.From,x.To}).Distinct().Take(30).ToArray();var points=actors.Select((a,i)=>(a,p:new PointF(Width/2f+(float)Math.Cos(i*Math.Tau/Math.Max(1,actors.Length))*Width*.36f,Height/2f+(float)Math.Sin(i*Math.Tau/Math.Max(1,actors.Length))*Height*.34f))).ToDictionary(x=>x.a,x=>x.p);
        using var pen=new Pen(UiTheme.CyanDim);foreach(var edge in Edges)if(points.TryGetValue(edge.From,out var a)&&points.TryGetValue(edge.To,out var b))e.Graphics.DrawLine(pen,a,b);
        foreach(var pair in points)TextRenderer.DrawText(e.Graphics,pair.Key,UiTheme.Small,Point.Round(pair.Value),UiTheme.CyanHot,UiTheme.Void);
    }
}
internal sealed class RegionalHotspotControl : Control
{
    public Dictionary<string,int> Counts { get; init; } = new();
    public RegionalHotspotControl(){DoubleBuffered=true;BackColor=UiTheme.Void;}
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);using var grid=new Pen(UiTheme.Grid);using var point=new Pen(UiTheme.Cyan,2);
        PointF Project(double longitude,double latitude)=>new((float)((longitude+180)/360*(Width-40)+20),(float)((90-latitude)/180*(Height-50)+20));
        for(int lon=-180;lon<=180;lon+=60){var a=Project(lon,-90);var b=Project(lon,90);e.Graphics.DrawLine(grid,a,b);}
        for(int lat=-60;lat<=60;lat+=30){var a=Project(-180,lat);var b=Project(180,lat);e.Graphics.DrawLine(grid,a,b);}
        var centers=new[]{("Europe",25d,52d),("East Asia",120d,24d),("Korea",128d,39d),("Middle East",45d,30d),("South Asia",72d,20d)};
        foreach(var (name,lon,lat) in centers){var p=Project(lon,lat);e.Graphics.DrawEllipse(point,p.X-4,p.Y-4,8,8);TextRenderer.DrawText(e.Graphics,$"{name}: {Counts.GetValueOrDefault(name)}",UiTheme.Micro,new Point((int)p.X-40,(int)p.Y+(name=="Korea"?-24:8)),UiTheme.CyanHot,UiTheme.Void);}
        TextRenderer.DrawText(e.Graphics,"REGIONAL SCHEMATIC // 24H EVENT FAMILIES // region centers are not event coordinates",UiTheme.Micro,new Point(8,Height-20),UiTheme.Muted);
    }
}
