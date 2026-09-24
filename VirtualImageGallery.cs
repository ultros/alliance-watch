namespace AllianceWatch;

internal sealed record GalleryImageInfo(
    long Id,
    string ArticleHash,
    int Position,
    string MimeType,
    string AltText,
    int OriginalBytes,
    int CompressedBytes,
    string Title,
    string? BlobHash = null,
    int LinkCount = 1);

// A painted, virtualized gallery keeps the browser responsive even when the archive
// contains tens of thousands of images. Metadata is retained, but only nearby
// thumbnails are decoded and cached.
internal sealed class VirtualImageGallery : ScrollableControl
{
    private const int HorizontalPadding = 10;
    private const int CardWidth = 228;
    private const int CardHeight = 196;
    private const int Gap = 8;
    private const int ThumbnailCacheLimit = 320;

    private IReadOnlyList<GalleryImageInfo> _items = [];
    private HashSet<long> _itemIds = [];
    private readonly Dictionary<long, Image> _thumbnails = [];
    private readonly HashSet<long> _noThumbnail = [];
    private readonly LinkedList<long> _thumbnailOrder = [];
    private readonly Dictionary<long, LinkedListNode<long>> _thumbnailNodes = [];
    private int _selectedIndex = -1;
    private Point _lastPaintScroll;

    public event Action<GalleryImageInfo>? ItemSelected;
    public event Action<GalleryImageInfo>? ItemActivated;
    public event Action? ViewportChanged;

    public int ItemCount => _items.Count;
    public int CachedThumbnailCount => _thumbnails.Count;
    public GalleryImageInfo? SelectedItem => _selectedIndex >= 0 && _selectedIndex < _items.Count ? _items[_selectedIndex] : null;

    public VirtualImageGallery()
    {
        AutoScroll = true;
        DoubleBuffered = true;
        BackColor = UiTheme.Surface;
        ForeColor = UiTheme.Text;
        Font = UiTheme.Small;
        TabStop = true;
        AccessibleName = "Archived image gallery";
        AccessibleDescription = "Virtualized gallery of archived local images. Arrow keys navigate cards; Enter opens the selected image.";
    }

    public void SetItems(IReadOnlyList<GalleryImageInfo> items)
    {
        _items = items;
        _itemIds = items.Select(item => item.Id).ToHashSet();
        _selectedIndex = -1;
        ClearThumbnails();
        AutoScrollPosition = Point.Empty;
        UpdateScrollArea();
        Invalidate();
        ViewportChanged?.Invoke();
    }

    public IReadOnlyList<GalleryImageInfo> VisibleItems(int overscanRows = 1)
    {
        var (first, last) = VisibleRange(overscanRows);
        return first > last ? [] : _items.Skip(first).Take(last - first + 1).ToArray();
    }

    public void SetThumbnail(long imageId, Image? thumbnail)
    {
        if (!_itemIds.Contains(imageId))
        {
            thumbnail?.Dispose();
            return;
        }
        if (thumbnail is null)
        {
            _noThumbnail.Add(imageId);
            Invalidate();
            return;
        }

        if (_thumbnails.Remove(imageId, out var previous)) previous.Dispose();
        if (_thumbnailNodes.Remove(imageId, out var existingNode)) _thumbnailOrder.Remove(existingNode);
        _noThumbnail.Remove(imageId);
        _thumbnails[imageId] = thumbnail;
        _thumbnailNodes[imageId] = _thumbnailOrder.AddLast(imageId);
        while (_thumbnailOrder.Count > ThumbnailCacheLimit)
        {
            var oldest = _thumbnailOrder.First!;
            _thumbnailOrder.RemoveFirst();
            _thumbnailNodes.Remove(oldest.Value);
            if (_thumbnails.Remove(oldest.Value, out var evicted)) evicted.Dispose();
        }
        Invalidate();
    }

    public bool NeedsThumbnail(long imageId) => !_thumbnails.ContainsKey(imageId) && !_noThumbnail.Contains(imageId);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_items.Count == 0)
        {
            TextRenderer.DrawText(e.Graphics, "NO ARCHIVED IMAGES MATCH THIS SCOPE", UiTheme.Label, ClientRectangle, UiTheme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            return;
        }

        var metrics = Metrics();
        var scroll = AutoScrollPosition;
        e.Graphics.TranslateTransform(scroll.X, scroll.Y);
        var (first, last) = VisibleRange();
        for (var index = first; index <= last; index++)
        {
            DrawCard(e.Graphics, CardBounds(index, metrics), _items[index], index == _selectedIndex);
        }
        e.Graphics.ResetTransform();
        // ScrollableControl does not consistently raise OnScroll for mouse-wheel,
        // touchpad, or programmatic viewport changes. Painting observes them all.
        if (scroll != _lastPaintScroll)
        {
            _lastPaintScroll = scroll;
            if (IsHandleCreated) BeginInvoke(new Action(() => { if (!IsDisposed) ViewportChanged?.Invoke(); }));
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateScrollArea();
        Invalidate();
        ViewportChanged?.Invoke();
    }

    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        Invalidate();
        ViewportChanged?.Invoke();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Invalidate();
        ViewportChanged?.Invoke();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        Focus();
        var index = IndexAt(e.Location);
        if (index < 0) return;
        SetSelectedIndex(index, activate: e.Clicks >= 2);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_items.Count == 0) return;
        var metrics = Metrics();
        var current = Math.Max(0, _selectedIndex);
        var target = e.KeyCode switch
        {
            Keys.Left => current - 1,
            Keys.Right => current + 1,
            Keys.Up => current - metrics.Columns,
            Keys.Down => current + metrics.Columns,
            Keys.PageUp => current - metrics.Columns * Math.Max(1, ClientSize.Height / metrics.StepHeight),
            Keys.PageDown => current + metrics.Columns * Math.Max(1, ClientSize.Height / metrics.StepHeight),
            Keys.Home => 0,
            Keys.End => _items.Count - 1,
            _ => current
        };
        if (e.KeyCode == Keys.Enter && SelectedItem is { } selected)
        {
            ItemActivated?.Invoke(selected);
            e.Handled = true;
            return;
        }
        if (target == current && e.KeyCode is not (Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End)) return;
        SetSelectedIndex(Math.Clamp(target, 0, _items.Count - 1), activate: false);
        e.Handled = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ClearThumbnails();
        base.Dispose(disposing);
    }

    private void DrawCard(Graphics graphics, Rectangle bounds, GalleryImageInfo image, bool selected)
    {
        using var background = new SolidBrush(selected ? Color.FromArgb(12, 61, 66) : UiTheme.Raised);
        using var border = new Pen(selected ? UiTheme.CyanHot : UiTheme.Grid, selected ? 2 : 1);
        graphics.FillRectangle(background, bounds);
        graphics.DrawRectangle(border, bounds);

        var imageBounds = new Rectangle(bounds.X + 7, bounds.Y + 7, bounds.Width - 14, 118);
        using var imageBackground = new SolidBrush(UiTheme.Void);
        graphics.FillRectangle(imageBackground, imageBounds);
        if (_thumbnails.TryGetValue(image.Id, out var thumbnail))
        {
            TouchThumbnail(image.Id);
            DrawImageFit(graphics, thumbnail, imageBounds);
        }
        else
        {
            var placeholder = _noThumbnail.Contains(image.Id) ? "PREVIEW UNAVAILABLE" : "LOADING PREVIEW…";
            TextRenderer.DrawText(graphics, placeholder, UiTheme.Micro, imageBounds, UiTheme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        var metadata = new Rectangle(bounds.X + 7, bounds.Y + 130, bounds.Width - 14, bounds.Height - 136);
        TextRenderer.DrawText(graphics, $"#{image.Id:N0}  {image.LinkCount:N0} LINK{(image.LinkCount == 1 ? "" : "S")}  {image.MimeType}", UiTheme.Micro,
            new Rectangle(metadata.X, metadata.Y, metadata.Width, 16), UiTheme.Cyan,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(graphics, image.Title, UiTheme.Micro,
            new Rectangle(metadata.X, metadata.Y + 17, metadata.Width, 16), UiTheme.Text,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(graphics, $"{image.OriginalBytes / 1024d:F0} KiB // {image.AltText}", UiTheme.Micro,
            new Rectangle(metadata.X, metadata.Y + 34, metadata.Width, 16), UiTheme.Muted,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    private static void DrawImageFit(Graphics graphics, Image image, Rectangle target)
    {
        var scale = Math.Min(target.Width / (double)image.Width, target.Height / (double)image.Height);
        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));
        var destination = new Rectangle(target.X + (target.Width - width) / 2, target.Y + (target.Height - height) / 2, width, height);
        graphics.DrawImage(image, destination);
    }

    private void SetSelectedIndex(int index, bool activate)
    {
        if (index < 0 || index >= _items.Count) return;
        _selectedIndex = index;
        var bounds = CardBounds(index, Metrics());
        var top = -AutoScrollPosition.Y;
        if (bounds.Top < top || bounds.Bottom > top + ClientSize.Height)
            AutoScrollPosition = new Point(0, Math.Max(0, bounds.Top - Math.Max(8, (ClientSize.Height - bounds.Height) / 2)));
        Invalidate();
        ItemSelected?.Invoke(_items[index]);
        if (activate) ItemActivated?.Invoke(_items[index]);
        ViewportChanged?.Invoke();
    }

    private int IndexAt(Point point)
    {
        var metrics = Metrics();
        var content = new Point(point.X - AutoScrollPosition.X, point.Y - AutoScrollPosition.Y);
        var column = (content.X - HorizontalPadding) / metrics.StepWidth;
        var row = (content.Y - HorizontalPadding) / metrics.StepHeight;
        if (column < 0 || row < 0 || column >= metrics.Columns) return -1;
        var bounds = new Rectangle(HorizontalPadding + column * metrics.StepWidth, HorizontalPadding + row * metrics.StepHeight, CardWidth, CardHeight);
        var index = row * metrics.Columns + column;
        return bounds.Contains(content) && index < _items.Count ? index : -1;
    }

    private Rectangle CardBounds(int index, GalleryMetrics metrics)
    {
        var column = index % metrics.Columns;
        var row = index / metrics.Columns;
        return new Rectangle(HorizontalPadding + column * metrics.StepWidth, HorizontalPadding + row * metrics.StepHeight, CardWidth, CardHeight);
    }

    private GalleryMetrics Metrics()
    {
        var available = Math.Max(CardWidth, ClientSize.Width - HorizontalPadding * 2 - SystemInformation.VerticalScrollBarWidth);
        var columns = Math.Max(1, (available + Gap) / (CardWidth + Gap));
        var rows = Math.Max(1, (_items.Count + columns - 1) / columns);
        return new GalleryMetrics(columns, rows, CardWidth + Gap, CardHeight + Gap);
    }

    private void UpdateScrollArea()
    {
        var metrics = Metrics();
        AutoScrollMinSize = new Size(0, HorizontalPadding * 2 + metrics.Rows * metrics.StepHeight - Gap);
    }

    private (int First, int Last) VisibleRange(int overscanRows = 1)
    {
        if (_items.Count == 0) return (0, -1);
        var metrics = Metrics();
        var scrollY = -AutoScrollPosition.Y;
        var firstRow = Math.Max(0, scrollY / metrics.StepHeight - overscanRows);
        var lastRow = Math.Min(metrics.Rows - 1, (scrollY + ClientSize.Height) / metrics.StepHeight + overscanRows);
        var first = firstRow * metrics.Columns;
        var last = Math.Min(_items.Count - 1, (lastRow + 1) * metrics.Columns - 1);
        return (first, last);
    }

    private void TouchThumbnail(long imageId)
    {
        if (!_thumbnailNodes.Remove(imageId, out var node)) return;
        _thumbnailOrder.Remove(node);
        _thumbnailNodes[imageId] = _thumbnailOrder.AddLast(imageId);
    }

    private void ClearThumbnails()
    {
        foreach (var image in _thumbnails.Values) image.Dispose();
        _thumbnails.Clear();
        _noThumbnail.Clear();
        _thumbnailOrder.Clear();
        _thumbnailNodes.Clear();
    }

    private readonly record struct GalleryMetrics(int Columns, int Rows, int StepWidth, int StepHeight);
}
