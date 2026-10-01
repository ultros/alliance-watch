// SPDX-License-Identifier: LicenseRef-AllianceWatch-Free-Use-No-Resale
// Copyright (c) 2026 Jesse Lee Shelley. All Rights Reserved.
// Free to run; selling or paid access requires Owner's paid written permission.
// See LICENSE and NOTICE for terms and required attribution.
// Creator: https://linkedin.com/in/jesse-shelley
// Repository: https://github.com/ultros/alliance-watch

using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace AllianceWatch;

internal sealed partial class Storage
{
    internal static string ImageContentHash(byte[] compressedData, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Older imports and local fixtures can contain uncompressed payloads.
        if (compressedData.Length < 2 || compressedData[0] != 0x1f || compressedData[1] != 0x8b)
            return Convert.ToHexString(SHA256.HashData(compressedData)).ToLowerInvariant();
        using var input = new MemoryStream(compressedData, writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int length;
        while ((length = gzip.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, length);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool StoreImageBlob(SqliteConnection connection, SqliteTransaction transaction, string hash, byte[] data)
    {
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT OR IGNORE INTO image_blobs(sha256,image_gzip,compressed_bytes,hash_version) VALUES($hash,$data,$bytes,1)";
        insert.Parameters.AddWithValue("$hash", hash);
        insert.Parameters.Add("$data", SqliteType.Blob).Value = data;
        insert.Parameters.AddWithValue("$bytes", data.Length);
        var added = insert.ExecuteNonQuery() != 0;
        if (!added)
        {
            using var mark = connection.CreateCommand();
            mark.Transaction = transaction;
            mark.CommandText = "UPDATE image_blobs SET hash_version=1 WHERE sha256=$hash AND hash_version=0";
            mark.Parameters.AddWithValue("$hash", hash);
            mark.ExecuteNonQuery();
        }
        return added;
    }

    private static long ImageBlobSize(SqliteConnection connection, SqliteTransaction transaction, string hash)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT compressed_bytes FROM image_blobs WHERE sha256=$hash";
        command.Parameters.AddWithValue("$hash", hash);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    // Read and hash outside the write transaction. Bound both the image count and
    // compressed bytes so migration can coexist with collection and gallery reads.
    internal (int Processed, long ReclaimedBytes) ConsolidateImageBatch(int limit = 25, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        limit = Math.Clamp(limit, 1, 50);
        const long maxBatchBytes = 32 * 1024 * 1024;
        var batch = new List<(string? OldHash, long Id, byte[] Data, string Hash)>();
        long bytes = 0;
        using var connection = Open();
        foreach (var shared in new[] { true, false })
        {
            using var read = connection.CreateCommand();
            read.CommandText = shared
                ? "SELECT sha256,image_gzip FROM image_blobs WHERE hash_version=0 ORDER BY sha256 LIMIT $limit"
                : "SELECT id,image_gzip FROM article_images WHERE blob_hash IS NULL AND length(image_gzip)>0 ORDER BY id LIMIT $limit";
            read.Parameters.AddWithValue("$limit", limit - batch.Count);
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var size = reader.GetBytes(1, 0, null, 0, 0);
                if (batch.Count > 0 && bytes + size > maxBatchBytes) break;
                var data = reader.GetFieldValue<byte[]>(1);
                batch.Add((shared ? reader.GetString(0) : null, shared ? 0 : reader.GetInt64(0),
                    data, ImageContentHash(data, cancellationToken)));
                bytes += size;
            }
            if (batch.Count == limit || bytes >= maxBatchBytes) break;
        }
        if (batch.Count == 0) return (0, 0);

        cancellationToken.ThrowIfCancellationRequested();
        using var transaction = connection.BeginTransaction();
        int processed = 0;
        long reclaimed = 0;
        foreach (var item in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Another worker or a re-archive may have completed this item since
            // the read. Never recreate deleted payloads or overwrite newer links.
            using var current = connection.CreateCommand();
            current.Transaction = transaction;
            current.CommandText = item.OldHash is not null
                ? "SELECT 1 FROM image_blobs WHERE sha256=$old AND hash_version=0"
                : "SELECT 1 FROM article_images WHERE id=$id AND blob_hash IS NULL AND image_gzip=$data";
            current.Parameters.AddWithValue("$old", (object?)item.OldHash ?? DBNull.Value);
            current.Parameters.AddWithValue("$id", item.Id);
            current.Parameters.Add("$data", SqliteType.Blob).Value = item.Data;
            if (current.ExecuteScalar() is null) continue;

            var added = StoreImageBlob(connection, transaction, item.Hash, item.Data);
            var size = ImageBlobSize(connection, transaction, item.Hash);
            using var link = connection.CreateCommand();
            link.Transaction = transaction;
            link.CommandText = item.OldHash is not null
                ? "UPDATE article_images SET blob_hash=$hash,image_gzip=x'',compressed_bytes=$bytes WHERE blob_hash=$old"
                : "UPDATE article_images SET blob_hash=$hash,image_gzip=x'',compressed_bytes=$bytes WHERE id=$id AND blob_hash IS NULL";
            link.Parameters.AddWithValue("$hash", item.Hash);
            link.Parameters.AddWithValue("$bytes", size);
            link.Parameters.AddWithValue("$old", (object?)item.OldHash ?? DBNull.Value);
            link.Parameters.AddWithValue("$id", item.Id);
            link.ExecuteNonQuery();
            if (item.OldHash is not null && item.OldHash != item.Hash)
            {
                using var remove = connection.CreateCommand();
                remove.Transaction = transaction;
                remove.CommandText = "DELETE FROM image_blobs WHERE sha256=$old AND NOT EXISTS (SELECT 1 FROM article_images WHERE blob_hash=$old)";
                remove.Parameters.AddWithValue("$old", item.OldHash);
                remove.ExecuteNonQuery();
            }
            if (!added && item.OldHash != item.Hash) reclaimed += item.Data.Length;
            processed++;
        }
        transaction.Commit();
        return (processed, reclaimed);
    }

    // The optional offline command uses exactly the same resumable migration as
    // the background worker, then can compact the database to shrink the file.
    public (long Links, long UniqueBlobs, long ReclaimedBytes) ConsolidateImages(Action<long, long>? progress = null)
    {
        long processed = 0, reclaimed = 0;
        using var connection = Open();
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT (SELECT COUNT(*) FROM image_blobs WHERE hash_version=0)+(SELECT COUNT(*) FROM article_images WHERE blob_hash IS NULL AND length(image_gzip)>0)";
        var total = Convert.ToInt64(count.ExecuteScalar());
        while (true)
        {
            var batch = ConsolidateImageBatch();
            if (batch.Processed == 0) break;
            processed += batch.Processed;
            reclaimed += batch.ReclaimedBytes;
            progress?.Invoke(processed, Math.Max(processed, total));
        }
        using var final = connection.CreateCommand();
        final.CommandText = "SELECT (SELECT COUNT(*) FROM article_images),(SELECT COUNT(*) FROM image_blobs)";
        using var result = final.ExecuteReader();
        result.Read();
        return (result.GetInt64(0), result.GetInt64(1), reclaimed);
    }
}
