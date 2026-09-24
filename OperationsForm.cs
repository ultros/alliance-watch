using System.Diagnostics;

namespace AllianceWatch;

internal sealed record ClaimReport(string RecordId,string Title,string Publisher,string Origin,string Status,bool OriginalReporting,
    DateTimeOffset Published,DateTimeOffset Detected,string Url);

internal sealed class OperationsForm : Form
{
    private readonly Storage _storage;
    private readonly AppConfig _config;
    private readonly TabControl _tabs=new(){Dock=DockStyle.Fill,Multiline=true};
    private readonly Label _status=new(){Dock=DockStyle.Top,Height=42,Padding=new Padding(8),ForeColor=UiTheme.Cyan};
    private readonly DataGridView _coverageGrid=Grid(),_feedGrid=Grid(),_scanGrid=Grid(),_claimsGrid=Grid(),_reportsGrid=Grid(),_challengesGrid=Grid(),
        _watchGrid=Grid(),_reviewGrid=Grid(),_qualityGrid=Grid();
    private readonly Label _coverageSummary=Caption(),_scanSummary=Caption(),_qualitySummary=Caption();
    private readonly TextBox _claimSearch=new(){Width=300,PlaceholderText="Claim, actor, event ID…"};
    private readonly Label _claimCount=Inline("0 / 0");
    private readonly ComboBox _watchKind=new(){Width=125,DropDownStyle=ComboBoxStyle.DropDownList};
    private readonly ComboBox _watchValue=new(){Width=290,DropDownStyle=ComboBoxStyle.DropDown};
    private readonly NumericUpDown _watchConfidence=new(){Minimum=0,Maximum=100,Value=50,Width=60};
    private readonly TextBox _reviewId=new(){Width=310,PlaceholderText="Event family ID"};
    private readonly ComboBox _reviewOutcome=new(){Width=155,DropDownStyle=ComboBoxStyle.DropDownList};
    private readonly TextBox _reviewNotes=new(){Width=420,Height=58,Multiline=true,ScrollBars=ScrollBars.Vertical,PlaceholderText="Reason and source references…"};
    private readonly DateTimePicker _whatAt=new(){Width=175,Format=DateTimePickerFormat.Custom,CustomFormat="yyyy-MM-dd HH:mm 'UTC'",ShowUpDown=true};
    private readonly NumericUpDown _normalization=new(){Minimum=1,Maximum=1000,DecimalPlaces=1,Increment=1,Width=70};
    private readonly NumericUpDown _convergence=new(){Minimum=0,Maximum=100,DecimalPlaces=1,Increment=.5M,Width=65};
    private readonly ComboBox _protocol=new(){Width=220,DropDownStyle=ComboBoxStyle.DropDownList};
    private readonly NumericUpDown _severity=new(){Minimum=0,Maximum=10,DecimalPlaces=1,Increment=.5M,Width=60};
    private readonly ComboBox _origin=new(){Width=220,DropDownStyle=ComboBoxStyle.DropDown};
    private readonly NumericUpDown _sourceQuality=new(){Minimum=0,Maximum=1,DecimalPlaces=2,Increment=.05M,Width=60};
    private readonly TextBox _whatOutput=Readout("Set the controls, then run a local simulation. Live assessments and evidence are not changed.");
    private readonly TextBox _backupOutput=Readout("Create a self-contained SQLite backup or verify an existing one. Verification never replaces the live database.");
    private readonly Button _inspectBackup=UiTheme.Button("INSPECT VERIFIED BACKUP");
    private string? _verifiedBackup;
    private EvidenceEvent[] _records=[];
    private ClaimFamily[] _claims=[];
    private Dictionary<string,EvidenceEvent[]> _claimGroups=new();
    private Assessment? _latest,_previous;
    private string _comparisonBasis="PREVIOUS ASSESSMENT";
    private FeedHealth[] _health=[];
    private CoverageRow[] _coverage=[];
    private bool _loading;
    private int _claimOffset;
    private const int ClaimPageSize=250;

    public OperationsForm(Storage storage,AppConfig config,string? initialTab=null)
    {
        _storage=storage;_config=config;
        Text="AllianceWatch // Operations Workspace";Size=new(1320,820);MinimumSize=new(920,600);
        StartPosition=FormStartPosition.CenterParent;BackColor=UiTheme.Void;ForeColor=UiTheme.Text;Font=UiTheme.Small;
        Controls.Add(_tabs);Controls.Add(_status);
        BuildCoverage();BuildScan();BuildClaims();BuildWatchlist();BuildReview();BuildQuality();BuildWhatIf();BuildBackup();
        if(initialTab!=null && _tabs.TabPages.Cast<TabPage>().FirstOrDefault(p=>p.Text==initialTab) is {} page)_tabs.SelectedTab=page;
        UiToolTips.Enable(this);
        Shown+=async(_,_)=>await RefreshAsync();
        KeyPreview=true;KeyDown+=async(_,e)=>{if(e.KeyCode==Keys.F5){e.Handled=true;await RefreshAsync();}};
    }

    private static DataGridView Grid()
    {
        var grid=new DataGridView{Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,
            RowHeadersVisible=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,MultiSelect=false,
            AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.None,BackgroundColor=UiTheme.Surface,BorderStyle=BorderStyle.None,
            EnableHeadersVisualStyles=false};
        grid.DefaultCellStyle=new(){BackColor=UiTheme.Surface,ForeColor=UiTheme.Text,SelectionBackColor=UiTheme.Raised,
            SelectionForeColor=UiTheme.CyanHot,Font=UiTheme.Small};
        grid.ColumnHeadersDefaultCellStyle=new(){BackColor=UiTheme.Void,ForeColor=UiTheme.Cyan,Font=UiTheme.Small};
        grid.DataBindingComplete+=(_,_)=>{foreach(DataGridViewColumn col in grid.Columns)
            col.Width=col.Name is "Title" or "Finding" or "Action" or "Notes"?390:col.Name is "ClusterId" or "Url"?260:150;};
        return grid;
    }
    private static Label Caption()=>new(){Dock=DockStyle.Top,Height=60,ForeColor=UiTheme.Cyan,Padding=new Padding(8,6,8,2)};
    private static TextBox Readout(string text)=>new(){Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Both,
        WordWrap=true,BorderStyle=BorderStyle.None,BackColor=UiTheme.Surface,ForeColor=UiTheme.Cyan,Font=UiTheme.Small,Text=text};
    private TabPage Page(string title){var page=new TabPage(title){BackColor=UiTheme.Surface,ForeColor=UiTheme.Text,Padding=new Padding(8)};_tabs.TabPages.Add(page);return page;}
    private static WrappingToolbar Bar(int height=40)=>new(){MinimumToolbarHeight=height};
    private static Label Inline(string text)=>new(){Text=text,AutoSize=true,ForeColor=UiTheme.Cyan,Margin=new Padding(7,7,3,3)};

    internal async Task RefreshAsync()
    {
        if(_loading)return;_loading=true;_status.Text="LOADING LOCAL OPERATIONS DATA…";
        try
        {
            var snapshot=await Task.Run(()=>
            {
                var assessments=_storage.AssessmentHistory(2);
                var scanAt=assessments.Length>0?_storage.PreviousCompletedScan(assessments[0].Timestamp):null;
                var baseline=scanAt is {} scanTime?_storage.AssessmentAt(scanTime):assessments.Skip(1).FirstOrDefault();
                var since=DateTimeOffset.UtcNow.AddDays(-7);
                if(baseline!=null && baseline.Timestamp<since)since=baseline.Timestamp;
                var records=_storage.RecentEvidence(since);var health=_storage.FeedHealthRecords();
                var history=_storage.ScoreHistory(2);var at=DateTimeOffset.UtcNow;
                var coverage=OperationsAnalytics.Coverage(_config,health,records,at);
                var groups=OperationsAnalytics.ClaimGroups(records);
                var claims=OperationsAnalytics.Claims(groups);
                var quality=OperationsAnalytics.Quality(_config,health,records,coverage,history,_storage.FutureDatedEvidenceCount(at),at);
                var scanRows=assessments.Length>0 && baseline!=null?OperationsAnalytics.SinceLastAssessment(assessments[0],baseline,records):[];
                var origins=records.Select(e=>e.Source.Origin).Where(s=>s.Length>0).Distinct(StringComparer.OrdinalIgnoreCase).Order().Take(1000).ToArray();
                return (Assessments:assessments,Records:records,Health:health,Coverage:coverage,Groups:groups,Claims:claims,
                    Quality:quality,ScanRows:scanRows,Origins:origins,Watches:_storage.WatchItems(),Reviews:_storage.ReviewHistory(),
                    Baseline:baseline,HasScanBaseline:scanAt!=null && baseline!=null);
            });
            if(IsDisposed)return;
            _latest=snapshot.Assessments.FirstOrDefault();_previous=snapshot.Baseline;
            _comparisonBasis=snapshot.HasScanBaseline?"LAST COMPLETED SCAN":"PREVIOUS ASSESSMENT (NO EARLIER SCAN MARKER)";
            _records=snapshot.Records;_health=snapshot.Health;
            _coverage=snapshot.Coverage;_claimGroups=snapshot.Groups;_claims=snapshot.Claims;
            _coverageGrid.DataSource=_coverage;
            _coverageSummary.Text=$"{_coverage.Count(c=>c.FreshFeeds==c.ConfiguredFeeds && c.ConfiguredFeeds>0)}/{_coverage.Length} theatres have all enabled feeds fresh. " +
                "Verified origins count distinct original reporting, not copied headlines. Select a theatre for its feeds.";
            RefreshFeedRows();RefreshScan(snapshot.ScanRows);RefreshClaims();RefreshWatchRows(snapshot.Watches);RefreshReviewRows(snapshot.Reviews);
            _qualityGrid.DataSource=snapshot.Quality;
            _qualitySummary.Text=snapshot.Quality.Length==0?"NO CURRENT DATA-QUALITY FLAGS // absence of warnings does not imply complete coverage.":
                $"{snapshot.Quality.Count(w=>w.Level=="HIGH")} HIGH / {snapshot.Quality.Count(w=>w.Level=="MEDIUM")} MEDIUM / {snapshot.Quality.Count(w=>w.Level=="LOW")} LOW // Generated from local feed, timestamp and assessment records.";
            RefreshSimulationChoices(snapshot.Origins);
            _status.Text=$"UPDATED {DateTimeOffset.UtcNow:u} // {snapshot.Records.Length:N0} RECENT RECORDS // {_claims.Length:N0} EVENT FAMILIES // F5 REFRESH\r\nOperational views are descriptive. They do not predict conflict or independently validate claims.";
        }
        catch(Exception ex){if(!IsDisposed)_status.Text="OPERATIONS LOAD FAILED // "+ex.Message;}
        finally{_loading=false;}
    }

    private void BuildCoverage()
    {
        var page=Page("COVERAGE");var split=new SplitContainer{Dock=DockStyle.Fill,Orientation=Orientation.Horizontal,SplitterDistance=220};
        UiTheme.KeepSplitReadable(split,.52,130,130);
        split.Panel1.Controls.Add(_coverageGrid);split.Panel2.Controls.Add(_feedGrid);page.Controls.Add(split);page.Controls.Add(_coverageSummary);
        _coverageGrid.SelectionChanged+=(_,_)=>RefreshFeedRows();
    }
    private void RefreshFeedRows()
    {
        var theater=(_coverageGrid.CurrentRow?.DataBoundItem as CoverageRow)?.Theater;
        var feeds=_config.Feeds.Where(f=>f.Enabled && (theater==null || f.Group.StartsWith(theater,StringComparison.OrdinalIgnoreCase)))
            .Select(f=>{var h=_health.FirstOrDefault(x=>x.Url.Equals(f.Url,StringComparison.OrdinalIgnoreCase));return new
            {f.Name,f.Group,LastSuccess=h?.LastSuccess,NextFetch=h?.NextFetch,Status=h?.Status??0,Failures=h?.Failures??0,Items=h?.Items??0,Duplicates=h?.Duplicates??0,Error=h?.Error??"NOT YET FETCHED",f.Url};}).ToArray();
        _feedGrid.DataSource=feeds;
    }
    private void BuildScan()
    {
        var page=Page("SINCE LAST SCAN");page.Controls.Add(_scanGrid);page.Controls.Add(_scanSummary);
        _scanGrid.CellDoubleClick+=(_,e)=>{if(e.RowIndex>=0 && _scanGrid.Rows[e.RowIndex].DataBoundItem is ScanChangeRow row)SelectClaim(row.ClusterId);};
    }
    private void RefreshScan(ScanChangeRow[] rows)
    {
        if(_latest==null || _previous==null){_scanGrid.DataSource=Array.Empty<ScanChangeRow>();_scanSummary.Text="At least two saved assessments are needed for a scan-to-scan comparison.";return;}
        _scanGrid.DataSource=rows;
        var gap=_latest.Timestamp-_previous.Timestamp;
        _scanSummary.Text=$"{_comparisonBasis}: {_previous.Timestamp:u} → {_latest.Timestamp:u} ({gap.TotalMinutes:F1} min) // INDEX {_previous.Risk:F4} → {_latest.Risk:F4} ({_latest.Risk-_previous.Risk:+0.0000;-0.0000;0.0000})\r\n"+
            $"Showing up to 500 leading rows ({rows.Count(r=>r.Kind=="NEW EVENT")} new scored families, {rows.Count(r=>r.Kind is "RETRACTION" or "CORRECTION" or "DISPUTED")} challenge flags shown). Open WHY SCORE MOVED for the full normalized bridge.";
    }
    private void BuildClaims()
    {
        var page=Page("CLAIM COMPARISON");var split=new SplitContainer{Dock=DockStyle.Fill,Orientation=Orientation.Vertical,SplitterDistance=350};
        var compare=new SplitContainer{Dock=DockStyle.Fill,Orientation=Orientation.Vertical,SplitterDistance=260};
        UiTheme.KeepSplitReadable(split,.42,260,440);
        UiTheme.KeepSplitReadable(compare,.50,190,190);
        var support=Caption();support.Height=34;support.Text="SUPPORTING / UNCHALLENGED REPORTS";
        var challenges=Caption();challenges.Height=34;challenges.Text="DISPUTES / CORRECTIONS / RETRACTIONS";
        compare.Panel1.Controls.Add(_reportsGrid);compare.Panel1.Controls.Add(support);
        compare.Panel2.Controls.Add(_challengesGrid);compare.Panel2.Controls.Add(challenges);
        split.Panel1.Controls.Add(_claimsGrid);split.Panel2.Controls.Add(compare);page.Controls.Add(split);
        var bar=Bar();bar.Controls.Add(_claimSearch);var previous=UiTheme.Button("◀ PREVIOUS");var next=UiTheme.Button("NEXT ▶");
        bar.Controls.Add(previous);bar.Controls.Add(next);bar.Controls.Add(_claimCount);
        var review=UiTheme.Button("REVIEW SELECTED CLAIM");bar.Controls.Add(review);page.Controls.Add(bar);
        _claimSearch.TextChanged+=(_,_)=>{_claimOffset=0;RefreshClaims();};
        previous.Click+=(_,_)=>{_claimOffset=Math.Max(0,_claimOffset-ClaimPageSize);RefreshClaims();};
        next.Click+=(_,_)=>{_claimOffset+=ClaimPageSize;RefreshClaims();};
        _claimsGrid.SelectionChanged+=(_,_)=>RefreshReports();
        _reportsGrid.CellDoubleClick+=(_,e)=>{if(e.RowIndex>=0 && _reportsGrid.Rows[e.RowIndex].DataBoundItem is ClaimReport row)OpenSource(row.Url);};
        _challengesGrid.CellDoubleClick+=(_,e)=>{if(e.RowIndex>=0 && _challengesGrid.Rows[e.RowIndex].DataBoundItem is ClaimReport row)OpenSource(row.Url);};
        review.Click+=(_,_)=>{if(_claimsGrid.CurrentRow?.DataBoundItem is ClaimFamily family){_reviewId.Text=family.ClusterId;_tabs.SelectedTab=_tabs.TabPages.Cast<TabPage>().Single(p=>p.Text=="ANALYST REVIEW");}};
    }
    private void RefreshClaims()
    {
        var search=_claimSearch.Text.Trim();
        var filtered=search.Length==0?_claims:_claims.Where(c=>c.Title.Contains(search,StringComparison.OrdinalIgnoreCase)||
            c.Actors.Contains(search,StringComparison.OrdinalIgnoreCase)||c.ClusterId.Contains(search,StringComparison.OrdinalIgnoreCase)).ToArray();
        _claimOffset=Math.Min(_claimOffset,Math.Max(0,((filtered.Length-1)/ClaimPageSize)*ClaimPageSize));
        _claimsGrid.DataSource=filtered.Skip(_claimOffset).Take(ClaimPageSize).ToArray();
        _claimCount.Text=filtered.Length==0?"0 / 0":$"{_claimOffset+1:N0}–{Math.Min(_claimOffset+ClaimPageSize,filtered.Length):N0} / {filtered.Length:N0}";
        RefreshReports();
    }
    private void RefreshReports()
    {
        var id=(_claimsGrid.CurrentRow?.DataBoundItem as ClaimFamily)?.ClusterId;
        var rows=id==null || !_claimGroups.TryGetValue(id,out var family)?Array.Empty<ClaimReport>():family
            .OrderBy(e=>e.Source.FirstSeenAt).Select(e=>new ClaimReport(e.Source.RecordId,e.Source.Title,e.Source.Publisher,e.Source.Origin,
                e.Retraction?"RETRACTION":e.CorrectionOfEventId!=null?"CORRECTION":e.Disputed?"DISPUTED":e.Source.OriginalReporting?"ORIGINAL":"REPORT / COPY",
                e.Source.OriginalReporting,e.Source.PublishedAt,e.Source.FirstSeenAt,e.Source.Url)).ToArray();
        _reportsGrid.DataSource=rows.Where(r=>r.Status is not ("RETRACTION" or "CORRECTION" or "DISPUTED")).ToArray();
        _challengesGrid.DataSource=rows.Where(r=>r.Status is "RETRACTION" or "CORRECTION" or "DISPUTED").ToArray();
    }
    private void SelectClaim(string id)
    {
        _tabs.SelectedTab=_tabs.TabPages.Cast<TabPage>().Single(p=>p.Text=="CLAIM COMPARISON");
        _claimSearch.Text=id;
    }
    private void OpenSource(string url)
    {
        if(!Uri.TryCreate(url,UriKind.Absolute,out var uri) || uri.Scheme is not ("http" or "https"))
        {_status.Text="SOURCE LINK UNAVAILABLE // The selected record has no valid HTTP(S) URL.";return;}
        try{Process.Start(new ProcessStartInfo(url){UseShellExecute=true});}
        catch(Exception ex){_status.Text="COULD NOT OPEN SOURCE // "+ex.Message;}
    }

    private sealed record Choice(string Label,string Value){public override string ToString()=>Label;}
    private void BuildWatchlist()
    {
        var page=Page("PERSONAL WATCHLIST");page.Controls.Add(_watchGrid);
        var bar=Bar(76);bar.Controls.Add(Inline("Follow"));bar.Controls.Add(_watchKind);bar.Controls.Add(_watchValue);
        bar.Controls.Add(Inline("Min confidence"));bar.Controls.Add(_watchConfidence);
        var add=UiTheme.Button("ADD / UPDATE");var toggle=UiTheme.Button("ENABLE / DISABLE");
        bar.Controls.Add(add);bar.Controls.Add(toggle);page.Controls.Add(bar);
        var guidance=Caption();guidance.Dock=DockStyle.Bottom;guidance.Height=50;
        guidance.Text="Alerts require a new or strengthened scored event, raw score ≥1, current evidence and the selected confidence threshold. Historical records are not replayed into new alerts.";
        page.Controls.Add(guidance);
        _watchKind.Items.AddRange(["THEATER","ACTOR","PROTOCOL"]);_watchKind.SelectedIndexChanged+=(_,_)=>RefreshWatchChoices();_watchKind.SelectedIndex=0;
        add.Click+=async(_,_)=>
        {
            try
            {
                var value=(_watchValue.SelectedItem as Choice)?.Value??_watchValue.Text.Trim();
                if(_watchKind.Text=="PROTOCOL" && value.StartsWith('P'))value=value[1..].Split(' ',StringSplitOptions.RemoveEmptyEntries)[0];
                if(_watchKind.Text=="THEATER")value=TheaterCatalog.All.FirstOrDefault(t=>t.Name.Equals(value,StringComparison.OrdinalIgnoreCase))?.Name
                    ??throw new ArgumentException("Choose a configured theatre.");
                if(_watchKind.Text=="ACTOR")value=_config.Assessment.ActorAliases.Keys.FirstOrDefault(a=>a.Equals(value,StringComparison.OrdinalIgnoreCase))
                    ??throw new ArgumentException("Choose an actor recognized by the current rules.");
                _storage.SaveWatchItem(_watchKind.Text,value,(double)_watchConfidence.Value);
                RefreshWatchRows(_storage.WatchItems());_status.Text=$"WATCHLIST SAVED // {_watchKind.Text} {value}";
            }
            catch(Exception ex){_status.Text="WATCHLIST FAILED // "+ex.Message;}
            await Task.CompletedTask;
        };
        toggle.Click+=(_,_)=>
        {
            if(_watchGrid.CurrentRow?.DataBoundItem is not WatchItem item)return;
            try{_storage.SetWatchItemEnabled(item.Id,!item.Enabled);RefreshWatchRows(_storage.WatchItems());}
            catch(Exception ex){_status.Text="WATCHLIST FAILED // "+ex.Message;}
        };
    }
    private void RefreshWatchChoices()
    {
        _watchValue.Items.Clear();
        IEnumerable<Choice> choices=_watchKind.Text switch
        {
            "THEATER"=>TheaterCatalog.All.Select(t=>new Choice(t.Name,t.Name)),
            "ACTOR"=>_config.Assessment.ActorAliases.Keys.Order().Select(a=>new Choice(a,a)),
            "PROTOCOL"=>_config.Assessment.Protocols.Select(p=>new Choice($"P{p.Id:D2} / {p.Name}",p.Id.ToString())),
            _=>[]
        };
        _watchValue.Items.AddRange(choices.Cast<object>().ToArray());
        if(_watchValue.Items.Count>0)_watchValue.SelectedIndex=0;
    }
    private void RefreshWatchRows(WatchItem[] rows)=>_watchGrid.DataSource=rows;

    private void BuildReview()
    {
        _reviewNotes.Tag="review-notes";
        var page=Page("ANALYST REVIEW");page.Controls.Add(_reviewGrid);
        var bar=Bar(112);bar.Controls.Add(_reviewId);
        _reviewOutcome.Items.AddRange(["NEEDS REVIEW","SUPPORTED","DISPUTED","FALSE POSITIVE"]);_reviewOutcome.SelectedIndex=0;
        bar.Controls.Add(_reviewOutcome);bar.Controls.Add(_reviewNotes);
        var useClaim=UiTheme.Button("USE SELECTED CLAIM");var save=UiTheme.Button("SAVE REVIEW");bar.Controls.Add(useClaim);bar.Controls.Add(save);page.Controls.Add(bar);
        var guidance=Caption();guidance.Dock=DockStyle.Bottom;guidance.Height=48;
        guidance.Text="Review history is append-only. Flags are analyst judgments and do not rewrite evidence, suppress articles or alter the live score.";
        page.Controls.Add(guidance);
        useClaim.Click+=(_,_)=>{if(_claimsGrid.CurrentRow?.DataBoundItem is ClaimFamily family)_reviewId.Text=family.ClusterId;};
        save.Click+=(_,_)=>
        {
            try{var review=_storage.SaveReview(_reviewId.Text,_reviewOutcome.Text,_reviewNotes.Text);
                RefreshReviewRows(_storage.ReviewHistory());_status.Text=$"REVIEW SAVED // {review.ClusterId} / {review.Outcome}";_reviewNotes.Clear();}
            catch(Exception ex){_status.Text="REVIEW FAILED // "+ex.Message;}
        };
    }
    private void RefreshReviewRows(AnalystReview[] rows)=>_reviewGrid.DataSource=rows;

    private void BuildQuality()
    {
        var page=Page("DATA QUALITY");page.Controls.Add(_qualityGrid);page.Controls.Add(_qualitySummary);
    }

    private void BuildWhatIf()
    {
        var page=Page("WHAT-IF LAB");page.Controls.Add(_whatOutput);
        var bar=Bar(110);bar.Controls.Add(Inline("As of"));bar.Controls.Add(_whatAt);
        bar.Controls.Add(Inline("Scale"));bar.Controls.Add(_normalization);
        bar.Controls.Add(Inline("Convergence"));bar.Controls.Add(_convergence);
        bar.Controls.Add(Inline("Protocol"));bar.Controls.Add(_protocol);
        bar.Controls.Add(Inline("Severity"));bar.Controls.Add(_severity);
        bar.Controls.Add(Inline("Source origin"));bar.Controls.Add(_origin);
        bar.Controls.Add(Inline("Quality"));bar.Controls.Add(_sourceQuality);
        var run=UiTheme.Button("RUN LOCAL SIMULATION");bar.Controls.Add(run);page.Controls.Add(bar);
        _normalization.Value=(decimal)Math.Clamp(_config.Assessment.NormalizationScale,1,1000);
        _convergence.Value=(decimal)Math.Clamp(_config.Assessment.ConvergenceBonus,0,100);
        _whatAt.Value=DateTime.UtcNow;
        _protocol.Items.Add(new Choice("NO PROTOCOL CHANGE","0"));
        _protocol.Items.AddRange(_config.Assessment.Protocols.Select(p=>(object)new Choice($"P{p.Id:D2} / {p.Name}",p.Id.ToString())).ToArray());
        _protocol.SelectedIndexChanged+=(_,_)=>{var id=int.Parse((_protocol.SelectedItem as Choice)?.Value??"0");
            _severity.Value=id==0?0:(decimal)_config.Assessment.Protocols.First(p=>p.Id==id).Severity;};
        _protocol.SelectedIndex=0;_origin.Items.Add("NO SOURCE CHANGE");_origin.SelectedIndex=0;_sourceQuality.Value=.5M;
        run.Click+=async(_,_)=>
        {
            if(run.Enabled==false)return;run.Enabled=false;_whatOutput.Text="RUNNING LOCAL REPLAY…";
            try
            {
                var at=new DateTimeOffset(DateTime.SpecifyKind(_whatAt.Value,DateTimeKind.Utc));
                if(at>DateTimeOffset.UtcNow.AddMinutes(1))throw new ArgumentException("Choose a UTC time no later than now.");
                var scale=(double)_normalization.Value;var bonus=(double)_convergence.Value;
                var id=int.Parse((_protocol.SelectedItem as Choice)?.Value??"0");var severity=(double)_severity.Value;
                var origin=_origin.Text=="NO SOURCE CHANGE"?"":_origin.Text.Trim();var quality=(double)_sourceQuality.Value;
                var outcome=await Task.Run(()=>
                {
                    var evidence=_storage.LoadEvidence(at,requireComplete:true,forScoring:true);
                    return (Count:evidence.Length,Result:OperationsAnalytics.CompareWhatIf(_config.Assessment,evidence,at,scale,bonus,id,severity,origin,quality));
                });
                if(IsDisposed)return;
                var result=outcome.Result;
                var changes=AssessmentEngine.ExplainChanges(result.Variant,result.Baseline)
                    .Where(c=>c.EventId is not ("normalization" or "convergence")).OrderByDescending(c=>Math.Abs(c.Delta)).Take(12);
                _whatOutput.Text=$"LOCAL WHAT-IF // {at:u} // {outcome.Count:N0} contemporaneously available records\r\n"+
                    $"BASELINE INDEX {result.Baseline.Risk:F3}   SIMULATED {result.Variant.Risk:F3}   DIFFERENCE {result.Variant.Risk-result.Baseline.Risk:+0.000;-0.000;0.000}\r\n"+
                    $"BASELINE CONFIDENCE {result.Baseline.Confidence:F1}   SIMULATED {result.Variant.Confidence:F1}\r\n"+
                    $"Source records adjusted: {result.ChangedSources}; protocol setting adjusted: {(id==0?"none":$"P{id:D2}")}.\r\n\r\n"+
                    "LARGEST RAW COMPONENT DIFFERENCES\r\n"+string.Join("\r\n",changes.Select(c=>$"{c.Delta:+0.0000;-0.0000;0.0000}  {c.Reason}"))+
                    "\r\n\r\nThis is a same-time sensitivity comparison using stored extraction results. It is not a forecast; no live data or rules were changed.";
            }
            catch(Exception ex){if(!IsDisposed)_whatOutput.Text="SIMULATION FAILED // "+ex.Message;}
            finally{if(!IsDisposed)run.Enabled=true;}
        };
    }
    private void RefreshSimulationChoices(string[] origins)
    {
        var selected=_origin.Text;
        _origin.Items.Clear();_origin.Items.Add("NO SOURCE CHANGE");
        _origin.Items.AddRange(origins.Cast<object>().ToArray());
        if(_origin.Items.Contains(selected))_origin.SelectedItem=selected;else _origin.SelectedIndex=0;
    }

    private void BuildBackup()
    {
        var page=Page("BACKUP / RESTORE CHECK");page.Controls.Add(_backupOutput);
        var bar=Bar(76);var create=UiTheme.Button("CREATE VERIFIED BACKUP");var verify=UiTheme.Button("VERIFY BACKUP");
        var restoreTest=UiTheme.Button("TEST RESTORE TO COPY");_inspectBackup.Enabled=false;
        bar.Controls.Add(create);bar.Controls.Add(verify);bar.Controls.Add(restoreTest);bar.Controls.Add(_inspectBackup);page.Controls.Add(bar);
        create.Click+=async(_,_)=>
        {
            using var dialog=new SaveFileDialog{Filter="SQLite database|*.db",FileName=$"AllianceWatch-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db",OverwritePrompt=false};
            if(dialog.ShowDialog(this)!=DialogResult.OK)return;
            await BackupAsync(()=>_storage.CreateVerifiedBackup(dialog.FileName),"BACKUP CREATED AND VERIFIED");
        };
        verify.Click+=async(_,_)=>
        {
            using var dialog=new OpenFileDialog{Filter="SQLite database|*.db;*.sqlite;*.sqlite3|All files|*.*"};
            if(dialog.ShowDialog(this)!=DialogResult.OK)return;
            await BackupAsync(()=>_storage.VerifyBackup(dialog.FileName),"BACKUP VERIFIED");
        };
        restoreTest.Click+=async(_,_)=>
        {
            if(_verifiedBackup==null){_backupOutput.Text="Verify a backup first, then test its restoration into a new copy.";return;}
            using var dialog=new SaveFileDialog{Filter="SQLite database|*.db",FileName=$"AllianceWatch-restore-test-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db",OverwritePrompt=false};
            if(dialog.ShowDialog(this)!=DialogResult.OK)return;
            var source=_verifiedBackup;
            await BackupAsync(()=>new Storage(source).CreateVerifiedBackup(dialog.FileName),"RESTORE COPY CREATED AND VERIFIED");
        };
        _inspectBackup.Click+=(_,_)=>{if(_verifiedBackup!=null)new DatabaseBrowserForm(new Storage(_verifiedBackup)).Show(this);};
    }
    private async Task BackupAsync(Func<BackupCheck> action,string heading)
    {
        _backupOutput.Text="WORKING… A large archive may take several minutes. The live database remains available.";
        try
        {
            var check=await Task.Run(action);if(IsDisposed)return;
            _verifiedBackup=check.Path;_inspectBackup.Enabled=true;
            _backupOutput.Text=$"{heading}\r\n\r\nPATH: {check.Path}\r\nSIZE: {check.Bytes/1048576d:F1} MiB\r\n"+
                $"ARTICLES: {check.Articles:N0}\r\nASSESSMENTS: {check.Assessments:N0}\r\nSQLITE QUICK CHECK: {check.Integrity}\r\n\r\n"+
                "The backup opens read-only and contains the required AllianceWatch tables. Test Restore To Copy creates a separate verified database; it never replaces the live file.";
        }
        catch(Exception ex){if(!IsDisposed)_backupOutput.Text="BACKUP / RESTORE CHECK FAILED // "+ex.Message;}
    }
}
