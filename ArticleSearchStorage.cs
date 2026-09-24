using System.IO.Compression;
using System.Text;

namespace AllianceWatch;

internal sealed record ArticleSearchResult(long Id, string ArticleHash, string Title, string FeedName,
    string Published, string FirstSeen, string Url, string Summary, string MatchedField, bool IgnoredForScore);

internal sealed record ArticleSearchProgress(int Scanned, int Total, int Matches);
internal sealed record ArticleSearchOutcome(ArticleSearchResult[] Results, int Scanned, int UnreadableArchives);

internal sealed partial class Storage
{
    // A read-only, literal substring scan deliberately covers old articles and
    // fields that are not present in the recent dashboard or any one browser view.
    // Archive bodies remain gzip-compressed in SQLite and are streamed in chunks
    // only if the structured fields did not already match.
    internal ArticleSearchOutcome SearchArticles(string text, IProgress<ArticleSearchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var term = text.Trim();
        if (term.Length > 512) throw new ArgumentException("Use a search term of 512 characters or fewer.", nameof(text));
        var results = new List<ArticleSearchResult>();
        var scanned = 0;
        var unreadable = 0;
        using var connection = OpenReadOnly();
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM articles";
        var total = Convert.ToInt32(count.ExecuteScalar() ?? 0);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.id AS id, a.article_hash AS article_hash, a.feed_name AS feed_name,
                   a.title AS title, a.url AS url, a.published AS published,
                   a.summary AS summary, a.first_seen AS first_seen,
                   m.severity AS severity, m.score AS score, m.matched_phrases AS matched_phrases,
                   m.matched_actors AS matched_actors, m.source_weight AS source_weight,
                   m.alerted AS alerted, m.detected_at AS detected_at,
                   aa.final_url AS final_url, aa.content_type AS content_type,
                   aa.html_bytes AS html_bytes, aa.text_bytes AS text_bytes,
                   aa.compressed_bytes AS compressed_bytes, aa.image_count AS image_count,
                   aa.fetch_status AS fetch_status, aa.last_error AS last_error,
                   aa.attempts AS attempts, aa.next_attempt_at AS next_attempt_at,
                   aa.fetched_at AS fetched_at, e.event_id AS event_id,
                   e.cluster_id AS cluster_id, e.canonical_url AS canonical_url,
                   e.canonical_hash AS canonical_hash, e.published_at AS evidence_published,
                   e.first_seen_at AS evidence_first_seen, e.payload AS evidence_payload,
                   (SELECT group_concat(
                       i.position || ' ' || i.source_url || ' ' || i.resolved_url || ' ' ||
                       i.mime_type || ' ' || i.alt_text || ' ' || i.original_bytes || ' ' ||
                       i.compressed_bytes || ' ' || COALESCE(i.blob_hash,''), ' ')
                    FROM article_images i WHERE i.article_hash=a.article_hash) AS image_metadata,
                   EXISTS(SELECT 1 FROM aw_score_exclusions x
                          WHERE x.record_id=a.article_hash AND x.active=1) AS ignored_for_score,
                   x.reason AS ignore_reason, x.ignored_at AS ignored_at,
                   x.updated_at AS ignore_updated_at,
                   aa.text_gzip AS archived_text, aa.html_gzip AS archived_html
            FROM articles a
            LEFT JOIN matches m ON m.article_hash=a.article_hash
            LEFT JOIN article_archives aa ON aa.article_hash=a.article_hash
            LEFT JOIN aw_events e ON e.record_id=a.article_hash
            LEFT JOIN aw_score_exclusions x ON x.record_id=a.article_hash
            ORDER BY a.id DESC
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            scanned++;
            string Value(int column) => reader.IsDBNull(column) ? "" : Convert.ToString(reader.GetValue(column)) ?? "";
            var matched = term.Length == 0 ? "ALL ARTICLES" : "";
            if (term.Length > 0)
            {
                for (var column = 0; column <= 37; column++)
                {
                    if (!Value(column).Contains(term, StringComparison.OrdinalIgnoreCase)) continue;
                    matched = reader.GetName(column).Replace('_', ' ').ToUpperInvariant();
                    break;
                }
                if (matched.Length == 0)
                {
                    foreach (var (column, label) in new[] { (38, "ARCHIVED TEXT"), (39, "ARCHIVED HTML") })
                    {
                        if (reader.IsDBNull(column)) continue;
                        try
                        {
                            if (!CompressedContains(reader.GetFieldValue<byte[]>(column), term, cancellationToken)) continue;
                            matched = label;
                            break;
                        }
                        catch (Exception ex) when (ex is InvalidDataException or IOException)
                        {
                            unreadable++;
                        }
                    }
                }
            }
            if (matched.Length > 0)
                results.Add(new ArticleSearchResult(reader.GetInt64(0), Value(1), Value(3), Value(2),
                    Value(5), Value(7), Value(4), Value(6), matched, reader.GetInt64(34) != 0));
            if (scanned % 250 == 0) progress?.Report(new ArticleSearchProgress(scanned, total, results.Count));
        }
        progress?.Report(new ArticleSearchProgress(scanned, total, results.Count));
        return new ArticleSearchOutcome(results.ToArray(), scanned, unreadable);
    }

    internal string ReadArchivedArticleText(string articleHash, int maxCharacters = 30_000)
    {
        using var connection = OpenReadOnly();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT text_gzip FROM article_archives WHERE article_hash=$hash";
        command.Parameters.AddWithValue("$hash", articleHash);
        if (command.ExecuteScalar() is not byte[] compressed || compressed.Length == 0) return "No extracted article text is stored.";
        try
        {
            using var input = new MemoryStream(compressed, writable: false);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            var buffer = new char[maxCharacters + 1];
            var count = 0;
            while (count < buffer.Length)
            {
                var read = reader.Read(buffer, count, buffer.Length - count);
                if (read == 0) break;
                count += read;
            }
            return new string(buffer, 0, Math.Min(count, maxCharacters)) +
                (count > maxCharacters ? "\r\n\r\n[Preview truncated; the full archive remains in the database.]" : "");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return "Archived text could not be opened: " + ex.Message;
        }
    }

    private static bool CompressedContains(byte[] compressed, string term, CancellationToken cancellationToken)
    {
        if (compressed.Length == 0) return false;
        using var input = new MemoryStream(compressed, writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        var buffer = new char[8192];
        var carry = "";
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = reader.Read(buffer, 0, buffer.Length);
            if (read == 0) return false;
            var chunk = carry + new string(buffer, 0, read);
            if (chunk.Contains(term, StringComparison.OrdinalIgnoreCase)) return true;
            carry = chunk.Length >= term.Length ? chunk[^Math.Min(term.Length - 1, chunk.Length)..] : chunk;
        }
    }
}
