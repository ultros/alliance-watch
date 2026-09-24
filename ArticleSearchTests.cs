using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;

namespace AllianceWatch;

internal static class ArticleSearchTests
{
    internal static void VerifyAllFieldsAndOldArticles()
    {
        var path = Path.Combine(Path.GetTempPath(), "aw-article-search-" + Guid.NewGuid() + ".db");
        try
        {
            var storage = new Storage(path);
            storage.Initialize();
            storage.MigrateAssessment();
            storage.InsertArticle("old-fixture", "Historic wire", "Old dispatch", "https://example.invalid/old",
                "2003-03-01T00:00:00Z", "Archive summary with literal % token");
            storage.InsertArticle("signal-fixture", "Signal wire", "Recent dispatch", "https://example.invalid/new",
                DateTimeOffset.UtcNow.ToString("O"), "Routine summary");
            storage.InsertArticle("other-fixture", "Other wire", "Unrelated report", "https://example.invalid/other",
                DateTimeOffset.UtcNow.ToString("O"), "Nothing to match");
            storage.InsertMatch(new MatchResult { ArticleHash = "signal-fixture", Severity = "YELLOW", Score = 5,
                Phrases = ["uniquephrase"], Actors = ["CustomActor"] });
            var archivedText = new string('x', 8190) + "CROSSBOUNDARY vellum operation";
            var textGzip = Gzip(archivedText);
            var htmlGzip = Gzip("<html><body>RawHTMLMarker</body></html>");
            storage.SaveArticleArchive(new ArticleArchive("old-fixture", "https://archive.example/old", "text/html",
                40, Encoding.UTF8.GetByteCount(archivedText), htmlGzip, textGzip,
                [new ArchivedImage(0, "https://example.invalid/source.png", "https://example.invalid/blue.png",
                    "image/png", "hidden blue marker", 4, [1, 2, 3])]));
            storage.SetNewsIgnored("old-fixture", true, "analyst exclusion reason sentinel");

            ArticleSearchResult Only(string term)
            {
                var result = storage.SearchArticles(term).Results;
                if (result.Length != 1) throw new InvalidOperationException($"Expected one result for '{term}', got {result.Length}");
                return result[0];
            }
            Require(Only("2003-03-01").ArticleHash == "old-fixture", "Old publication dates must be searchable");
            Require(Only("literal % token").ArticleHash == "old-fixture", "Search text must be literal, not a SQL wildcard");
            Require(Only("uniquephrase").ArticleHash == "signal-fixture", "Linked signal fields must be searchable");
            Require(Only("hidden blue marker").ArticleHash == "old-fixture", "Image alt text must be searchable");
            Require(Only("archive.example").ArticleHash == "old-fixture", "Archive metadata must be searchable");
            Require(Only("exclusion reason sentinel").ArticleHash == "old-fixture", "Article exclusion notes must be searchable");
            var body = Only("CROSSBOUNDARY vellum");
            Require(body.ArticleHash == "old-fixture" && body.MatchedField == "ARCHIVED TEXT" && body.IgnoredForScore,
                "Compressed article text, including chunk boundaries and ignored news, must be searchable");
            Require(Only("RawHTMLMarker").MatchedField == "ARCHIVED HTML", "Raw archived HTML must be searchable");
            Require(storage.SearchArticles("").Results.Length == 3, "Blank search must browse all stored articles");
            Require(storage.ReadArchivedArticleText("old-fixture").Contains("CROSSBOUNDARY vellum operation"),
                "Search results must offer a readable local archive preview");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { storage.SearchArticles("anything", cancellationToken: cancelled.Token); throw new InvalidOperationException("Search did not cancel"); }
            catch (OperationCanceledException) { }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    private static byte[] Gzip(string text)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            gzip.Write(bytes);
        }
        return output.ToArray();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
