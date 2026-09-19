using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AllianceWatch;

internal sealed partial class Storage(string databasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared
    }.ToString();

    public void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;
            CREATE TABLE IF NOT EXISTS articles (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                article_hash TEXT NOT NULL UNIQUE,
                feed_name TEXT NOT NULL,
                title TEXT NOT NULL,
                url TEXT NOT NULL,
                published TEXT NOT NULL,
                summary TEXT NOT NULL,
                first_seen TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS matches (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                article_hash TEXT NOT NULL UNIQUE,
                severity TEXT NOT NULL,
                score INTEGER NOT NULL,
                matched_phrases TEXT NOT NULL,
                matched_actors TEXT NOT NULL,
                source_weight INTEGER NOT NULL,
                alerted INTEGER NOT NULL DEFAULT 0,
                detected_at TEXT NOT NULL,
                FOREIGN KEY (article_hash) REFERENCES articles(article_hash)
            );
            CREATE INDEX IF NOT EXISTS idx_matches_severity ON matches(severity, score);
            CREATE INDEX IF NOT EXISTS idx_articles_first_seen ON articles(first_seen);

            CREATE TABLE IF NOT EXISTS article_archives (
                article_hash TEXT PRIMARY KEY,
                final_url TEXT NOT NULL,
                content_type TEXT NOT NULL,
                html_gzip BLOB,
                text_gzip BLOB,
                html_bytes INTEGER NOT NULL DEFAULT 0,
                text_bytes INTEGER NOT NULL DEFAULT 0,
                compressed_bytes INTEGER NOT NULL DEFAULT 0,
                image_count INTEGER NOT NULL DEFAULT 0,
                fetch_status TEXT NOT NULL,
                last_error TEXT NOT NULL DEFAULT '',
                attempts INTEGER NOT NULL DEFAULT 0,
                fetched_at TEXT NOT NULL,
                FOREIGN KEY (article_hash) REFERENCES articles(article_hash) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS article_images (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                article_hash TEXT NOT NULL,
                position INTEGER NOT NULL,
                source_url TEXT NOT NULL,
                resolved_url TEXT NOT NULL,
                mime_type TEXT NOT NULL,
                alt_text TEXT NOT NULL,
                original_bytes INTEGER NOT NULL,
                compressed_bytes INTEGER NOT NULL,
                image_gzip BLOB NOT NULL,
                FOREIGN KEY (article_hash) REFERENCES articles(article_hash) ON DELETE CASCADE,
                UNIQUE(article_hash, resolved_url)
            );
            CREATE INDEX IF NOT EXISTS idx_archives_status ON article_archives(fetch_status, attempts);
            CREATE INDEX IF NOT EXISTS idx_article_images_hash ON article_images(article_hash, position);
            """;
        command.ExecuteNonQuery();
    }

    public bool InsertArticle(string hash, string feed, string title, string url, string published, string summary)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO articles
                (article_hash, feed_name, title, url, published, summary, first_seen)
            VALUES ($hash, $feed, $title, $url, $published, $summary, $seen)
            """;
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$feed", feed);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$url", url);
        command.Parameters.AddWithValue("$published", published);
        command.Parameters.AddWithValue("$summary", summary);
        command.Parameters.AddWithValue("$seen", DateTimeOffset.UtcNow.ToString("O"));
        return command.ExecuteNonQuery() == 1;
    }

    public string ResolveArticleVersion(string hash, string title, string summary)
    {
        using var connection=Open(); using var command=connection.CreateCommand();
        command.CommandText="SELECT title,summary FROM articles WHERE article_hash=$hash";
        command.Parameters.AddWithValue("$hash",hash);
        using var reader=command.ExecuteReader();
        if(!reader.Read() || reader.GetString(0)==title && reader.GetString(1)==summary) return hash;
        return hash+"-revision-"+AssessmentEngine.Hash(title+"\n"+summary)[..16];
    }

    public IReadOnlyList<PendingArticle> PendingArchives(int limit)
    {
        var articles = new List<PendingArticle>();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.article_hash, a.url, a.title
            FROM articles a
            LEFT JOIN article_archives aa ON aa.article_hash = a.article_hash
            WHERE TRIM(a.url) <> ''
              AND (aa.article_hash IS NULL OR aa.fetch_status <> 'COMPLETE')
            ORDER BY a.first_seen DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", Math.Max(1, limit));
        using var reader = command.ExecuteReader();
        while (reader.Read())
            articles.Add(new PendingArticle(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return articles;
    }

    public void SaveArticleArchive(ArticleArchive archive)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO article_archives
                    (article_hash, final_url, content_type, html_gzip, text_gzip,
                     html_bytes, text_bytes, compressed_bytes, image_count,
                     fetch_status, last_error, attempts, fetched_at)
                VALUES
                    ($hash, $url, $type, $html, $text, $htmlBytes, $textBytes,
                     $compressedBytes, $imageCount, 'COMPLETE', '', 1, $fetched)
                ON CONFLICT(article_hash) DO UPDATE SET
                    final_url = excluded.final_url,
                    content_type = excluded.content_type,
                    html_gzip = excluded.html_gzip,
                    text_gzip = excluded.text_gzip,
                    html_bytes = excluded.html_bytes,
                    text_bytes = excluded.text_bytes,
                    compressed_bytes = excluded.compressed_bytes,
                    image_count = excluded.image_count,
                    fetch_status = 'COMPLETE',
                    last_error = '',
                    attempts = article_archives.attempts + 1,
                    fetched_at = excluded.fetched_at
                """;
            command.Parameters.AddWithValue("$hash", archive.ArticleHash);
            command.Parameters.AddWithValue("$url", archive.FinalUrl);
            command.Parameters.AddWithValue("$type", archive.ContentType);
            command.Parameters.Add("$html", SqliteType.Blob).Value = archive.CompressedHtml;
            command.Parameters.Add("$text", SqliteType.Blob).Value = archive.CompressedText;
            command.Parameters.AddWithValue("$htmlBytes", archive.HtmlBytes);
            command.Parameters.AddWithValue("$textBytes", archive.TextBytes);
            command.Parameters.AddWithValue("$compressedBytes",
                archive.CompressedHtml.Length + archive.CompressedText.Length + archive.Images.Sum(x => x.CompressedData.Length));
            command.Parameters.AddWithValue("$imageCount", archive.Images.Count);
            command.Parameters.AddWithValue("$fetched", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM article_images WHERE article_hash = $hash";
            delete.Parameters.AddWithValue("$hash", archive.ArticleHash);
            delete.ExecuteNonQuery();
        }

        foreach (var image in archive.Images)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO article_images
                    (article_hash, position, source_url, resolved_url, mime_type,
                     alt_text, original_bytes, compressed_bytes, image_gzip)
                VALUES ($hash, $position, $source, $resolved, $type, $alt,
                        $originalBytes, $compressedBytes, $data)
                """;
            command.Parameters.AddWithValue("$hash", archive.ArticleHash);
            command.Parameters.AddWithValue("$position", image.Position);
            command.Parameters.AddWithValue("$source", image.SourceUrl);
            command.Parameters.AddWithValue("$resolved", image.ResolvedUrl);
            command.Parameters.AddWithValue("$type", image.MimeType);
            command.Parameters.AddWithValue("$alt", image.AltText);
            command.Parameters.AddWithValue("$originalBytes", image.OriginalBytes);
            command.Parameters.AddWithValue("$compressedBytes", image.CompressedData.Length);
            command.Parameters.Add("$data", SqliteType.Blob).Value = image.CompressedData;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void MarkArchiveFailure(string articleHash, string url, string error)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO article_archives
                (article_hash, final_url, content_type, fetch_status, last_error, attempts, fetched_at)
            VALUES ($hash, $url, '', 'FAILED', $error, 1, $fetched)
            ON CONFLICT(article_hash) DO UPDATE SET
                final_url = excluded.final_url,
                fetch_status = 'FAILED',
                last_error = excluded.last_error,
                attempts = article_archives.attempts + 1,
                fetched_at = excluded.fetched_at
            """;
        command.Parameters.AddWithValue("$hash", articleHash);
        command.Parameters.AddWithValue("$url", url);
        command.Parameters.AddWithValue("$error", error.Length > 1500 ? error[..1500] : error);
        command.Parameters.AddWithValue("$fetched", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public (int Complete, int Pending, int Images, long CompressedBytes) ArchiveStats()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM article_archives WHERE fetch_status = 'COMPLETE'),
              (SELECT COUNT(*) FROM articles a LEFT JOIN article_archives aa ON aa.article_hash = a.article_hash
                 WHERE TRIM(a.url) <> '' AND (aa.article_hash IS NULL OR aa.fetch_status <> 'COMPLETE')),
              (SELECT COUNT(*) FROM article_images),
              (SELECT COALESCE(SUM(compressed_bytes), 0) FROM article_archives)
            """;
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt64(3));
    }

    public long InsertMatch(MatchResult result)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO matches
                (article_hash, severity, score, matched_phrases, matched_actors,
                 source_weight, alerted, detected_at)
            VALUES ($hash, $severity, $score, $phrases, $actors, $weight, 0, $detected);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$hash", result.ArticleHash);
        command.Parameters.AddWithValue("$severity", result.Severity);
        command.Parameters.AddWithValue("$score", result.Score);
        command.Parameters.AddWithValue("$phrases", JsonSerializer.Serialize(result.Phrases));
        command.Parameters.AddWithValue("$actors", JsonSerializer.Serialize(result.Actors));
        command.Parameters.AddWithValue("$weight", result.SourceWeight);
        command.Parameters.AddWithValue("$detected", DateTimeOffset.UtcNow.ToString("O"));
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    public void MarkAlerted(long matchId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE matches SET alerted = 1 WHERE id = $id";
        command.Parameters.AddWithValue("$id", matchId);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<DashboardEvent> RecentEvents(int limit = 150)
    {
        var events = new List<DashboardEvent>();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.id, m.severity, m.score, a.title, a.feed_name, a.published,
                   m.matched_actors, m.matched_phrases, m.source_weight, a.url,
                   m.detected_at, m.alerted
            FROM matches m
            JOIN articles a ON a.article_hash = m.article_hash
            ORDER BY m.detected_at DESC, m.score DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var actors = string.Join(" / ", JsonSerializer.Deserialize<List<string>>(reader.GetString(6)) ?? []);
            var indicators = string.Join(" / ", JsonSerializer.Deserialize<List<string>>(reader.GetString(7)) ?? []);
            DateTime.TryParse(reader.GetString(10), out var detectedAt);
            events.Add(new DashboardEvent(
                reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), actors, indicators, reader.GetInt32(8),
                reader.GetString(9), detectedAt, reader.GetInt32(11) != 0));
        }
        return events;
    }

    public DashboardStats GetStats()
    {
        using var connection = Open();
        int Scalar(string sql)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }

        var scores = new List<int>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT score FROM matches ORDER BY detected_at DESC LIMIT 32";
            using var reader = command.ExecuteReader();
            while (reader.Read()) scores.Add(reader.GetInt32(0));
        }

        return new DashboardStats(
            Scalar("SELECT COUNT(*) FROM articles"),
            Scalar("SELECT COUNT(*) FROM matches"),
            Scalar("SELECT COUNT(*) FROM matches WHERE severity = 'RED'"),
            Scalar("SELECT COUNT(*) FROM matches WHERE severity = 'ORANGE'"),
            Scalar("SELECT COUNT(*) FROM matches WHERE severity = 'YELLOW'"),
            Scalar("SELECT COALESCE(MAX(score), 0) FROM matches"),
            scores);
    }

    public IReadOnlyList<DashboardEvent> UnreadAlerts(int limit = 20) =>
        RecentEvents(Math.Max(150, limit)).Where(x => !x.Alerted && x.Severity != "LOG ONLY").Take(limit).ToList();

    public void MarkAllAlerted()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE matches SET alerted = 1 WHERE alerted = 0";
        command.ExecuteNonQuery();
    }

    public int RecalculateScores()
    {
        var updates = new List<(long Id, int Score, string Severity)>();
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT m.id, a.title, a.summary, m.source_weight
                FROM matches m JOIN articles a ON a.article_hash = m.article_hash
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var text = $"{reader.GetString(1)} {reader.GetString(2)}";
                var indicators = IndicatorEngine.DetectIndicators(text);
                var actors = IndicatorEngine.DetectActors(text);
                var score = IndicatorEngine.CalculateScore(indicators, actors, reader.GetInt32(3), text);
                updates.Add((reader.GetInt64(0), score, IndicatorEngine.ClassifySeverity(score)));
            }
        }

        using var updateConnection = Open();
        using var transaction = updateConnection.BeginTransaction();
        foreach (var update in updates)
        {
            using var command = updateConnection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE matches SET score = $score, severity = $severity WHERE id = $id";
            command.Parameters.AddWithValue("$score", update.Score);
            command.Parameters.AddWithValue("$severity", update.Severity);
            command.Parameters.AddWithValue("$id", update.Id);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        return updates.Count;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        command.ExecuteNonQuery();
        return connection;
    }
}
