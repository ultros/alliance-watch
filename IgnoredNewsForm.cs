using System.Diagnostics;

namespace AllianceWatch;

// Exclusions affect future assessments, not the retained article or evidence log.
internal sealed class IgnoredNewsForm : Form
{
    private const int PageSize = 500;
    private readonly Storage _storage;
    private readonly AppConfig _config;
    private readonly TextBox _search = new() { Width = 300, PlaceholderText = "Search ignored headlines, sources, reasons, IDs…" };
    private readonly DataGridView _grid = new();
    private readonly Label _status = new() { Dock = DockStyle.Top, Height = 36, ForeColor = UiTheme.Cyan, Padding = new Padding(8, 7, 4, 2) };
    private readonly Button _previous = UiTheme.Button("◀ PREVIOUS");
    private readonly Button _next = UiTheme.Button("NEXT ▶");
    private readonly System.Windows.Forms.Timer _searchTimer = new() { Interval = 250 };
    private int _offset;
    private int _total;

    public event Action<Assessment>? AssessmentUpdated;

    public IgnoredNewsForm(Storage storage, AppConfig config)
    {
        _storage = storage;
        _config = config;
        Text = "AllianceWatch // Ignored News";
        Size = new Size(1220, 710);
        MinimumSize = new Size(800, 480);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = UiTheme.Void;
        ForeColor = UiTheme.Text;
        Font = UiTheme.Small;
        KeyPreview = true;

        var toolbar = new WrappingToolbar { MinimumToolbarHeight = 42, Padding = new Padding(7, 5, 7, 3) };
        var restore = UiTheme.Button("RESTORE SELECTED TO SCORE");
        var open = UiTheme.Button("OPEN SOURCE");
        var refresh = UiTheme.Button("REFRESH");
        toolbar.Controls.AddRange([_search, restore, open, _previous, _next, refresh]);
        var explanation = new Label
        {
            Dock = DockStyle.Top, Height = 44, Padding = new Padding(8, 5, 8, 3),
            ForeColor = UiTheme.Muted, Font = UiTheme.Small,
            Text = "These articles are retained for audit but excluded from new score calculations and the main news list. Restore an item to include it again. Other reports about the same event may still affect the score."
        };
        ConfigureGrid();
        Controls.Add(_grid);
        Controls.Add(_status);
        Controls.Add(explanation);
        Controls.Add(toolbar);

        _search.TextChanged += (_, _) => { _searchTimer.Stop(); _searchTimer.Start(); };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); _offset = 0; Reload(); };
        _previous.Click += (_, _) => { _offset = Math.Max(0, _offset - PageSize); Reload(); };
        _next.Click += (_, _) => { _offset += PageSize; Reload(); };
        refresh.Click += (_, _) => Reload();
        restore.Click += async (_, _) => await RestoreSelectedAsync();
        open.Click += (_, _) => OpenSelectedSource();
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) OpenSelectedSource(); };
        _grid.CellMouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0)
            {
                _grid.ClearSelection();
                _grid.Rows[e.RowIndex].Selected = true;
                _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells[0];
            }
        };
        var menu = new ContextMenuStrip();
        var restoreMenu = new ToolStripMenuItem("RESTORE THIS ARTICLE TO SCORE");
        restoreMenu.Click += async (_, _) => await RestoreSelectedAsync();
        var openMenu = new ToolStripMenuItem("OPEN ORIGINAL SOURCE");
        openMenu.Click += (_, _) => OpenSelectedSource();
        menu.Items.AddRange([restoreMenu, openMenu]);
        menu.Opening += (_, _) => restoreMenu.Enabled = Selected is not null;
        _grid.ContextMenuStrip = menu;
        KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.F) { _search.Focus(); e.SuppressKeyPress = true; }
            if (e.KeyCode == Keys.F5) { Reload(); e.SuppressKeyPress = true; }
        };
        UiToolTips.Enable(this);
        Shown += (_, _) => Reload();
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
        _grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = UiTheme.Surface, ForeColor = UiTheme.Text, SelectionBackColor = UiTheme.Raised, SelectionForeColor = UiTheme.CyanHot, Font = UiTheme.Small };
        _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = UiTheme.Void, ForeColor = UiTheme.Cyan, Font = UiTheme.Label };
        _grid.DataBindingComplete += (_, _) =>
        {
            foreach (DataGridViewColumn column in _grid.Columns)
                column.Width = column.Name switch
                {
                    nameof(IgnoredNewsItem.Title) => 350,
                    nameof(IgnoredNewsItem.Reason) => 280,
                    nameof(IgnoredNewsItem.Url) => 270,
                    nameof(IgnoredNewsItem.RecordId) => 220,
                    _ => 150
                };
        };
    }

    private IgnoredNewsItem? Selected => _grid.CurrentRow?.DataBoundItem as IgnoredNewsItem;

    private void Reload()
    {
        try
        {
            var search = _search.Text.Trim();
            _total = _storage.IgnoredNewsCount(search);
            if (_offset >= _total) _offset = _total == 0 ? 0 : ((_total - 1) / PageSize) * PageSize;
            var rows = _storage.IgnoredNews(PageSize, _offset, search);
            _grid.DataSource = rows;
            _previous.Enabled = _offset > 0;
            _next.Enabled = _offset + rows.Length < _total;
            _status.Text = $"IGNORED NEWS // {(_total == 0 ? 0 : _offset + 1):N0}–{_offset + rows.Length:N0} OF {_total:N0} // ORIGINAL RECORDS RETAINED";
        }
        catch (Exception ex) { _status.Text = "IGNORED NEWS LOAD FAILED // " + ex.Message; }
    }

    private async Task RestoreSelectedAsync()
    {
        if (Selected is not { } item) return;
        if (MessageBox.Show(this,
                $"Restore this article to future score calculations?\n\n{item.Title}\n\nThe original record was never deleted.",
                "Restore ignored news", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var saved = false;
        try
        {
            if (!_storage.SetNewsIgnored(item.RecordId, false, "Restored by operator")) { Reload(); return; }
            saved = true;
            _status.Text = "RESTORED // RECALCULATING SCORE…";
            var assessment = await Task.Run(() => _storage.RefreshAssessment(new AssessmentEngine(_config.Assessment), _config, "MANUAL"));
            if (IsDisposed) return;
            AssessmentUpdated?.Invoke(assessment);
            Reload();
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                Reload();
                MessageBox.Show(this, (saved ? "The restore was saved, but the score could not be recalculated yet. Run a scan to retry." :
                        "The article could not be restored.") + "\n\n" + ex.Message,
                    saved ? "Score refresh failed" : "Restore failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    private void OpenSelectedSource()
    {
        if (Selected is not { } item || !Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        try { Process.Start(new ProcessStartInfo(item.Url) { UseShellExecute = true }); }
        catch (Exception ex) { _status.Text = "SOURCE OPEN FAILED // " + ex.Message; }
    }

    internal static string? PromptIgnore(IWin32Window owner, string headline)
    {
        using var dialog = new Form
        {
            Text = "Ignore news item for score", Size = new Size(610, 310), MinimumSize = new Size(500, 280),
            StartPosition = FormStartPosition.CenterParent, BackColor = UiTheme.Void, ForeColor = UiTheme.Text,
            Font = UiTheme.Small, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false
        };
        var explanation = new Label
        {
            Dock = DockStyle.Top, Height = 95, Padding = new Padding(12, 12, 12, 5), ForeColor = UiTheme.Text,
            Text = $"Exclude this article from new score calculations?\n{headline}\n\nThe original article and evidence remain in the archive. Other articles about the same event may still contribute."
        };
        var reason = new TextBox { Dock = DockStyle.Fill, Multiline = true, MaxLength = 1000, PlaceholderText = "Optional reason for the audit log", Margin = new Padding(12) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8, 5, 8, 5) };
        var ignore = UiTheme.Button("IGNORE FOR SCORE");
        var cancel = UiTheme.Button("CANCEL");
        ignore.DialogResult = DialogResult.OK;
        cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.AddRange([ignore, cancel]);
        dialog.Controls.Add(reason);
        dialog.Controls.Add(explanation);
        dialog.Controls.Add(buttons);
        dialog.AcceptButton = ignore;
        dialog.CancelButton = cancel;
        UiToolTips.Enable(dialog);
        return dialog.ShowDialog(owner) == DialogResult.OK
            ? string.IsNullOrWhiteSpace(reason.Text) ? "Operator excluded this article from scoring." : reason.Text.Trim()
            : null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _searchTimer.Dispose();
        base.Dispose(disposing);
    }
}
