using System.Data;
using System.IO.Compression;
using Microsoft.Data.Sqlite;

namespace AllianceWatch;

internal sealed record DatabaseView(
    string Label,
    string FromClause,
    string? DateColumn = null,
    bool HasImages = false,
    string SelectColumns = "*",
    string[]? SearchColumns = null,
    string? OrderTieBreaker = null);

internal sealed record ArticleChoice(string Hash, string Display);

internal sealed class DatabaseBrowserForm : Form
{
    private const int PageSize = 500;
    private const int ArticlePickerLimit = 1_000;
    private const int ThumbnailBatchSize = 72;

    private readonly Storage _storage;
    private readonly ComboBox _viewPicker = new() { Width = 232, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _search = new() { Width = 245, PlaceholderText = "Search entire selected dataset…" };
    private readonly DateTimePicker _from = DatePicker();
    private readonly DateTimePicker _to = DatePicker();
    private readonly ComboBox _imageSort = new() { Width = 178, DropDownStyle = ComboBoxStyle.DropDownList, Visible = false };
    private readonly ComboBox _imageGrouping = new() { Width = 158, DropDownStyle = ComboBoxStyle.DropDownList, Visible = false, Tag = "image-grouping" };
    private readonly ComboBox _imageArticlePicker = new() { Width = 260, DropDownStyle = ComboBoxStyle.DropDownList, Visible = false };
    private readonly TextBox _imageManualEntry = new() { Width = 178, PlaceholderText = "Image ID or article hash", Visible = false };
    private readonly Button _galleryToggle = UiTheme.Button("GALLERY VIEW");
    private readonly Button _reloadGallery = UiTheme.Button("RELOAD GALLERY");
    private readonly Button _first = UiTheme.Button("FIRST");
    private readonly Button _previous = UiTheme.Button("◀ PREVIOUS");
    private readonly TextBox _pageEntry = new() { Width = 68, PlaceholderText = "PAGE", TextAlign = HorizontalAlignment.Center };
    private readonly Button _goToPage = UiTheme.Button("GO");
    private readonly Button _next = UiTheme.Button("NEXT ▶");
    private readonly Button _last = UiTheme.Button("LAST");
    private readonly Button _clearFilters = UiTheme.Button("CLEAR FILTERS");
    private readonly Label _status = new() { Dock = DockStyle.Top, Height = 27, ForeColor = UiTheme.Cyan, Font = UiTheme.Micro, Padding = new Padding(7, 4, 7, 2) };
    private readonly DataGridView _grid = new();
    private readonly VirtualImageGallery _gallery = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly PictureBox _preview = new() { Dock = DockStyle.Top, Height = 245, SizeMode = PictureBoxSizeMode.Zoom, BackColor = UiTheme.Void };
    private readonly TextBox _details = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, BackColor = UiTheme.Surface, ForeColor = UiTheme.Text, BorderStyle = BorderStyle.None, Font = UiTheme.Small };
    private readonly System.Windows.Forms.Timer _searchTimer = new() { Interval = 280 };

    private readonly List<DatabaseView> _views =
    [
        new("ARTICLES", "FROM articles", "first_seen", SearchColumns: ["article_hash", "feed_name", "title", "url", "published", "summary"], OrderTieBreaker: "id DESC"),
        new("MATCHES / SIGNALS", "FROM matches m JOIN articles a ON a.article_hash=m.article_hash", "m.detected_at", SearchColumns: ["a.article_hash", "a.feed_name", "a.title", "a.url", "a.summary", "m.severity", "m.matched_phrases", "m.matched_actors"], OrderTieBreaker: "m.id DESC"),
        new("ARTICLE ARCHIVES", "FROM article_archives aa LEFT JOIN articles a ON a.article_hash=aa.article_hash", "aa.fetched_at", SelectColumns: "aa.article_hash,aa.final_url,aa.content_type,aa.html_bytes,aa.text_bytes,aa.compressed_bytes,aa.image_count,aa.fetch_status,aa.last_error,aa.attempts,aa.next_attempt_at,aa.fetched_at,a.feed_name,a.title,a.published", SearchColumns: ["aa.article_hash", "aa.final_url", "aa.content_type", "aa.fetch_status", "aa.last_error", "a.feed_name", "a.title"], OrderTieBreaker: "aa.article_hash DESC"),
        new("ARCHIVED IMAGES", "FROM article_images i LEFT JOIN articles a ON a.article_hash=i.article_hash", "a.published", true, "i.id,i.article_hash,i.position,i.source_url,i.resolved_url,i.mime_type,i.alt_text,i.original_bytes,i.compressed_bytes,i.blob_hash,a.feed_name,a.title,a.published", ["i.article_hash", "i.source_url", "i.resolved_url", "i.mime_type", "i.alt_text", "a.feed_name", "a.title"]),
        new("NORMALIZED EVIDENCE", "FROM aw_events", "first_seen_at", SearchColumns: ["record_id", "event_id", "cluster_id", "canonical_url", "payload"], OrderTieBreaker: "record_id DESC"),
        new("ASSESSMENTS", "FROM aw_assessments", "timestamp", SearchColumns: ["version", "payload"], OrderTieBreaker: "id DESC"),
        new("SCORE HISTORY", "FROM aw_score_history", "timestamp", SearchColumns: ["payload"], OrderTieBreaker: "id DESC"),
        new("ASSESSMENT ALERTS", "FROM aw_alerts", "timestamp", SearchColumns: ["alert_key", "rule", "event_ids", "state"], OrderTieBreaker: "id DESC"),
        new("FEED HEALTH", "FROM aw_feed_health", SearchColumns: ["url", "payload"], OrderTieBreaker: "url ASC"),
        new("FEED FETCH LOG", "FROM aw_feed_fetches", "timestamp", SearchColumns: ["url", "payload"], OrderTieBreaker: "id DESC"),
        new("RULE VERSIONS", "FROM aw_rule_versions", SearchColumns: ["hash", "payload"], OrderTieBreaker: "hash ASC"),
        new("EVALUATIONS", "FROM aw_evaluations", "timestamp", SearchColumns: ["event_id", "outcome", "notes"], OrderTieBreaker: "id DESC"),
        new("ANALYST REVIEWS", "FROM aw_review_log", "timestamp", SearchColumns: ["cluster_id", "outcome", "notes"], OrderTieBreaker: "id DESC"),
        new("PERSONAL WATCHLIST", "FROM aw_watch_items", SearchColumns: ["kind", "value"], OrderTieBreaker: "id DESC"),
        new("ASSESSMENT RUNS", "FROM aw_assessment_runs", "timestamp", SearchColumns: ["trigger_kind"], OrderTieBreaker: "assessment_id DESC"),
        new("IGNORED NEWS", "FROM aw_score_exclusions x JOIN articles a ON a.article_hash=x.record_id AND x.active=1", "x.ignored_at", SelectColumns: "x.record_id,a.title,a.feed_name,a.url,a.published,x.ignored_at,x.reason", SearchColumns: ["x.record_id", "a.title", "a.feed_name", "a.url", "x.reason"], OrderTieBreaker: "x.record_id DESC"),
        new("SCORE EXCLUSION LOG", "FROM aw_score_exclusion_log", "timestamp", SearchColumns: ["record_id", "action", "reason"], OrderTieBreaker: "id DESC"),
        new("ACTOR ALIASES", "FROM actor_aliases", SearchColumns: ["actor", "alias"], OrderTieBreaker: "actor ASC, alias ASC"),
        new("SCHEMA MIGRATIONS", "FROM schema_migrations", "applied_at", SearchColumns: ["version", "applied_at"], OrderTieBreaker: "version DESC")
    ];

    private DataTable? _table;
    private int _offset;
    private long _totalRows;
    private bool _galleryMode;
    private bool _galleryLoading;
    private bool _galleryReloadQueued;
    private bool _thumbnailLoading;
    private bool _thumbnailReloadPending;
    private bool _suppressFilterReload;
    private CancellationTokenSource? _galleryCancellation;
    private CancellationTokenSource? _previewCancellation;
    private bool _loadingImagePicker;
    private string? _manualArticleHash;
    private long? _manualImageId;
    private string? _manualBlobHash;

    public DatabaseBrowserForm(Storage storage, string? initialArticleHash = null)
    {
        _storage = storage;
        Text = "AllianceWatch // Read-Only Database Browser";
        Size = new Size(1500, 880);
        MinimumSize = new Size(1000, 650);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = UiTheme.Void;
        ForeColor = UiTheme.Text;
        Font = UiTheme.Small;
        KeyPreview = true;

        ConfigureGrid();
        _viewPicker.Items.AddRange(_views.Select(view => (object)view.Label).ToArray());
        _viewPicker.SelectedIndex = 0;
        if (!string.IsNullOrWhiteSpace(initialArticleHash)) _search.Text = initialArticleHash;
        _imageSort.Items.AddRange(["DATE (NEWEST)", "DATE (OLDEST)", "POSITION", "LARGEST FILE", "SMALLEST FILE", "COMPRESSED SIZE", "MIME TYPE", "ALT TEXT"]);
        _imageSort.SelectedIndex = 0;
        _imageGrouping.Items.AddRange(["UNIQUE IMAGES", "ARTICLE LINKS"]);
        _imageGrouping.SelectedIndex = 0;
        _from.Checked = false;
        _to.Checked = false;

        var refresh = UiTheme.Button("REFRESH");
        var copyRecord = UiTheme.Button("COPY RECORD");
        var toolbar = new WrappingToolbar { MinimumToolbarHeight = 76, Padding = new Padding(6, 5, 6, 3), BackColor = UiTheme.Void };
        toolbar.Controls.AddRange([
            _viewPicker, Caption("SEARCH"), _search, Caption("FROM"), _from, Caption("TO"), _to,
            _imageSort, _imageGrouping, _imageArticlePicker, _imageManualEntry, _galleryToggle, _reloadGallery,
            refresh, _first, _previous, _pageEntry, _goToPage, _next, _last, _clearFilters, copyRecord
        ]);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 700, BackColor = UiTheme.Grid };
        UiTheme.KeepSplitReadable(split, .68, 480, 290);
        split.Panel1.Controls.Add(_grid);
        split.Panel1.Controls.Add(_gallery);
        split.Panel2.Padding = new Padding(7);
        split.Panel2.Controls.Add(_details);
        split.Panel2.Controls.Add(_preview);
        split.Panel2.Controls.Add(new Label { Dock = DockStyle.Top, Height = 28, Text = "SELECTED RECORD / IMAGE PREVIEW", ForeColor = UiTheme.Cyan, Font = UiTheme.Label, Padding = new Padding(3, 5, 3, 1) });
        Controls.Add(split);
        Controls.Add(_status);
        Controls.Add(toolbar);

        _viewPicker.SelectedIndexChanged += (_, _) => ResetForView();
        _search.TextChanged += (_, _) => QueueSearch();
        _from.ValueChanged += (_, _) => ReloadForFilterChange();
        _to.ValueChanged += (_, _) => ReloadForFilterChange();
        _from.MouseUp += (_, _) => ReloadForFilterChange();
        _to.MouseUp += (_, _) => ReloadForFilterChange();
        _from.KeyUp += (_, _) => ReloadForFilterChange();
        _to.KeyUp += (_, _) => ReloadForFilterChange();
        _imageSort.SelectedIndexChanged += (_, _) => SortImages();
        _imageGrouping.SelectedIndexChanged += (_, _) => { if (_galleryMode) RequestGalleryReload(); };
        _imageArticlePicker.SelectedIndexChanged += (_, _) => { if (_imageArticlePicker.Visible && !_loadingImagePicker) RefreshImageScope(); };
        _imageManualEntry.KeyDown += (_, eventArgs) => { if (eventArgs.KeyCode == Keys.Enter) { eventArgs.SuppressKeyPress = true; ApplyManualImageScope(); } };
        _galleryToggle.Click += (_, _) => SetGalleryMode(!_galleryMode);
        _reloadGallery.Click += (_, _) => RequestGalleryReload();
        refresh.Click += (_, _) => ReloadCurrent();
        _first.Click += (_, _) => { _offset = 0; LoadPage(); };
        _previous.Click += (_, _) => { _offset = Math.Max(0, _offset - PageSize); LoadPage(); };
        _goToPage.Click += (_, _) => GoToPage();
        _pageEntry.KeyDown += (_, eventArgs) => { if (eventArgs.KeyCode == Keys.Enter) { eventArgs.SuppressKeyPress = true; GoToPage(); } };
        _next.Click += (_, _) => { _offset = Math.Min(LastPageOffset, _offset + PageSize); LoadPage(); };
        _last.Click += (_, _) => { _offset = LastPageOffset; LoadPage(); };
        _clearFilters.Click += (_, _) => ClearFilters();
        copyRecord.Click += (_, _) => CopyRecord();
        _grid.SelectionChanged += (_, _) => ShowSelection();
        _gallery.ItemSelected += ShowGallerySelection;
        _gallery.ItemActivated += OpenGalleryImageInTable;
        _gallery.ViewportChanged += QueueVisibleThumbnailLoad;
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); ReloadCurrent(resetPage: true); };
        KeyDown += BrowserKeyDown;
        UiToolTips.Enable(this);
        Shown += (_, _) => LoadPage();
    }

    private static Label Caption(string text) => new() { Text = text, AutoSize = true, ForeColor = UiTheme.Muted, Padding = new Padding(8, 8, 2, 0) };

    private static DateTimePicker DatePicker() => new()
    {
        Width = 165, Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm", ShowCheckBox = true, Value = DateTime.UtcNow
    };

    private void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.RowHeadersVisible = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.EnableHeadersVisualStyles = false;
        _grid.BackgroundColor = UiTheme.Surface;
        _grid.BorderStyle = BorderStyle.None;
        _grid.GridColor = UiTheme.Grid;
        _grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = UiTheme.Surface, ForeColor = UiTheme.Text, SelectionBackColor = UiTheme.Raised, SelectionForeColor = UiTheme.CyanHot, Font = UiTheme.Small, Padding = new Padding(4) };
        _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = UiTheme.Void, ForeColor = UiTheme.Cyan, SelectionBackColor = UiTheme.Void, Font = UiTheme.Label, Padding = new Padding(4) };
        _grid.DataError += (_, _) => { };
    }

    private DatabaseView CurrentView => _views[_viewPicker.SelectedIndex];
    private int CurrentPage => _offset / PageSize + 1;
    private int TotalPages => Math.Max(1, (int)Math.Ceiling(_totalRows / (double)PageSize));
    private int LastPageOffset => _totalRows <= 0 ? 0 : ((int)(_totalRows - 1) / PageSize) * PageSize;

    private void ResetForView()
    {
        _offset = 0;
        _manualArticleHash = null;
        _manualImageId = null;
        _manualBlobHash = null;
        _imageManualEntry.Clear();
        ClearPreview();
        if (_galleryMode && !CurrentView.HasImages) SetGalleryMode(false);
        LoadPage();
    }

    private void ReloadForFilterChange()
    {
        if (_suppressFilterReload || !IsHandleCreated || _viewPicker.SelectedIndex < 0) return;
        _offset = 0;
        ReloadCurrent();
    }

    private void QueueSearch()
    {
        if (_suppressFilterReload || !IsHandleCreated || _viewPicker.SelectedIndex < 0) return;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void ReloadCurrent(bool resetPage = false)
    {
        if (resetPage) _offset = 0;
        if (_galleryMode) RequestGalleryReload();
        else LoadPage();
    }

    private void LoadPage()
    {
        if (!IsHandleCreated || _viewPicker.SelectedIndex < 0) return;
        try
        {
            var view = CurrentView;
            using var connection = _storage.OpenReadOnly();
            _totalRows = CountRows(connection, view);
            if (_offset > LastPageOffset) _offset = LastPageOffset;
            using var command = connection.CreateCommand();
            var where = AddFilters(view, command);
            var order = view.HasImages ? ImageOrder() : TableOrder(view);
            command.CommandText = $"SELECT {view.SelectColumns} {view.FromClause}{WhereClause(where)}{order} LIMIT $limit OFFSET $offset";
            command.Parameters.AddWithValue("$limit", PageSize);
            command.Parameters.AddWithValue("$offset", _offset);
            using var reader = command.ExecuteReader();
            var result = new DataTable();
            result.Load(reader);
            RemoveBinaryColumns(result);
            _table = result;
            _grid.DataSource = result;
            ConfigureBoundColumns();
            UpdateViewControls(view);
            _details.Text = result.Rows.Count == 0 ? "No records match the current scope." : "Select a row to inspect its fields.";
            SetTableStatus(view, result.Rows.Count);
        }
        catch (Exception ex)
        {
            _table = null;
            _grid.DataSource = null;
            _status.Text = "DATABASE BROWSER FAILED // " + ex.Message;
        }
    }

    private long CountRows(SqliteConnection connection, DatabaseView view)
    {
        using var count = connection.CreateCommand();
        var where = AddFilters(view, count);
        count.CommandText = $"SELECT COUNT(*) {view.FromClause}{WhereClause(where)}";
        return Convert.ToInt64(count.ExecuteScalar() ?? 0L);
    }

    private static string TableOrder(DatabaseView view)
    {
        if (view.DateColumn is null) return string.IsNullOrWhiteSpace(view.OrderTieBreaker) ? "" : " ORDER BY " + view.OrderTieBreaker;
        return $" ORDER BY {view.DateColumn} DESC" + (string.IsNullOrWhiteSpace(view.OrderTieBreaker) ? "" : ", " + view.OrderTieBreaker);
    }

    private List<string> AddFilters(DatabaseView view, SqliteCommand command)
    {
        var where = new List<string>();
        if (view.DateColumn is not null && _from.Checked)
        {
            where.Add($"{view.DateColumn} >= $from");
            command.Parameters.AddWithValue("$from", AsUtcText(_from.Value));
        }
        if (view.DateColumn is not null && _to.Checked)
        {
            where.Add($"{view.DateColumn} <= $to");
            command.Parameters.AddWithValue("$to", AsUtcText(_to.Value));
        }
        if (view.HasImages && !string.IsNullOrWhiteSpace(CurrentImageHash))
        {
            where.Add("i.article_hash = $imageHash");
            command.Parameters.AddWithValue("$imageHash", CurrentImageHash);
        }
        if (view.HasImages && _manualImageId is long imageId)
        {
            where.Add("i.id = $imageId");
            command.Parameters.AddWithValue("$imageId", imageId);
        }
        if (view.HasImages && !string.IsNullOrWhiteSpace(_manualBlobHash))
        {
            where.Add("i.blob_hash = $blobHash");
            command.Parameters.AddWithValue("$blobHash", _manualBlobHash);
        }
        var search = _search.Text.Trim();
        if (!string.IsNullOrWhiteSpace(search) && view.SearchColumns is { Length: > 0 })
        {
            where.Add("(" + string.Join(" OR ", view.SearchColumns.Select(column => $"instr(lower(COALESCE(CAST({column} AS TEXT),'')), lower($search)) > 0")) + ")");
            command.Parameters.AddWithValue("$search", search);
        }
        return where;
    }

    private static string WhereClause(IReadOnlyCollection<string> where) => where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where);
    private static string AsUtcText(DateTime value) => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToString("O");

    private void UpdateViewControls(DatabaseView view)
    {
        _imageSort.Visible = view.HasImages;
        _imageGrouping.Visible = view.HasImages && _galleryMode;
        _imageArticlePicker.Visible = view.HasImages;
        _imageManualEntry.Visible = view.HasImages;
        _galleryToggle.Visible = view.HasImages;
        _reloadGallery.Visible = view.HasImages && _galleryMode;
        _preview.Visible = view.HasImages;
        if (view.HasImages && _imageArticlePicker.Items.Count == 0) PopulateImageArticles();
        if (!view.HasImages && _galleryMode) SetGalleryMode(false);
    }

    private void SetTableStatus(DatabaseView view, int rowsOnPage)
    {
        var first = _totalRows == 0 ? 0 : _offset + 1;
        var last = _totalRows == 0 ? 0 : _offset + rowsOnPage;
        var filtered = string.IsNullOrWhiteSpace(_search.Text) && !_from.Checked && !_to.Checked &&
            string.IsNullOrWhiteSpace(CurrentImageHash) && _manualImageId is null && _manualBlobHash is null ? "" : " // FILTERED";
        _status.Text = $"READ-ONLY VIEW // {view.Label} // ROWS {first:N0}–{last:N0} OF {_totalRows:N0} // PAGE {CurrentPage:N0}/{TotalPages:N0}{filtered}";
        _pageEntry.Text = CurrentPage.ToString();
        _first.Enabled = _previous.Enabled = _offset > 0;
        _next.Enabled = _last.Enabled = _offset + PageSize < _totalRows;
        _goToPage.Enabled = _totalRows > 0;
    }

    private static void RemoveBinaryColumns(DataTable table)
    {
        foreach (var column in table.Columns.Cast<DataColumn>().Where(column => column.DataType == typeof(byte[])).ToArray())
            table.Columns.Remove(column);
    }

    private void ConfigureBoundColumns()
    {
        foreach (DataGridViewColumn column in _grid.Columns)
        {
            column.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
            column.Width = column.Name is "id" or "position" or "attempts" ? 82 : column.Name is "title" or "summary" ? 320 : 180;
            column.SortMode = DataGridViewColumnSortMode.Automatic;
            if (column.Name is "payload" or "summary" or "last_error") column.Visible = false;
        }
    }

    private void GoToPage()
    {
        if (!int.TryParse(_pageEntry.Text, out var page))
        {
            _status.Text = "ENTER A VALID PAGE NUMBER";
            return;
        }
        page = Math.Clamp(page, 1, TotalPages);
        _offset = (page - 1) * PageSize;
        LoadPage();
    }

    private void ClearFilters()
    {
        _searchTimer.Stop();
        _suppressFilterReload = true;
        try
        {
            _search.Clear();
            _from.Checked = false;
            _to.Checked = false;
            _manualArticleHash = null;
            _manualImageId = null;
            _manualBlobHash = null;
            _imageManualEntry.Clear();
            _loadingImagePicker = true;
            if (_imageArticlePicker.Items.Count > 0) _imageArticlePicker.SelectedIndex = 0;
        }
        finally
        {
            _loadingImagePicker = false;
            _suppressFilterReload = false;
        }
        _offset = 0;
        ReloadCurrent();
    }

    private void SortImages()
    {
        if (!CurrentView.HasImages || _imageSort.SelectedIndex < 0) return;
        if (_galleryMode)
        {
            RequestGalleryReload();
            return;
        }
        if (_table?.DefaultView is not { } view) return;
        var sort = _imageSort.SelectedIndex switch
        {
            0 => "published DESC, id DESC",
            1 => "published ASC, id ASC",
            2 => "position ASC, id ASC",
            3 => "original_bytes DESC, id DESC",
            4 => "original_bytes ASC, id ASC",
            5 => "compressed_bytes DESC, id DESC",
            6 => "mime_type ASC, id ASC",
            _ => "alt_text ASC, id ASC"
        };
        if (sort.Split(',').All(part => _table.Columns.Contains(part.Trim().Split(' ')[0]))) view.Sort = sort;
    }

    private string ImageOrder() => _imageSort.SelectedIndex switch
    {
        1 => " ORDER BY a.published ASC, i.id ASC",
        2 => " ORDER BY i.position ASC, i.id ASC",
        3 => " ORDER BY i.original_bytes DESC, i.id DESC",
        4 => " ORDER BY i.original_bytes ASC, i.id ASC",
        5 => " ORDER BY i.compressed_bytes DESC, i.id DESC",
        6 => " ORDER BY i.mime_type COLLATE NOCASE ASC, i.id ASC",
        7 => " ORDER BY i.alt_text COLLATE NOCASE ASC, i.id ASC",
        _ => " ORDER BY a.published DESC, i.id DESC"
    };

    private void PopulateImageArticles()
    {
        var selected = (_imageArticlePicker.SelectedItem as ArticleChoice)?.Hash ?? _manualArticleHash ?? "";
        _loadingImagePicker = true;
        try
        {
            using var connection = _storage.OpenReadOnly();
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT a.article_hash,a.title,MAX(a.published) AS published,COUNT(i.id) AS image_count
                FROM article_images i JOIN articles a ON a.article_hash=i.article_hash
                GROUP BY a.article_hash,a.title ORDER BY published DESC LIMIT {ArticlePickerLimit}
                """;
            using var reader = command.ExecuteReader();
            var choices = new List<ArticleChoice> { new("", "ALL ARCHIVED ARTICLES") };
            while (reader.Read())
            {
                var hash = reader.GetString(0);
                var title = reader.IsDBNull(1) ? "UNTITLED" : reader.GetString(1);
                choices.Add(new ArticleChoice(hash, $"{title} // {reader.GetInt32(3)} IMG // {hash[..Math.Min(12, hash.Length)]}"));
            }
            _imageArticlePicker.Items.Clear();
            _imageArticlePicker.Items.AddRange(choices.Cast<object>().ToArray());
            _imageArticlePicker.DisplayMember = nameof(ArticleChoice.Display);
            _imageArticlePicker.SelectedIndex = Math.Max(0, choices.FindIndex(choice => choice.Hash == selected));
            _imageArticlePicker.AccessibleDescription = $"The most recent {ArticlePickerLimit:N0} archived articles. Use Image ID or article hash for an uncapped archive lookup.";
        }
        catch (Exception ex)
        {
            _status.Text = "IMAGE ARTICLE LIST FAILED // " + ex.Message;
        }
        finally { _loadingImagePicker = false; }
    }

    private string CurrentImageHash => _manualArticleHash ?? (_imageArticlePicker.SelectedItem as ArticleChoice)?.Hash ?? "";

    private void RefreshImageScope()
    {
        _manualArticleHash = null;
        _manualImageId = null;
        _manualBlobHash = null;
        _imageManualEntry.Clear();
        _offset = 0;
        ReloadCurrent();
    }

    private void ApplyManualImageScope()
    {
        var value = _imageManualEntry.Text.Trim();
        if (string.IsNullOrEmpty(value))
        {
            _manualArticleHash = null;
            _manualImageId = null;
            _manualBlobHash = null;
            ReloadCurrent();
            return;
        }
        try
        {
            using var connection = _storage.OpenReadOnly();
            using var command = connection.CreateCommand();
            command.CommandText = long.TryParse(value, out _)
                ? "SELECT article_hash FROM article_images WHERE id=$value"
                : "SELECT article_hash FROM article_images WHERE article_hash=$value LIMIT 1";
            command.Parameters.AddWithValue("$value", long.TryParse(value, out var imageId) ? imageId : value);
            if (command.ExecuteScalar() is not string hash)
            {
                _status.Text = "NO ARCHIVED IMAGE FOUND FOR THAT ARTICLE HASH OR IMAGE ID";
                return;
            }
            _manualArticleHash = hash;
            _manualImageId = long.TryParse(value, out var selectedId) ? selectedId : null;
            _manualBlobHash = null;
            _offset = 0;
            ReloadCurrent();
        }
        catch (Exception ex) { _status.Text = "IMAGE LOOKUP FAILED // " + ex.Message; }
    }

    private void SetGalleryMode(bool enabled)
    {
        _galleryMode = enabled;
        _imageGrouping.Visible = enabled;
        _gallery.Visible = enabled;
        _grid.Visible = !enabled;
        _galleryToggle.Text = enabled ? "TABLE VIEW" : "GALLERY VIEW";
        _reloadGallery.Visible = enabled;
        if (enabled) RequestGalleryReload();
        else
        {
            _galleryCancellation?.Cancel();
            _thumbnailReloadPending = false;
            ClearPreview();
            LoadPage();
        }
    }

    private void RequestGalleryReload()
    {
        if (!_galleryMode || !CurrentView.HasImages) return;
        _galleryCancellation?.Cancel();
        if (_galleryLoading)
        {
            _galleryReloadQueued = true;
            return;
        }
        _ = LoadGalleryAsync();
    }

    private async Task LoadGalleryAsync()
    {
        if (!_galleryMode || !CurrentView.HasImages || IsDisposed) return;
        _galleryLoading = true;
        _galleryReloadQueued = false;
        _galleryCancellation?.Dispose();
        _galleryCancellation = new CancellationTokenSource();
        var cancellation = _galleryCancellation.Token;
        _reloadGallery.Enabled = false;
        _status.Text = "LOADING COMPLETE IMAGE DATASET METADATA…";
        try
        {
            var query = CaptureGalleryQuery();
            var images = await Task.Run(() => ReadGalleryMetadata(query, cancellation), cancellation);
            if (IsDisposed || cancellation.IsCancellationRequested) return;
            _gallery.SetItems(images);
            var scope = string.IsNullOrWhiteSpace(query.ArticleHash) ? "ALL ARCHIVES" : "ARTICLE SCOPE";
            _status.Text = $"GALLERY // {images.Count:N0} {(_imageGrouping.SelectedIndex == 0 ? "UNIQUE IMAGES" : "ARTICLE LINKS")} IN {scope} // VIRTUAL SCROLL ACTIVE // {ImageSortLabel()}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = "GALLERY LOAD FAILED // " + ex.Message; }
        finally
        {
            _galleryLoading = false;
            if (!IsDisposed) _reloadGallery.Enabled = true;
            if (_galleryReloadQueued && !IsDisposed)
            {
                _galleryReloadQueued = false;
                _ = LoadGalleryAsync();
            }
            else if (!IsDisposed)
            {
                QueueVisibleThumbnailLoad();
            }
        }
    }

    private GalleryQuery CaptureGalleryQuery() => new(
        CurrentImageHash,
        _manualImageId,
        _manualBlobHash,
        _from.Checked ? AsUtcText(_from.Value) : null,
        _to.Checked ? AsUtcText(_to.Value) : null,
        _search.Text.Trim(),
        CurrentView.SearchColumns ?? [],
        ImageOrder(),
        _imageGrouping.SelectedIndex == 0);

    private List<GalleryImageInfo> ReadGalleryMetadata(GalleryQuery query, CancellationToken cancellation)
    {
        using var connection = _storage.OpenReadOnly();
        using var command = connection.CreateCommand();
        var where = new List<string>();
        if (!string.IsNullOrEmpty(query.ArticleHash)) { where.Add("i.article_hash=$hash"); command.Parameters.AddWithValue("$hash", query.ArticleHash); }
        if (query.ImageId is long imageId) { where.Add("i.id=$imageId"); command.Parameters.AddWithValue("$imageId", imageId); }
        if (!string.IsNullOrWhiteSpace(query.BlobHash)) { where.Add("i.blob_hash=$blobHash"); command.Parameters.AddWithValue("$blobHash", query.BlobHash); }
        if (query.From is not null) { where.Add("a.published >= $from"); command.Parameters.AddWithValue("$from", query.From); }
        if (query.To is not null) { where.Add("a.published <= $to"); command.Parameters.AddWithValue("$to", query.To); }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            where.Add("(" + string.Join(" OR ", query.SearchColumns.Select(column => $"instr(lower(COALESCE(CAST({column} AS TEXT),'')), lower($search)) > 0")) + ")");
            command.Parameters.AddWithValue("$search", query.Search);
        }
        command.CommandText = $"SELECT i.id,i.article_hash,i.position,i.mime_type,i.alt_text,i.original_bytes,i.compressed_bytes,a.title,i.blob_hash FROM article_images i LEFT JOIN articles a ON a.article_hash=i.article_hash{WhereClause(where)}{query.Order}";
        using var reader = command.ExecuteReader();
        var images = new List<GalleryImageInfo>();
        var unique = query.UniqueImages ? new Dictionary<string, int>(StringComparer.Ordinal) : null;
        while (reader.Read())
        {
            cancellation.ThrowIfCancellationRequested();
            var hash = reader.IsDBNull(8) ? null : reader.GetString(8);
            if (unique is not null && hash is not null && unique.TryGetValue(hash, out var existing))
            {
                images[existing] = images[existing] with { LinkCount = images[existing].LinkCount + 1 };
                continue;
            }
            if (unique is not null && hash is not null) unique[hash] = images.Count;
            images.Add(new GalleryImageInfo(
                reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3), reader.GetString(4),
                reader.GetInt32(5), reader.GetInt32(6), reader.IsDBNull(7) ? "UNTITLED" : reader.GetString(7), hash));
        }
        return images;
    }

    private string ImageSortLabel() => "SORT: " + _imageSort.Text;

    private void QueueVisibleThumbnailLoad()
    {
        if (!_galleryMode || _galleryLoading || IsDisposed) return;
        // A debounced WinForms timer can be postponed indefinitely by wheel or
        // scrollbar events. Keep one batch in flight and coalesce later scrolls.
        if (_thumbnailLoading)
        {
            _thumbnailReloadPending = true;
            return;
        }
        _ = LoadVisibleThumbnailsAsync();
    }

    private async Task LoadVisibleThumbnailsAsync()
    {
        if (!_galleryMode || _thumbnailLoading || _galleryLoading || IsDisposed) return;
        var images = _gallery.VisibleItems(2).Where(image => _gallery.NeedsThumbnail(image.Id)).Take(ThumbnailBatchSize).ToArray();
        if (images.Length == 0) return;
        _thumbnailLoading = true;
        var cancellation = _galleryCancellation?.Token ?? CancellationToken.None;
        var scheduleNextBatch = false;
        try
        {
            var thumbnails = await Task.Run(() => ReadThumbnails(images, cancellation), cancellation);
            if (IsDisposed || cancellation.IsCancellationRequested)
            {
                foreach (var (_, image) in thumbnails) image?.Dispose();
                scheduleNextBatch = true;
                return;
            }
            foreach (var (id, image) in thumbnails) _gallery.SetThumbnail(id, image);
            scheduleNextBatch = true;
        }
        catch (OperationCanceledException) { scheduleNextBatch = true; }
        catch (Exception ex) { _status.Text = "THUMBNAIL LOAD FAILED // " + ex.Message; }
        finally
        {
            _thumbnailLoading = false;
            var reload = _thumbnailReloadPending || scheduleNextBatch &&
                _gallery.VisibleItems(2).Any(image => _gallery.NeedsThumbnail(image.Id));
            _thumbnailReloadPending = false;
            if (reload && !IsDisposed && _galleryMode && !_galleryLoading)
                QueueVisibleThumbnailLoad();
        }
    }

    private List<(long Id, Image? Thumbnail)> ReadThumbnails(IReadOnlyList<GalleryImageInfo> images, CancellationToken cancellation)
    {
        using var connection = _storage.OpenReadOnly();
        using var command = connection.CreateCommand();
        var parameters = images.Select((_, index) => "$id" + index).ToArray();
        command.CommandText = "SELECT i.id,CASE WHEN i.blob_hash IS NULL THEN i.image_gzip ELSE b.image_gzip END FROM article_images i LEFT JOIN image_blobs b ON b.sha256=i.blob_hash WHERE i.id IN (" + string.Join(",", parameters) + ")";
        for (var index = 0; index < images.Count; index++) command.Parameters.AddWithValue(parameters[index], images[index].Id);
        using var reader = command.ExecuteReader();
        var thumbnails = new List<(long Id, Image? Thumbnail)>();
        try
        {
            while (reader.Read())
            {
                cancellation.ThrowIfCancellationRequested();
                thumbnails.Add((reader.GetInt64(0), reader.IsDBNull(1) ? null : CreateThumbnail(reader.GetFieldValue<byte[]>(1), cancellation)));
            }
            var loaded = thumbnails.Select(item => item.Id).ToHashSet();
            foreach (var image in images)
                if (!loaded.Contains(image.Id)) thumbnails.Add((image.Id, null));
            return thumbnails;
        }
        catch
        {
            foreach (var (_, thumbnail) in thumbnails) thumbnail?.Dispose();
            throw;
        }
    }

    private void ShowGallerySelection(GalleryImageInfo image)
    {
        _details.Text = string.Join(Environment.NewLine,
        [
            $"id: {image.Id}", $"article_hash: {image.ArticleHash}", $"position: {image.Position}", $"mime_type: {image.MimeType}",
            $"original_bytes: {image.OriginalBytes:N0}", $"compressed_bytes: {image.CompressedBytes:N0}", $"linked_articles_in_scope: {image.LinkCount:N0}",
            $"content_sha256: {image.BlobHash ?? "legacy image"}", $"title: {image.Title}", $"alt_text: {image.AltText}",
            "", "Double-click, or press Enter, to inspect every article link for this image in the table."
        ]);
        _ = LoadImageAsync(image.Id);
    }

    private void OpenGalleryImageInTable(GalleryImageInfo image)
    {
        _imageManualEntry.Clear();
        var allLinks = _imageGrouping.SelectedIndex == 0 && image.BlobHash is not null;
        if (allLinks)
        {
            _searchTimer.Stop();
            _suppressFilterReload = true;
            try
            {
                _search.Clear();
                _from.Checked = false;
                _to.Checked = false;
            }
            finally { _suppressFilterReload = false; }
        }
        _manualArticleHash = allLinks ? "" : image.ArticleHash;
        _manualImageId = allLinks ? null : image.Id;
        _manualBlobHash = allLinks ? image.BlobHash : null;
        _offset = 0;
        SetGalleryMode(false);
    }

    private static Image? CreateThumbnail(byte[] compressed, CancellationToken cancellation)
    {
        if (compressed.Length > 8 * 1024 * 1024) return null;
        try
        {
            using var input = new MemoryStream(compressed);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = ReadGzip(gzip, 12L * 1024 * 1024, cancellation);
            using var original = Image.FromStream(output, false, true);
            return RenderScaledImage(original, 180, 112, 1_000_000);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private void ShowSelection()
    {
        if (_grid.CurrentRow?.DataBoundItem is not DataRowView row) return;
        _details.Text = string.Join(Environment.NewLine, row.Row.Table.Columns.Cast<DataColumn>()
            .Select(column => $"{column.ColumnName}: {Display(row.Row[column])}"));
        if (CurrentView.HasImages && row.Row.Table.Columns.Contains("id") && long.TryParse(row.Row["id"]?.ToString(), out var id))
            _ = LoadImageAsync(id);
        else
            ClearPreview();
    }

    private static string Display(object value) => value == DBNull.Value ? "—" : value.ToString() ?? "";

    private async Task LoadImageAsync(long id)
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = new CancellationTokenSource();
        var cancellation = _previewCancellation.Token;
        ClearPreview();
        try
        {
            var image = await Task.Run(() => ReadPreview(id, cancellation), cancellation);
            if (IsDisposed || cancellation.IsCancellationRequested) { image?.Dispose(); return; }
            _preview.Image = image;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _details.Text += Environment.NewLine + Environment.NewLine + "PREVIEW UNAVAILABLE: " + ex.Message;
        }
    }

    private Bitmap? ReadPreview(long id, CancellationToken cancellation)
    {
        using var connection = _storage.OpenReadOnly();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT CASE WHEN i.blob_hash IS NULL THEN i.image_gzip ELSE b.image_gzip END FROM article_images i LEFT JOIN image_blobs b ON b.sha256=i.blob_hash WHERE i.id=$id";
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteScalar() is not byte[] compressed || compressed.Length > 24 * 1024 * 1024) return null;
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = ReadGzip(gzip, 64L * 1024 * 1024, cancellation);
        using var original = Image.FromStream(output, useEmbeddedColorManagement: false, validateImageData: true);
        return RenderScaledImage(original, 2400, 1800, 12_000_000);
    }

    private static MemoryStream ReadGzip(GZipStream gzip, long byteLimit, CancellationToken cancellation)
    {
        var output = new MemoryStream();
        var buffer = new byte[81_920];
        var total = 0L;
        int read;
        while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            total += read;
            if (total > byteLimit)
            {
                output.Dispose();
                throw new InvalidDataException($"Image exceeds the {byteLimit / 1048576:N0} MiB safety limit.");
            }
            output.Write(buffer, 0, read);
        }
        output.Position = 0;
        return output;
    }

    private static Bitmap RenderScaledImage(Image original, int maxWidth, int maxHeight, int maxPixels)
    {
        var pixelScale = Math.Sqrt(maxPixels / (double)Math.Max(1L, (long)original.Width * original.Height));
        var scale = Math.Min(1d, Math.Min(Math.Min(maxWidth / (double)original.Width, maxHeight / (double)original.Height), pixelScale));
        var width = Math.Max(1, (int)Math.Round(original.Width * scale));
        var height = Math.Max(1, (int)Math.Round(original.Height * scale));
        var rendered = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(rendered);
        graphics.Clear(UiTheme.Void);
        graphics.DrawImage(original, new Rectangle(0, 0, width, height));
        return rendered;
    }

    private void CopyRecord()
    {
        if (string.IsNullOrWhiteSpace(_details.Text)) return;
        try { Clipboard.SetText(_details.Text); _status.Text = "SELECTED RECORD COPIED TO CLIPBOARD"; }
        catch (Exception ex) { _status.Text = "COPY FAILED // " + ex.Message; }
    }

    private void BrowserKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Control && eventArgs.KeyCode == Keys.F)
        {
            _search.Focus();
            eventArgs.SuppressKeyPress = true;
        }
        else if (eventArgs.KeyCode == Keys.F5)
        {
            ReloadCurrent();
            eventArgs.SuppressKeyPress = true;
        }
        else if (eventArgs.KeyCode == Keys.Escape && !string.IsNullOrWhiteSpace(_search.Text))
        {
            _search.Clear();
            eventArgs.SuppressKeyPress = true;
        }
    }

    private void ClearPreview()
    {
        _preview.Image?.Dispose();
        _preview.Image = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _searchTimer.Dispose();
            _galleryCancellation?.Cancel();
            _galleryCancellation?.Dispose();
            _previewCancellation?.Cancel();
            _previewCancellation?.Dispose();
            ClearPreview();
        }
        base.Dispose(disposing);
    }

    private sealed record GalleryQuery(string ArticleHash, long? ImageId, string? BlobHash, string? From, string? To, string Search, string[] SearchColumns, string Order, bool UniqueImages);
}
