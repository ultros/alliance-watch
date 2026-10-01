// SPDX-License-Identifier: LicenseRef-AllianceWatch-Free-Use-No-Resale
// Copyright (c) 2026 Jesse Lee Shelley. All Rights Reserved.
// Free to run; selling or paid access requires Owner's paid written permission.
// See LICENSE and NOTICE for terms and required attribution.
// Creator: https://linkedin.com/in/jesse-shelley
// Repository: https://github.com/ultros/alliance-watch

namespace AllianceWatch;

internal sealed record DatabaseCompressionProgress(string Stage, long Processed = 0, long Total = 0);
internal sealed record DatabaseCompressionResult(string BackupPath, long BeforeBytes, long AfterBytes,
    long ImageLinks, long UniqueImages, long DuplicatePayloadBytes);

internal sealed partial class Storage
{
    // Call with collection paused. The backup remains available even if a later
    // batch or compaction fails; committed image batches are safe to resume.
    public DatabaseCompressionResult CompressDatabase(IProgress<DatabaseCompressionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var before = DatabaseBytes();
        progress?.Report(new("Creating a verified backup"));
        var backup = BackupBeforeImageConsolidation();
        cancellationToken.ThrowIfCancellationRequested();
        VerifyImageStorage();

        progress?.Report(new("Preparing image content verification"));
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            // The manual pass rechecks even blobs marked current. Different
            // gzip encodings or imported stale hashes must converge too.
            command.CommandText = "UPDATE image_blobs SET hash_version=0 WHERE hash_version<>0";
            command.ExecuteNonQuery();
        }
        var images = ConsolidateImages((done, total) => progress?.Report(new("Relinking identical images", done, total)), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM image_blobs WHERE NOT EXISTS (SELECT 1 FROM article_images WHERE blob_hash=image_blobs.sha256)";
            command.ExecuteNonQuery();
        }
        VerifyImageStorage();
        progress?.Report(new("Reclaiming unused database space"));
        CompactDatabase(cancellationToken);
        progress?.Report(new("Verifying database and image links"));
        VerifyImageStorage();
        using (var connection = OpenReadOnly())
        using (var command = connection.CreateCommand())
        {
            command.CommandTimeout = 600;
            using var cancellation = cancellationToken.Register(command.Cancel);
            command.CommandText = "PRAGMA quick_check";
            if (command.ExecuteScalar()?.ToString() != "ok")
                throw new InvalidDataException("Compressed database integrity verification failed.");
            command.CommandText = "PRAGMA foreign_key_check";
            using var violations = command.ExecuteReader();
            if (violations.Read()) throw new InvalidDataException("Compressed database has a broken reference.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var stats = ArchiveStats();
        return new(backup, before, DatabaseBytes(), stats.ImageLinks, stats.Images, images.ReclaimedBytes);
    }

    private long DatabaseBytes() => new[] { databasePath, databasePath + "-wal" }
        .Where(File.Exists).Sum(path => new FileInfo(path).Length);
}
