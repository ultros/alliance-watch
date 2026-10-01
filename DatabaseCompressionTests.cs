// SPDX-License-Identifier: LicenseRef-AllianceWatch-Free-Use-No-Resale
// Copyright (c) 2026 Jesse Lee Shelley. All Rights Reserved.
// Free to run; selling or paid access requires Owner's paid written permission.
// See LICENSE and NOTICE for terms and required attribution.
// Creator: https://linkedin.com/in/jesse-shelley
// Repository: https://github.com/ultros/alliance-watch

using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace AllianceWatch;

internal static class DatabaseCompressionTests
{
    internal static void VerifyFullCompression() => InDatabase((storage, folder) =>
    {
        var raw = new byte[512 * 1024];
        new Random(47).NextBytes(raw);
        var first = Gzip(raw);
        var second = first.ToArray();
        second[4] = 19; // Different gzip header, identical original image bytes.
        Legacy(storage, "first", first, true);
        Legacy(storage, "second", second, true);
        Legacy(storage, "inline", first, false);
        using (var connection = storage.OpenReadOnly()) Require(Scalar(connection, "SELECT COUNT(*) FROM image_blobs WHERE hash_version=1") == 2, "Fixture must include stale hashes marked current.");
        using (var connection = new SqliteConnection("Data Source=" + storage.DatabasePath))
        {
            connection.Open();
            using var orphan = connection.CreateCommand();
            orphan.CommandText = "INSERT INTO image_blobs(sha256,image_gzip,compressed_bytes,hash_version) VALUES('orphan',$data,$bytes,1)";
            orphan.Parameters.Add("$data", SqliteType.Blob).Value = raw;
            orphan.Parameters.AddWithValue("$bytes", raw.Length);
            orphan.ExecuteNonQuery();
        }
        var metadata = Links(storage);
        var result = storage.CompressDatabase();
        Require(result.ImageLinks == 3 && result.UniqueImages == 1, "Manual compression must combine shared and inline duplicates and remove orphan payloads.");
        Require(result.AfterBytes < result.BeforeBytes, "Compaction must reclaim physical space released by duplicate and orphan payloads.");
        Require(Links(storage) == metadata, "Every article association, image ID, URL, order, MIME type, and alt text must survive compression.");
        using (var connection = storage.OpenReadOnly())
        {
            Require(Scalar(connection, "SELECT COUNT(*) FROM article_images WHERE length(image_gzip)>0") == 0, "Relinked image rows must not retain duplicate inline payloads.");
            using var hash = connection.CreateCommand();
            hash.CommandText = "SELECT sha256 FROM image_blobs";
            Require(hash.ExecuteScalar()?.ToString() == Storage.ImageContentHash(first), "The survivor must use the original-content hash.");
        }
        using (var backup = new SqliteConnection("Data Source=" + result.BackupPath + ";Mode=ReadOnly;Pooling=False"))
        {
            backup.Open();
            Require(Scalar(backup, "SELECT COUNT(*) FROM image_blobs") == 3 && Scalar(backup, "SELECT COUNT(*) FROM article_images WHERE length(image_gzip)>0") == 1,
                "The verified backup must retain the complete pre-compression image data.");
        }
        var again = storage.CompressDatabase();
        Require(again.BackupPath != result.BackupPath && File.Exists(result.BackupPath) && File.Exists(again.BackupPath), "Repeated compression must preserve each prior backup.");
        Require(again.ImageLinks == 3 && again.UniqueImages == 1 && Links(storage) == metadata, "Compression must be safe to repeat.");
    });

    internal static void VerifyCorruptImageRecovery() => InDatabase((storage, folder) =>
    {
        var data = Gzip([1, 2, 3, 4, 5]);
        data[^8] ^= 0xff; // Invalid gzip checksum.
        Legacy(storage, "corrupt", data, true);
        var metadata = Links(storage);
        bool rejected = false;
        try { storage.CompressDatabase(); } catch (InvalidDataException) { rejected = true; }
        Require(rejected, "Corrupt image data must stop compression instead of being merged under an incorrect hash.");
        Require(Links(storage) == metadata && storage.ArchiveStats().ImageLinks == 1, "A failed pass must preserve image associations.");
        using var connection = storage.OpenReadOnly();
        using var payload = connection.CreateCommand();
        payload.CommandText = "SELECT image_gzip FROM image_blobs";
        Require(((byte[])payload.ExecuteScalar()!).SequenceEqual(data), "A failed pass must preserve the original payload.");
        Require(Directory.GetFiles(folder, "alliance_watch.before-image-dedupe-*.db").Length == 1, "A failed pass must retain its recovery backup.");
    });

    internal static void VerifyCanceledCompression() => InDatabase((storage, folder) =>
    {
        Legacy(storage, "cancel", Gzip([5, 4, 3, 2, 1]), true);
        var metadata = Links(storage);
        bool canceled = false;
        try { storage.CompressDatabase(cancellationToken: new CancellationToken(canceled: true)); } catch (OperationCanceledException) { canceled = true; }
        Require(canceled && Links(storage) == metadata && Directory.GetFiles(folder, "alliance_watch.before-image-dedupe-*.db").Length == 0,
            "An already canceled request must not begin maintenance or create a backup.");
    });

    internal static void VerifyButtonAndMonitoringResume() => InDatabase((storage, folder) =>
    {
        Legacy(storage, "before", Gzip([6, 7, 8, 9]), true);
        using var main = new MainForm(folder, new AppConfig { ArchiveEnabled = false }, storage);
        main.CreateControl();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var button = (Button)typeof(MainForm).GetField("_compressButton", flags)!.GetValue(main)!;
        var oldMonitor = (FeedMonitor)typeof(MainForm).GetField("_monitor", flags)!.GetValue(main)!;
        main.ShowInTaskbar = false;
        main.Opacity = 0;
        main.Show();
        var readyDeadline = DateTime.UtcNow.AddSeconds(10);
        while (!button.Enabled && DateTime.UtcNow < readyDeadline) { Application.DoEvents(); Thread.Sleep(5); }
        Require(button.Enabled, "Compression must become available after the startup scan.");
        DatabaseCompressionResult? completed = null;
        Exception? layoutFailure = null;
        bool timedOut = false;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        using var closeCompleted = new System.Windows.Forms.Timer { Interval = 25 };
        closeCompleted.Tick += (_, _) =>
        {
            var dialog = Application.OpenForms.OfType<DatabaseCompressionForm>().FirstOrDefault();
            if (dialog is null) return;
            if (dialog.ControlBox)
            {
                completed = dialog.Result;
                try { UiLayoutTests.Verify(dialog); } catch (Exception ex) { layoutFailure = ex; }
                dialog.Close();
            }
            else if (DateTime.UtcNow > deadline) { timedOut = true; dialog.Dispose(); }
        };
        closeCompleted.Start();
        button.PerformClick();
        closeCompleted.Stop();
        if (layoutFailure is not null) throw layoutFailure;
        Require(!timedOut && completed is not null && button.Enabled, "The real compression button must complete and become usable again.");
        var resumed = (FeedMonitor)typeof(MainForm).GetField("_monitor", flags)!.GetValue(main)!;
        Require(!ReferenceEquals(oldMonitor, resumed), "The button must restart collection after releasing the paused worker lifetime.");
        Legacy(storage, "after", Gzip([6, 7, 8, 9]), true, hashVersion: 0);
        var resumedInTime = Task.Run(async () =>
        {
            var limit = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < limit)
            {
                if (storage.ArchiveStats().Images == 1 && storage.ArchiveStats().ImageLinks == 2) return true;
                await Task.Delay(20);
            }
            return false;
        }).GetAwaiter().GetResult();
        resumed.StopAsync().GetAwaiter().GetResult();
        Require(resumedInTime, "Automatic deduplication must resume and process later images without another scan.");
    });

    private static void InDatabase(Action<Storage, string> test)
    {
        var folder = Path.Combine(Path.GetTempPath(), "aw-compression-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var storage = new Storage(Path.Combine(folder, "alliance_watch.db"));
            storage.Initialize();
            storage.MigrateAssessment();
            test(storage, folder);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in Directory.GetFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
        }
    }

    private static void Legacy(Storage storage, string article, byte[] data, bool shared, int hashVersion = 1)
    {
        storage.InsertArticle(article, "fixture", "Article " + article, "https://example.invalid/" + article, DateTimeOffset.UtcNow.ToString("O"), "");
        using var connection = new SqliteConnection("Data Source=" + storage.DatabasePath);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        command.Parameters.AddWithValue("$hash", shared ? (object)hash : DBNull.Value);
        command.Parameters.AddWithValue("$article", article);
        command.Parameters.AddWithValue("$bytes", data.Length);
        command.Parameters.AddWithValue("$version", hashVersion);
        command.Parameters.Add("$data", SqliteType.Blob).Value = data;
        if (shared)
        {
            command.CommandText = "INSERT OR IGNORE INTO image_blobs(sha256,image_gzip,compressed_bytes,hash_version) VALUES($hash,$data,$bytes,$version)";
            command.ExecuteNonQuery();
        }
        command.CommandText = "INSERT INTO article_images(article_hash,position,source_url,resolved_url,mime_type,alt_text,original_bytes,compressed_bytes,image_gzip,blob_hash) VALUES($article,0,'original-source','original-resolved','image/png','Original alt text',524288,$bytes,CASE WHEN $hash IS NULL THEN $data ELSE x'' END,$hash)";
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static string Links(Storage storage)
    {
        using var connection = storage.OpenReadOnly();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,article_hash,position,source_url,resolved_url,mime_type,alt_text,original_bytes FROM article_images ORDER BY id";
        using var rows = command.ExecuteReader();
        var metadata = new List<object[]>();
        while (rows.Read()) { var values = new object[rows.FieldCount]; rows.GetValues(values); metadata.Add(values); }
        return System.Text.Json.JsonSerializer.Serialize(metadata);
    }

    private static long Scalar(SqliteConnection connection, string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
    private static byte[] Gzip(byte[] raw) { using var output = new MemoryStream(); using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true)) gzip.Write(raw); return output.ToArray(); }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
