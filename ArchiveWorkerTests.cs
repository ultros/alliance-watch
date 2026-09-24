using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;

namespace AllianceWatch;

internal static class ArchiveWorkerTests
{
    public static void VerifyContinuousDrainAndConcurrency() => Run(async (storage, path) =>
    {
        const int articleCount = 53;
        const int concurrency = 6;
        for (var i = 0; i < articleCount; i++) Seed(storage, "drain-" + i);
        var workersStarted = NewSignal();
        var releaseWorkers = NewSignal();
        var started = 0;
        using var handler = new ArchiveHttpHandler(async (_, token) =>
        {
            if (Interlocked.Increment(ref started) == concurrency) workersStarted.TrySetResult(true);
            await releaseWorkers.Task.WaitAsync(token);
            await Task.Delay(10, token);
            return Html("<article>Stored article content.</article>");
        });
        using var monitor = new FeedMonitor(new AppConfig { ArchiveConcurrency = concurrency }, storage, path + ".log", handler);
        try
        {
            await monitor.ScanAsync();
            await workersStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Require(handler.MaximumConcurrency == concurrency, "The configured archive worker count must be used.");
            releaseWorkers.TrySetResult(true);
            await Eventually(() => storage.ArchiveStats().Complete == articleCount,
                "One feed scan must drain more than the former 24-article archive batch.");
            Require(storage.ArchiveStats().Pending == 0, "All eligible archives must leave the pending queue.");
            Require(handler.MaximumConcurrency <= concurrency, "Archive requests must respect the configured concurrency limit.");

            await monitor.ScanAsync();
            await Task.Delay(100);
            Require(handler.Requests.Count == articleCount && handler.Requests.Values.All(count => count == 1),
                "An additional feed scan must not fetch completed archives again.");
            Require(Scalar(storage, "SELECT COUNT(*) FROM article_archives WHERE attempts <> 1") == 0,
                "Each completed article must be saved exactly once.");
        }
        finally { releaseWorkers.TrySetResult(true); }
    });

    public static void VerifyArticleTimeoutIsolation() => Run(async (storage, path) =>
    {
        const int successfulCount = 29;
        for (var i = 0; i < successfulCount; i++) Seed(storage, "healthy-" + i);
        Seed(storage, "timeout");
        using var handler = new ArchiveHttpHandler((request, _) =>
            request.RequestUri!.AbsolutePath == "/timeout"
                ? Task.FromException<HttpResponseMessage>(new TaskCanceledException("Simulated article request timeout."))
                : Task.FromResult(Html("<article>Healthy article survived another request's timeout.</article>")));
        using var monitor = new FeedMonitor(new AppConfig { ArchiveConcurrency = 2 }, storage, path + ".log", handler);

        await monitor.ScanAsync();
        await Eventually(() => storage.ArchiveStats().Complete == successfulCount &&
            Scalar(storage, "SELECT COUNT(*) FROM article_archives WHERE fetch_status = 'FAILED'") == 1,
            "A request timeout must be recorded without stopping the remaining archive queue.");
        using (var connection = storage.OpenReadOnly())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT fetch_status, attempts, next_attempt_at, last_error FROM article_archives WHERE article_hash = 'timeout'";
            using var reader = command.ExecuteReader();
            Require(reader.Read(), "The timed-out article must have a persisted result.");
            Require(reader.GetString(0) == "FAILED" && reader.GetInt32(1) == 1,
                "A request timeout must count as one failed attempt.");
            Require(DateTimeOffset.Parse(reader.GetString(2)) > DateTimeOffset.UtcNow,
                "Timed-out requests must receive a future retry time.");
            Require(reader.GetString(3).Length > 0, "The timeout must retain a diagnostic error.");
        }
        Require(storage.PendingArchives(100).Count == 0, "A failed archive must remain out of the eligible queue until its backoff expires.");
        await monitor.ScanAsync();
        await Task.Delay(100);
        Require(handler.Requests.Count == successfulCount + 1 && handler.Requests.Values.All(count => count == 1),
            "Another scan must not immediately retry the failed archive or duplicate successful archives.");
    });

    public static void VerifyImageTimeoutIsolation() => Run(async (storage, path) =>
    {
        Seed(storage, "images");
        using var handler = new ArchiveHttpHandler((request, _) =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/images" => Task.FromResult(Html("<article>Article with a partial image archive.<img src='/good.png' alt='saved image'><img src='/timeout.png' alt='timed out image'></article>")),
                "/timeout.png" => Task.FromException<HttpResponseMessage>(new TaskCanceledException("Simulated image request timeout.")),
                "/good.png" => Task.FromResult(Image()),
                _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
            };
        });
        using var monitor = new FeedMonitor(new AppConfig { ArchiveConcurrency = 2 }, storage, path + ".log", handler);

        await monitor.ScanAsync();
        await Eventually(() => storage.ArchiveStats().Complete == 1,
            "An image request timeout must not discard the downloaded article and other images.");
        Require(storage.ArchiveStats().ImageLinks == 1, "The successful image must be saved and the timed-out image skipped.");
        Require(storage.ReadArchivedArticleText("images").Contains("Article with a partial image archive."),
            "The readable article body must survive an image timeout.");
        Require(Scalar(storage, "SELECT COUNT(*) FROM article_images WHERE resolved_url = 'https://archive.example.invalid/good.png'") == 1,
            "Only the successful image should have an archive row.");
        Require(Scalar(storage, "SELECT COUNT(*) FROM article_archives WHERE fetch_status = 'FAILED'") == 0,
            "An individual image timeout must not mark the article as failed.");
        Require(handler.Requests.Count == 3 && handler.Requests.Values.All(count => count == 1),
            "The page and both image candidates must be fetched once.");
    });

    public static void VerifyDisposeCancelsArchive() => Run(async (storage, path) =>
    {
        Seed(storage, "blocked");
        var started = NewSignal();
        var cancelled = NewSignal();
        var release = NewSignal();
        using var handler = new ArchiveHttpHandler(async (_, token) =>
        {
            started.TrySetResult(true);
            try { await release.Task.WaitAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                cancelled.TrySetResult(true);
                throw;
            }
            return Html("<article>This blocked response must never be saved.</article>");
        });
        using var monitor = new FeedMonitor(new AppConfig { ArchiveConcurrency = 1 }, storage, path + ".log", handler);
        await monitor.ScanAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var dispose = Task.Run(monitor.Dispose);
        try { await dispose.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally
        {
            // Release a broken implementation as well so fixture cleanup cannot leave a worker running.
            release.TrySetResult(true);
            await dispose.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Require(cancelled.Task.IsCompletedSuccessfully, "Disposing the monitor must cancel its active archive request.");
        Require(handler.ActiveRequests == 0, "Dispose must wait for the archive request to stop.");
        Require(Scalar(storage, "SELECT COUNT(*) FROM article_archives") == 0,
            "Shutdown cancellation must neither mark a failed attempt nor save a successful archive.");
        Require(storage.PendingArchives(10).Single().ArticleHash == "blocked",
            "A cancelled archive must remain eligible for the next application run.");
    });

    private static void Run(Func<Storage, string, Task> test) => Task.Run(async () =>
    {
        var path = Path.Combine(Path.GetTempPath(), "aw-archive-worker-" + Guid.NewGuid() + ".db");
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

    private static void Seed(Storage storage, string hash) =>
        storage.InsertArticle(hash, "Archive fixture", "Article " + hash, "https://archive.example.invalid/" + hash,
            DateTimeOffset.UtcNow.ToString("O"), "Fixture summary");

    private static long Scalar(Storage storage, string sql)
    {
        using var connection = storage.OpenReadOnly();
        using var command = connection.CreateCommand();
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

    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static HttpResponseMessage Html(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/html")
    };

    private static HttpResponseMessage Image()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        return response;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ArchiveHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private int _active;
        private int _maximum;
        public ConcurrentDictionary<string, int> Requests { get; } = new(StringComparer.Ordinal);
        public int MaximumConcurrency => Volatile.Read(ref _maximum);
        public int ActiveRequests => Volatile.Read(ref _active);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.AddOrUpdate(request.RequestUri!.AbsoluteUri, 1, (_, count) => count + 1);
            var active = Interlocked.Increment(ref _active);
            int previous;
            do { previous = Volatile.Read(ref _maximum); }
            while (active > previous && Interlocked.CompareExchange(ref _maximum, active, previous) != previous);
            try
            {
                var response = await respond(request, cancellationToken);
                response.RequestMessage = request;
                return response;
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
}
