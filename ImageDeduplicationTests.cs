using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace AllianceWatch;

internal static class ImageDeduplicationTests
{
    public static void VerifyContentHashAndArticleLinks() => Run(async (storage, path) =>
    {
        var bytes = Enumerable.Range(0, 8192).Select(i => (byte)(i % 251)).ToArray();
        var first = Gzip(bytes, CompressionLevel.Fastest);
        var second = Gzip(bytes, CompressionLevel.SmallestSize);
        second[4] = 1; // A different gzip timestamp must not change image identity.
        Require(!first.SequenceEqual(second), "The fixture must have different compressed encodings.");
        var expectedHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Require(Storage.ImageContentHash(first) == expectedHash && Storage.ImageContentHash(second) == expectedHash,
            "Image identity must hash original bytes rather than gzip bytes.");
        SeedArticle(storage, "first");
        SeedArticle(storage, "second");
        Save(storage, "first", [Image(first, 0, "one", "First article image"), Image(second, 1, "two", "Second occurrence")]);
        Save(storage, "second", [Image(second, 0, "other-url", "Another article")]);
        Require(Scalar(storage, "SELECT COUNT(*) FROM image_blobs WHERE hash_version=1") == 1,
            "Different URLs and gzip encodings of identical bytes must share one stored payload.");
        Require(Scalar(storage, "SELECT COUNT(*) FROM article_images WHERE length(image_gzip)=0") == 3,
            "All article occurrences must retain their links without inline payload copies.");
        Require(Scalar(storage, "SELECT COUNT(*) FROM article_images WHERE article_hash='first' AND position=1 AND alt_text='Second occurrence'") == 1,
            "Deduplication must preserve article-specific image order and metadata.");
        Require(Scalar(storage, "SELECT COUNT(*) FROM article_images i JOIN image_blobs b ON b.sha256=i.blob_hash WHERE i.compressed_bytes<>b.compressed_bytes") == 0,
            "Every link must report the size of its actual shared payload.");
        using (var db = storage.OpenReadOnly())
        using (var command = db.CreateCommand())
        {
            command.CommandText = "SELECT b.image_gzip FROM article_images i JOIN image_blobs b ON b.sha256=i.blob_hash";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                using var input = new MemoryStream(reader.GetFieldValue<byte[]>(0));
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                using var decoded = new MemoryStream();
                gzip.CopyTo(decoded);
                Require(decoded.ToArray().SequenceEqual(bytes), "Every article must resolve to the exact original image bytes.");
            }
        }
        var changed = bytes.ToArray();
        changed[0] ^= 0xff;
        Save(storage, "second", [Image(Gzip(changed, CompressionLevel.Fastest), 0, "changed", "Different image")]);
        Require(storage.ArchiveStats().Images == 2, "Different image bytes must never be merged.");
        Save(storage, "first", []);
        Require(storage.ArchiveStats().Images == 1 && storage.ArchiveStats().ImageLinks == 1,
            "Re-archiving must remove a payload only when its last article link is removed.");
        storage.VerifyImageStorage();
        await Task.CompletedTask;
    });

    public static void VerifyAlwaysActiveMigration() => Run(async (storage, path) =>
    {
        var bytes = Enumerable.Range(0, 4096).Select(i => (byte)(i % 193)).ToArray();
        var first = Gzip(bytes, CompressionLevel.Fastest);
        var second = Gzip(bytes, CompressionLevel.SmallestSize);
        second[4] = 2;
        for (var i = 0; i < 60; i++) SeedLegacy(storage, "legacy-" + i, i % 2 == 0 ? first : second, i >= 35);
        var before = Scalar(storage, PendingSql);
        var batch = storage.ConsolidateImageBatch(2);
        Require(batch.Processed == 2 && Scalar(storage, PendingSql) < before,
            "Legacy compressed hashes must convert in bounded batches.");
        var pending = Scalar(storage, PendingSql);
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            try
            {
                storage.ConsolidateImageBatch(cancellationToken: cancellation.Token);
                throw new InvalidOperationException("A cancelled migration unexpectedly ran.");
            }
            catch (OperationCanceledException) { }
        }
        Require(Scalar(storage, PendingSql) == pending && Scalar(storage, "SELECT COUNT(*) FROM article_images") == 60,
            "Cancellation must leave all pending payloads and article links intact.");

        // No scan and no enabled archival downloads: startup alone must migrate.
        using var monitor = new FeedMonitor(new AppConfig { ArchiveEnabled = false }, storage, path + ".log");
        monitor.StartBackgroundWork();
        await Eventually(() => Scalar(storage, PendingSql) == 0, "Startup must automatically finish legacy deduplication with archives disabled.");
        Require(storage.ArchiveStats().Images == 1 && storage.ArchiveStats().ImageLinks == 60,
            "Inline and old shared payloads must merge without losing article associations.");
        Require(monitor.ArchiveRevision > 0, "Automatic conversion must refresh archive statistics.");

        // Work introduced after the first drain must be discovered without a scan.
        SeedLegacy(storage, "later-import", second, false);
        await Eventually(() => Scalar(storage, PendingSql) == 0, "The worker must keep deduplicating later legacy imports between scans.");
        Require(storage.ArchiveStats().Images == 1 && storage.ArchiveStats().ImageLinks == 61,
            "Later imports must join the existing content hash.");

        await monitor.StopAsync();
        SeedLegacy(storage, "after-shutdown", first, false);
        using var resumed = new FeedMonitor(new AppConfig { ArchiveEnabled = false }, storage, path + ".log");
        resumed.StartBackgroundWork();
        await Eventually(() => Scalar(storage, PendingSql) == 0, "A later application lifetime must resume pending image conversion.");
        Require(storage.ArchiveStats().Images == 1 && storage.ArchiveStats().ImageLinks == 62,
            "Restart must preserve all old and new article links.");
        storage.VerifyImageStorage();
    });

    public static void VerifyConcurrentMigrationAndArchival() => Run(async (storage, path) =>
    {
        var data = Gzip(Enumerable.Repeat((byte)79, 8192).ToArray(), CompressionLevel.Fastest);
        for (var i = 0; i < 80; i++) SeedLegacy(storage, "concurrent-legacy-" + i, data, false);
        using var readerConnection = storage.OpenReadOnly();
        using var monitor = new FeedMonitor(new AppConfig { ArchiveEnabled = false }, storage, path + ".log");
        monitor.StartBackgroundWork();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 15; i++)
            {
                var article = $"writer-{worker}-{i}";
                SeedArticle(storage, article);
                Save(storage, article, [Image(data, 0, article, "Concurrent archive")]);
                if (i % 3 == 0) Save(storage, article, [Image(data, 0, article, "Refetched archive")]);
            }
        })));
        await Eventually(() => Scalar(storage, PendingSql) == 0, "Migration must complete while parallel article archives are saved.");
        Require(storage.ArchiveStats().Images == 1 && storage.ArchiveStats().ImageLinks == 140,
            "Concurrent save and migration must retain every link with exactly one payload.");
        storage.VerifyImageStorage();
        using var integrity = readerConnection.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check";
        Require(integrity.ExecuteScalar()?.ToString() == "ok", "Concurrent image work must preserve database integrity.");
        integrity.CommandText = "PRAGMA foreign_key_check";
        using var violations = integrity.ExecuteReader();
        Require(!violations.Read(), "Concurrent image work must preserve every foreign-key reference.");
    });

    private const string PendingSql = "SELECT (SELECT COUNT(*) FROM image_blobs WHERE hash_version=0)+(SELECT COUNT(*) FROM article_images WHERE blob_hash IS NULL AND length(image_gzip)>0)";

    private static void Run(Func<Storage, string, Task> test) => Task.Run(async () =>
    {
        var path = Path.Combine(Path.GetTempPath(), "aw-auto-image-dedupe-" + Guid.NewGuid() + ".db");
        try
        {
            var storage = new Storage(path);
            storage.Initialize();
            storage.MigrateAssessment();
            await test(storage, path);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm", ".log" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }).GetAwaiter().GetResult();

    private static void SeedArticle(Storage storage, string article) => storage.InsertArticle(article, "fixture", "Article " + article,
        "https://example.invalid/" + article, DateTimeOffset.UtcNow.ToString("O"), "");

    private static ArchivedImage Image(byte[] data, int position, string name, string alt) =>
        new(position, "https://example.invalid/source/" + name, "https://example.invalid/image/" + name, "image/png", alt, 8192, data);

    private static void Save(Storage storage, string article, IReadOnlyList<ArchivedImage> images) => storage.SaveArticleArchive(
        new ArticleArchive(article, "https://example.invalid/" + article, "text/html", 0, 0, [], [], images));

    private static void SeedLegacy(Storage storage, string article, byte[] data, bool shared)
    {
        SeedArticle(storage, article);
        using var db = new SqliteConnection("Data Source=" + storage.DatabasePath);
        db.Open();
        using var transaction = db.BeginTransaction();
        var oldHash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$article", article);
        command.Parameters.AddWithValue("$hash", shared ? (object)oldHash : DBNull.Value);
        command.Parameters.AddWithValue("$size", data.Length);
        command.Parameters.Add("$data", SqliteType.Blob).Value = data;
        if (shared)
        {
            command.CommandText = "INSERT OR IGNORE INTO image_blobs(sha256,image_gzip,compressed_bytes) VALUES($hash,$data,$size)";
            command.ExecuteNonQuery();
        }
        command.CommandText = """
            INSERT INTO article_images(article_hash,position,source_url,resolved_url,mime_type,alt_text,original_bytes,compressed_bytes,image_gzip,blob_hash)
            VALUES($article,0,'legacy-source','legacy-image','image/png','Retained legacy image',4096,$size,
                CASE WHEN $hash IS NULL THEN $data ELSE x'' END,$hash)
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static byte[] Gzip(byte[] data, CompressionLevel level)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, level, leaveOpen: true)) gzip.Write(data);
        return output.ToArray();
    }

    private static long Scalar(Storage storage, string sql)
    {
        using var db = storage.OpenReadOnly();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static async Task Eventually(Func<bool> condition, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new InvalidOperationException(message);
            await Task.Delay(20);
        }
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
