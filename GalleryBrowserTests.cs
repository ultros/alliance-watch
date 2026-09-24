using System.Reflection;
using System.IO.Compression;
using Microsoft.Data.Sqlite;

namespace AllianceWatch;

internal static class GalleryBrowserTests
{
    public static void VerifyGalleryReadsDoNotMakeMonitorReadOnly()
    {
        var path = Path.Combine(Path.GetTempPath(), "aw-gallery-write-test-" + Guid.NewGuid() + ".db");
        try
        {
            var storage = new Storage(path);
            storage.Initialize();
            // Simulate a gallery connection being the first SQLite handle after
            // idle pooled handles have been retired, while a monitor write starts.
            SqliteConnection.ClearAllPools();
            using var galleryConnection = storage.OpenReadOnly();
            using var read = galleryConnection.CreateCommand();
            read.CommandText = "SELECT COUNT(*) FROM articles";
            Assert(Convert.ToInt64(read.ExecuteScalar()) == 0, "Gallery fixture should start empty.");
            Assert(storage.InsertArticle("gallery-write", "fixture", "Gallery must not stop monitoring",
                "https://example.invalid/gallery-write", DateTimeOffset.UtcNow.ToString("O"), ""),
                "The monitor must still be able to write while the gallery has a read-only connection.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    public static void VerifyImageDeduplication()
    {
        var path = Path.Combine(Path.GetTempPath(), "aw-image-dedupe-test-" + Guid.NewGuid() + ".db");
        string? backupPath = null;
        try
        {
            var storage = new Storage(path);
            storage.Initialize();
            var image = new ArchivedImage(0, "https://example.invalid/source", "https://example.invalid/same.png", "image/png", "same image", 4, [1, 2, 3, 4]);
            for (var index = 0; index < 3; index++)
            {
                var hash = "dedupe-" + index;
                storage.InsertArticle(hash, "test", "Article " + index, "https://example.invalid/" + index, DateTimeOffset.UtcNow.ToString("O"), "");
                storage.SaveArticleArchive(new ArticleArchive(hash, "https://example.invalid/" + index, "text/html", 0, 0, [], [], [image]));
            }
            using (var connection = storage.OpenReadOnly())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT (SELECT COUNT(*) FROM article_images),(SELECT COUNT(*) FROM image_blobs),(SELECT COUNT(*) FROM article_images WHERE length(image_gzip)=0)";
                using var reader = command.ExecuteReader();
                Assert(reader.Read() && reader.GetInt64(0) == 3 && reader.GetInt64(1) == 1 && reader.GetInt64(2) == 3,
                    "Three article links should share one payload, with no inline image data.");
            }
            var stats = storage.ArchiveStats();
            Assert(stats.Images == 1 && stats.ImageLinks == 3, "Status should distinguish unique images from article links.");
            backupPath = storage.BackupBeforeImageConsolidation();
            Assert(File.Exists(backupPath) && new FileInfo(backupPath).Length > 0, "A verified backup must precede live consolidation.");

            // Re-create one pre-migration inline row and verify the resumable conversion.
            using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var legacy = connection.CreateCommand();
                legacy.CommandText = "UPDATE article_images SET blob_hash=NULL,image_gzip=x'01020304' WHERE article_hash='dedupe-0'";
                legacy.ExecuteNonQuery();
            }
            var result = storage.ConsolidateImages();
            storage.VerifyImageStorage();
            Assert(result.Links == 3 && result.UniqueBlobs == 1 && result.ReclaimedBytes == 4,
                "Legacy inline data should relink to the existing shared payload.");
            storage.CompactDatabase();
            storage.VerifyImageStorage();
            var replacement = image with { CompressedData = [5, 6, 7, 8] };
            for (var index = 0; index < 3; index++)
                storage.SaveArticleArchive(new ArticleArchive("dedupe-" + index, "https://example.invalid/" + index,
                    "text/html", 0, 0, [], [], [replacement, replacement]));
            storage.VerifyImageStorage();
            var replaced = storage.ArchiveStats();
            Assert(replaced.Images == 1 && replaced.ImageLinks == 3,
                "Re-archiving should remove unreferenced old payloads and ignore duplicate URLs within an article.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
            if (backupPath is not null) foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(backupPath + suffix)) File.Delete(backupPath + suffix);
        }
    }

    public static void VerifyVirtualGalleryWindowing()
    {
        var items = Enumerable.Range(1, 5_000)
            .Select(index => new GalleryImageInfo(index, "hash-" + index, index % 4, "image/png", "fixture image", 2048, 1024, "Gallery fixture " + index))
            .ToArray();
        using var gallery = new VirtualImageGallery { Size = new Size(980, 620) };
        _ = gallery.Handle;
        gallery.SetItems(items);
        Assert(gallery.ItemCount == items.Length, "The virtual gallery must retain the complete metadata result set.");
        Assert(gallery.VisibleItems().Count is > 0 and < 100, "Only a small visible metadata window should be requested at once.");
        for (var index = 0; index < 400; index++) gallery.SetThumbnail(items[index].Id, new Bitmap(4, 4));
        Assert(gallery.CachedThumbnailCount <= 320, "The thumbnail cache must remain bounded for large archives.");
    }

    public static void VerifyBrowserPagingAndSearch()
    {
        var path = Path.Combine(Path.GetTempPath(), "aw-browser-test-" + Guid.NewGuid() + ".db");
        try
        {
            var storage = new Storage(path);
            storage.Initialize();
            storage.MigrateAssessment();
            var now = DateTimeOffset.UtcNow.ToString("O");
            for (var index = 0; index < 1_005; index++)
            {
                var hash = "browser-fixture-" + index;
                storage.InsertArticle(hash, "gallery test", "Archive browser record " + index, "https://example.invalid/" + index, now,
                    index == 1004 ? "Unique % marker" : "Local browser fixture " + index);
            }
            using var fixtureBitmap = new Bitmap(4, 4);
            using var fixturePng = new MemoryStream();
            fixtureBitmap.Save(fixturePng, System.Drawing.Imaging.ImageFormat.Png);
            using var fixtureGzip = new MemoryStream();
            using (var compressor = new GZipStream(fixtureGzip, CompressionLevel.Fastest, leaveOpen: true))
                compressor.Write(fixturePng.ToArray());
            var imageBytes = fixtureGzip.ToArray();
            for (var index = 0; index < 512; index++)
            {
                var hash = "browser-fixture-" + index;
                var images = new List<ArchivedImage>
                {
                    new(0, "https://example.invalid/source/" + index, "https://example.invalid/image/" + index, "image/png", "Gallery fixture " + index, (int)fixturePng.Length, imageBytes)
                };
                if (index == 511) images.Add(new ArchivedImage(1, "https://example.invalid/source/extra", "https://example.invalid/image/extra", "image/png", "Second image in article", (int)fixturePng.Length, imageBytes));
                storage.SaveArticleArchive(new ArticleArchive(
                    hash,
                    "https://example.invalid/archive/" + index,
                    "text/html",
                    0,
                    0,
                    [],
                    [],
                    images));
            }

            using var form = new DatabaseBrowserForm(storage);
            _ = form.Handle;
            Invoke(form, "LoadPage");
            var grid = Field<DataGridView>(form, "_grid");
            Assert(grid.Rows.Count == 500, "The first browser page must be bounded to 500 rows.");

            var search = Field<TextBox>(form, "_search");
            search.Text = "record 1004";
            Invoke(form, "LoadPage");
            Assert(grid.Rows.Count == 1, "Dataset search must reach records beyond the first page.");
            Assert(grid.Rows[0].Cells["article_hash"].Value?.ToString() == "browser-fixture-1004", "Global search returned the wrong record.");

            search.Text = "%";
            Invoke(form, "LoadPage");
            Assert(grid.Rows.Count == 1 && grid.Rows[0].Cells["article_hash"].Value?.ToString() == "browser-fixture-1004",
                "Search must treat percent signs as literal text across the dataset.");

            search.Clear();
            Invoke(form, "LoadPage");
            var page = Field<TextBox>(form, "_pageEntry");
            page.Text = "3";
            Invoke(form, "GoToPage");
            Assert(grid.Rows.Count == 5, "Direct page navigation must reach the final partial page.");
            Assert(grid.Rows[0].Cells["article_hash"].Value?.ToString() == "browser-fixture-4", "The final page starts at the wrong row.");

            var viewPicker = Field<ComboBox>(form, "_viewPicker");
            viewPicker.SelectedIndex = 3; // ARCHIVED IMAGES
            Invoke(form, "SetGalleryMode", true);
            var gallery = Field<VirtualImageGallery>(form, "_gallery");
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (gallery.ItemCount != 1 && timeout.Elapsed < TimeSpan.FromSeconds(5))
            {
                Application.DoEvents();
                Thread.Sleep(5);
            }
            Assert(gallery.ItemCount == 1, "Unique gallery should collapse identical payloads across articles.");
            Invoke(form, "OpenGalleryImageInTable", gallery.VisibleItems().First());
            Assert(Field<Label>(form, "_status").Text.Contains("OF 513"),
                "Opening one unique image must reveal all 513 article-image links in the paged table.");
            Invoke(form, "ClearFilters");
            Invoke(form, "SetGalleryMode", true);
            var grouping = Field<ComboBox>(form, "_imageGrouping");
            grouping.SelectedIndex = 1; // ARTICLE LINKS
            timeout.Restart();
            while (gallery.ItemCount != 513 && timeout.Elapsed < TimeSpan.FromSeconds(5))
            {
                Application.DoEvents();
                Thread.Sleep(5);
            }
            Assert(gallery.ItemCount == 513, "Gallery mode must include the complete matching image dataset, not one load-more batch.");
            while (((bool)Value(form, "_galleryLoading")! || (bool)Value(form, "_galleryReloadQueued")!) &&
                   timeout.Elapsed < TimeSpan.FromSeconds(5))
            {
                Application.DoEvents();
                Thread.Sleep(5);
            }
            // The browser is not shown during self-tests, so drive the loader
            // directly; WinForms timers do not reliably tick for hidden forms.
            Invoke(form, "LoadVisibleThumbnailsAsync");
            timeout.Restart();
            while (!gallery.VisibleItems().Any(item => !gallery.NeedsThumbnail(item.Id)) &&
                   timeout.Elapsed < TimeSpan.FromSeconds(10))
            {
                Application.DoEvents();
                Thread.Sleep(20);
            }
            Assert(gallery.VisibleItems().Any(item => !gallery.NeedsThumbnail(item.Id)),
                "Visible thumbnails must load after gallery metadata arrives. " + Field<Label>(form, "_status").Text +
                $" // galleryLoading={Value(form, "_galleryLoading")} thumbnailLoading={Value(form, "_thumbnailLoading")} queued={Value(form, "_galleryReloadQueued")} visible={gallery.Visible} items={gallery.VisibleItems().Count}");
            Assert(gallery.CachedThumbnailCount > 0, "A valid archived PNG must render as a thumbnail.");

            var multiImageArticle = gallery.VisibleItems().FirstOrDefault(image => image.ArticleHash == "browser-fixture-511");
            Assert(multiImageArticle is not null, "Expected the multi-image article near the top of the gallery.");
            var firstImageId = gallery.VisibleItems().First().Id;
            var beforeScrollCached = gallery.CachedThumbnailCount;
            var wheel = typeof(VirtualImageGallery).GetMethod("OnMouseWheel", BindingFlags.Instance | BindingFlags.NonPublic)!;
            for (var index = 0; index < 25; index++)
                wheel.Invoke(gallery, [new MouseEventArgs(MouseButtons.None, 0, 0, 0, -120)]);
            var scrolledImage = gallery.VisibleItems().First();
            Assert(scrolledImage.Id != firstImageId,
                $"Gallery scrolling must expose a different image window. client={gallery.ClientSize} min={gallery.AutoScrollMinSize} position={gallery.AutoScrollPosition}");
            timeout.Restart();
            while (gallery.NeedsThumbnail(scrolledImage.Id) && timeout.Elapsed < TimeSpan.FromSeconds(5))
            {
                Application.DoEvents();
                Thread.Sleep(5);
            }
            Assert(!gallery.NeedsThumbnail(scrolledImage.Id), "Scrolling must automatically load the newly visible thumbnails.");
            Assert(gallery.CachedThumbnailCount > beforeScrollCached,
                "Scrolling must decode and cache newly visible images, not just mark them unavailable.");
            Invoke(form, "OpenGalleryImageInTable", multiImageArticle);
            Assert(grid.Rows.Count == 1 && Convert.ToInt64(grid.Rows[0].Cells["id"].Value) == multiImageArticle!.Id,
                "Opening a gallery card must select its exact image, rather than every image in its article.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    private static T Field<T>(object target, string name) where T : class =>
        (T)(typeof(DatabaseBrowserForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target)
            ?? throw new InvalidOperationException($"Missing browser field: {name}"));

    private static object? Value(object target, string name) =>
        typeof(DatabaseBrowserForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target);

    private static void Invoke(DatabaseBrowserForm form, string method, params object?[] arguments) =>
        typeof(DatabaseBrowserForm).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(form, arguments);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
