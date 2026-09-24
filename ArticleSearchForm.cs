using System.Diagnostics;

namespace AllianceWatch;

internal sealed class ArticleSearchForm : Form
{
    private const int PageSize = 250;
    private readonly Storage _storage;
    private readonly AppConfig _config;
    private readonly TextBox _query = new() { Width = 460, MaxLength = 512, PlaceholderText = "Headline, source, actor, date, archived text, ID…" };
    private readonly Button _search = UiTheme.Button("SEARCH ALL ARTICLES");
    private readonly Button _cancel = UiTheme.Button("CANCEL");
    private readonly Button _previous = UiTheme.Button("◀ PREVIOUS");
    private readonly Button _next = UiTheme.Button("NEXT ▶");
    private readonly Button _openSource = UiTheme.Button("OPEN SOURCE");
    private readonly Button _inspect = UiTheme.Button("INSPECT EVIDENCE");
    private readonly Button _allFields = UiTheme.Button("ALL FIELDS");
    private readonly Label _status = new() { Dock = DockStyle.Top, Height = 36, Padding = new Padding(8, 7, 4, 2), ForeColor = UiTheme.Cyan };
    private readonly DataGridView _grid = new();
    private readonly TextBox _preview = new()
    {
        Dock = DockStyle.Bottom, Height = 185, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Vertical, WordWrap = true, BorderStyle = BorderStyle.None,
        BackColor = UiTheme.Surface, ForeColor = UiTheme.Text, Font = UiTheme.Small
    };
    private CancellationTokenSource? _searchCancellation;
    private ArticleSearchResult[] _results = [];
    private int _offset;
    private int _searchGeneration;
    private int _previewGeneration;
    private int _unreadableArchives;

    internal ArticleSearchForm(Storage storage, AppConfig config, string initialQuery = "")
    {
        _storage = storage;
        _config = config;
        Text = "AllianceWatch // All-Article Archive Search";
        Size = new Size(1390, 800);
        MinimumSize = new Size(900, 540);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = UiTheme.Void;
        ForeColor = UiTheme.Text;
        Font = UiTheme.Small;
        KeyPreview = true;

        _query.Tag = "archive-search-query";
        _query.AccessibleName = "Search all saved articles and archived text";
        _query.Text = initialQuery;
        _search.Width = 176;
        _cancel.Width = 82;
        _previous.Width = 112;
        _next.Width = 85;
        _openSource.Width = 112;
        _inspect.Width = 160;
        _allFields.Width = 110;
        _cancel.Enabled = false;
        var toolbar = new WrappingToolbar { MinimumToolbarHeight = 46, Padding = new Padding(7, 6, 7, 4) };
        toolbar.Controls.AddRange([_query, _search, _cancel, _previous, _next, _openSource, _inspect, _allFields]);
        ConfigureGrid();
        Controls.Add(_grid);
        Controls.Add(_preview);
        Controls.Add(_status);
        Controls.Add(toolbar);
        _status.Text = "ALL SAVED ARTICLES // Includes old and ignored news, linked fields, image metadata and archived text";
        _preview.Text = "Enter a literal search term, or leave the box empty to browse the entire archive. No date limit is applied.";

        _search.Click += async (_, _) => await SearchAsync();
        _cancel.Click += (_, _) => { _searchCancellation?.Cancel(); _status.Text = "CANCELLING ARCHIVE SEARCH…"; };
        _previous.Click += (_, _) => { _offset = Math.Max(0, _offset - PageSize); ShowPage(); };
        _next.Click += (_, _) => { _offset += PageSize; ShowPage(); };
        _openSource.Click += (_, _) => OpenSource();
        _inspect.Click += (_, _) => { if (Selected is { } row) new AssessmentForm(_storage, _config, row.Url).Show(this); };
        _allFields.Click += (_, _) => { if (Selected is { } row) new DatabaseBrowserForm(_storage, row.ArticleHash).Show(this); };
        _query.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await SearchAsync();
        };
        KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.F) { _query.Focus(); e.SuppressKeyPress = true; }
            if (e.KeyCode == Keys.Escape && _searchCancellation is not null) { _searchCancellation.Cancel(); e.SuppressKeyPress = true; }
        };
        _grid.SelectionChanged += async (_, _) => await ShowSelectionAsync();
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) OpenSource(); };
        FormClosing += (_, _) => _searchCancellation?.Cancel();
        Shown += async (_, _) => await SearchAsync();
        UiToolTips.Enable(this);
    }

    private void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.RowHeadersVisible = false;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        _grid.BackgroundColor = UiTheme.Surface;
        _grid.BorderStyle = BorderStyle.None;
        _grid.GridColor = UiTheme.Grid;
        _grid.EnableHeadersVisualStyles = false;
        _grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = UiTheme.Surface, ForeColor = UiTheme.Text,
            SelectionBackColor = UiTheme.Raised, SelectionForeColor = UiTheme.CyanHot, Font = UiTheme.Small };
        _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = UiTheme.Void,
            ForeColor = UiTheme.Cyan, Font = UiTheme.Label };
        _grid.DataBindingComplete += (_, _) =>
        {
            foreach (DataGridViewColumn column in _grid.Columns)
            {
                column.SortMode = DataGridViewColumnSortMode.NotSortable;
                column.Width = column.Name switch
                {
                    nameof(ArticleSearchResult.Title) => 440,
                    nameof(ArticleSearchResult.Summary) => 330,
                    nameof(ArticleSearchResult.Url) => 270,
                    nameof(ArticleSearchResult.ArticleHash) => 220,
                    nameof(ArticleSearchResult.MatchedField) => 170,
                    _ => 145
                };
            }
        };
    }

    private ArticleSearchResult? Selected => _grid.CurrentRow?.DataBoundItem as ArticleSearchResult;

    private async Task SearchAsync()
    {
        _searchCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        var generation = ++_searchGeneration;
        var query = _query.Text;
        _search.Enabled = false;
        _cancel.Enabled = true;
        _status.Text = "SEARCHING COMPLETE LOCAL ARTICLE ARCHIVE…";
        try
        {
            var progress = new Progress<ArticleSearchProgress>(state =>
            {
                if (generation == _searchGeneration && ReferenceEquals(_searchCancellation, cancellation) && !IsDisposed)
                    _status.Text = $"SEARCHING // {state.Scanned:N0}/{state.Total:N0} ARTICLES // {state.Matches:N0} MATCHES";
            });
            var outcome = await Task.Run(() => _storage.SearchArticles(query, progress, cancellation.Token), cancellation.Token);
            if (IsDisposed || generation != _searchGeneration) return;
            _results = outcome.Results;
            _unreadableArchives = outcome.UnreadableArchives;
            _offset = 0;
            ShowPage();
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed && generation == _searchGeneration) _status.Text = "ARCHIVE SEARCH CANCELLED // Previous results remain available";
        }
        catch (Exception ex)
        {
            if (!IsDisposed && generation == _searchGeneration) _status.Text = "ARCHIVE SEARCH FAILED // " + ex.Message;
        }
        finally
        {
            if (generation == _searchGeneration)
            {
                _searchCancellation = null;
                if (!IsDisposed) { _search.Enabled = true; _cancel.Enabled = false; }
            }
            cancellation.Dispose();
        }
    }

    private void ShowPage()
    {
        if (_offset >= _results.Length) _offset = _results.Length == 0 ? 0 : ((_results.Length - 1) / PageSize) * PageSize;
        var page = _results.Skip(_offset).Take(PageSize).ToArray();
        _grid.DataSource = page;
        _previous.Enabled = _offset > 0;
        _next.Enabled = _offset + page.Length < _results.Length;
        _status.Text = $"ALL-ARTICLE SEARCH // {(_results.Length == 0 ? 0 : _offset + 1):N0}–{_offset + page.Length:N0} OF {_results.Length:N0} // OLDER + IGNORED ITEMS INCLUDED" +
            (_unreadableArchives > 0 ? $" // {_unreadableArchives:N0} UNREADABLE ARCHIVE BODY/BODIES" : "");
        if (page.Length == 0) _preview.Text = "No articles match this literal text across saved fields and archived article content.";
    }

    private async Task ShowSelectionAsync()
    {
        var selected = Selected;
        var generation = ++_previewGeneration;
        if (selected is null) return;
        _preview.Text = $"{selected.Title}\r\n{selected.FeedName} // PUBLISHED {selected.Published} // FIRST SEEN {selected.FirstSeen}\r\nMATCHED IN {selected.MatchedField} // {(selected.IgnoredForScore ? "IGNORED FOR SCORE" : "ACTIVE OR LOG-ONLY")}\r\n\r\n{selected.Summary}\r\n\r\nLOADING ARCHIVED TEXT PREVIEW…";
        try
        {
            var body = await Task.Run(() => _storage.ReadArchivedArticleText(selected.ArticleHash));
            if (IsDisposed || generation != _previewGeneration) return;
            _preview.Text = $"{selected.Title}\r\n{selected.FeedName} // PUBLISHED {selected.Published} // FIRST SEEN {selected.FirstSeen}\r\nMATCHED IN {selected.MatchedField} // {(selected.IgnoredForScore ? "IGNORED FOR SCORE" : "ACTIVE OR LOG-ONLY")}\r\n{selected.Url}\r\n\r\n{selected.Summary}\r\n\r\nARCHIVED ARTICLE TEXT\r\n{body}";
        }
        catch (Exception ex)
        {
            if (!IsDisposed && generation == _previewGeneration) _preview.Text += "\r\nARCHIVE PREVIEW FAILED // " + ex.Message;
        }
    }

    private void OpenSource()
    {
        if (Selected is not { } row || !Uri.TryCreate(row.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        try { Process.Start(new ProcessStartInfo(row.Url) { UseShellExecute = true }); }
        catch (Exception ex) { _status.Text = "SOURCE OPEN FAILED // " + ex.Message; }
    }
}
