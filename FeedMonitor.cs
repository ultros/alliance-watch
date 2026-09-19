using System.Net;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AllianceWatch;

internal sealed partial class FeedMonitor : IDisposable
{
    private readonly AppConfig _config;
    private readonly Storage _storage;
    private readonly string _logPath;
    private readonly HttpClient _client;
    private readonly object _logLock = new();
    private Task _archiveWork = Task.CompletedTask;
    private const int MaxImagesPerArticle = 24;
    private const int MaxImageBytes = 20 * 1024 * 1024;

    public FeedMonitor(AppConfig config, Storage storage, string logPath, HttpMessageHandler? handler = null)
    {
        _config = config;
        _storage = storage;
        _logPath = logPath;
        _client = new HttpClient(handler ?? new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10
        }) { Timeout = TimeSpan.FromSeconds(25) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("AllianceWatch/2.0 (local RSS monitor)");
        _client.DefaultRequestHeaders.Accept.ParseAdd("application/rss+xml");
        _client.DefaultRequestHeaders.Accept.ParseAdd("application/atom+xml");
        _client.DefaultRequestHeaders.Accept.ParseAdd("application/xml");
    }

    public async Task<ScanResult> ScanAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var health = _storage.FeedHealthRecords().ToDictionary(h => h.Url);
        var results = new System.Collections.Concurrent.ConcurrentBag<(FeedConfig Feed, FeedEntry[] Entries, FeedHealth Health)>();
        await Parallel.ForEachAsync(_config.Feeds.Where(f => f.Enabled),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (feed, token) =>
        {
            var previous = health.GetValueOrDefault(feed.Url) ?? new FeedHealth(feed.Url, feed.Name);
            if (previous.NextFetch > DateTimeOffset.UtcNow) { results.Add((feed, [], previous)); return; }
            progress?.Report($"POLLING // {feed.Name.ToUpperInvariant()}");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, feed.Url);
                if (previous.ETag.Length > 0) request.Headers.TryAddWithoutValidation("If-None-Match", previous.ETag);
                if (previous.LastModified.Length > 0) request.Headers.TryAddWithoutValidation("If-Modified-Since", previous.LastModified);
                if (feed.BearerTokenEnvironmentVariable.Length > 0)
                {
                    var tokenValue = Environment.GetEnvironmentVariable(feed.BearerTokenEnvironmentVariable);
                    if (string.IsNullOrWhiteSpace(tokenValue)) throw new InvalidDataException("Missing configured API token environment variable.");
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenValue);
                }
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                var status = (int)response.StatusCode;
                previous = previous with { Status = status };
                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    results.Add((feed, [], previous with { LastSuccess = DateTimeOffset.UtcNow, NextFetch = DateTimeOffset.UtcNow.AddMinutes(feed.IntervalMinutes), LatencyMs = watch.ElapsedMilliseconds, Failures = 0, Error = "" }));
                    return;
                }
                if (status == 429)
                {
                    var retry = response.Headers.RetryAfter?.Date ?? DateTimeOffset.UtcNow.Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(30));
                    results.Add((feed, [], previous with { NextFetch = retry, Failures = previous.Failures + 1, LatencyMs = watch.ElapsedMilliseconds, Error = "Rate limited" }));
                    return;
                }
                response.EnsureSuccessStatusCode();
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(25));
                var bytes = await ReadLimitedAsync(await response.Content.ReadAsStreamAsync(readTimeout.Token), 8 * 1024 * 1024, readTimeout.Token);
                var content = Encoding.UTF8.GetString(bytes);
                var entries = SourceAdapters.For(feed.Adapter).Parse(content, feed);
                results.Add((feed, entries, previous with { ETag = response.Headers.ETag?.ToString() ?? "", LastModified = response.Content.Headers.LastModified?.ToString("R") ?? "", LastSuccess = DateTimeOffset.UtcNow, NextFetch = DateTimeOffset.UtcNow.AddMinutes(feed.IntervalMinutes), LatencyMs = watch.ElapsedMilliseconds, Failures = 0, Items = entries.Length, Error = "" }));
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                results.Add((feed, [], previous with { NextFetch = DateTimeOffset.UtcNow.AddMinutes(Math.Min(360, feed.IntervalMinutes * Math.Pow(2, Math.Min(previous.Failures + 1, 6)))), LatencyMs = watch.ElapsedMilliseconds, Failures = previous.Failures + 1, Error = ex.Message }));
                Log("ERROR", $"feed={feed.Name} fetch failed: {ex.Message}");
            }
        });
        var alerts = new List<MatchResult>(); var logOnly = new List<MatchResult>(); var states = new List<FeedState>(); int newArticles = 0;
        var engine = new AssessmentEngine(_config.Assessment);
        // Serialize persistence after bounded parallel fetches; one failed feed cannot roll back its peers.
        foreach (var item in results.OrderBy(x => x.Feed.Name))
        {
            int duplicates = 0, signals = 0;
            foreach (var entry in item.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var title = NormalizeText(entry.Title); if (title.Length == 0) continue;
                    var summary = NormalizeText(entry.Summary); var url = NormalizeText(entry.Url); var published = ParsePublished(entry.Published);
                    var hash = ArticleIdentifier(item.Feed.Name, entry.Guid, title, url);
                    hash = _storage.ResolveArticleVersion(hash,title,summary);
                    if (!_storage.InsertArticle(hash, item.Feed.Name, title, url, published, summary)) { duplicates++; continue; }
                    newArticles++;
                    var now = DateTimeOffset.UtcNow;
                    var detected = engine.Extract(new(hash, title, summary, url, item.Feed.Name, DateTimeOffset.TryParse(published, out var at) ? at : now, now, item.Feed.SourceQuality, item.Feed.Origin));
                    if (detected.Protocols.Length == 0) continue;
                    signals++;
                    var result = new MatchResult { ArticleHash = hash, FeedName = item.Feed.Name, Title = title, Url = url, Published = published, Severity = "LOG ONLY", Score = (int)Math.Round(detected.Severity), Phrases = detected.Protocols.Select(id => _config.Assessment.Protocols.First(p => p.Id == id).Name).ToList(), Actors = detected.Actors.ToList(), SourceWeight = item.Feed.Weight };
                    result.MatchId = _storage.InsertMatch(result); logOnly.Add(result);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested) { Log("ERROR", $"feed={item.Feed.Name} entry failed: {ex.Message}"); }
            }
            var updated = item.Entries.Length > 0 ? item.Health with { Duplicates = duplicates, Signals = signals } : item.Health;
            _storage.SaveFeedHealth(updated);
            states.Add(new(item.Feed.Name, updated.Items, updated.Failures == 0, updated.Error.Length > 0 ? updated.Error : $"HTTP {updated.Status} // NEXT {updated.NextFetch:HH:mm}"));
        }
        // Archive remains available, bounded independently of assessment work.
        var archiveQueue = _config.ArchiveEnabled ? _storage.PendingArchives(12) : [];
        if (archiveQueue.Count > 0 && _archiveWork.IsCompleted) _archiveWork = ArchiveInBackgroundAsync(archiveQueue, cancellationToken);
        return new ScanResult(alerts, logOnly, states, newArticles);
    }
    private async Task ArchiveInBackgroundAsync(IReadOnlyCollection<PendingArticle> articles,CancellationToken token)
    {
        try{await ArchiveArticlesAsync(articles,null,token);}
        catch(OperationCanceledException) when(token.IsCancellationRequested) { }
        catch(Exception ex){Log("ERROR","Archive worker: "+ex.Message);}
    }
    private async Task ArchiveArticlesAsync(
        IReadOnlyCollection<PendingArticle> articles,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var completed = 0;
        await Parallel.ForEachAsync(articles,
            new ParallelOptions { MaxDegreeOfParallelism = 1, CancellationToken = cancellationToken },
            async (article, token) =>
            {
                try
                {
                    var archive = await FetchArticleArchiveAsync(article, token);
                    _storage.SaveArticleArchive(archive);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _storage.MarkArchiveFailure(article.ArticleHash, article.Url, ex.Message);
                    Log("WARNING", $"archive failed title={article.Title} url={article.Url}: {ex.Message}");
                }
                finally
                {
                    var current = Interlocked.Increment(ref completed);
                    progress?.Report($"ARCHIVING // {current}/{articles.Count} FULL ARTICLES");
                }
            });
    }

    private async Task<ArticleArchive> FetchArticleArchiveAsync(PendingArticle article, CancellationToken cancellationToken)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        cancellationToken=timeout.Token;
        if (!Uri.TryCreate(article.Url, UriKind.Absolute, out var requestUri) ||
            requestUri.Scheme is not ("http" or "https"))
            throw new InvalidDataException("Article URL is not HTTP/HTTPS.");

        using var response = await _client.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var finalUri = response.RequestMessage?.RequestUri ?? requestUri;
        var body = await ReadLimitedAsync(await response.Content.ReadAsStreamAsync(cancellationToken),8*1024*1024,cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        var html = DecodeBody(body, response.Content.Headers.ContentType?.CharSet);
        var text = contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
            ? ExtractArticleText(html) : "";

        var images = new List<ArchivedImage>();
        if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            var candidates = ExtractImageCandidates(html, finalUri).Take(MaxImagesPerArticle).ToList();
            using var imageGate = new SemaphoreSlim(4);
            var imageTasks = candidates.Select(async (candidate, position) =>
            {
                await imageGate.WaitAsync(cancellationToken);
                try
                {
                    return await FetchArchivedImageAsync(candidate, position, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log("DEBUG", $"image archive skipped url={candidate.ResolvedUrl}: {ex.Message}");
                    return null;
                }
                finally { imageGate.Release(); }
            });
            var downloaded = await Task.WhenAll(imageTasks);
            images.AddRange(downloaded.Where(x => x is not null).Select(x => x!).OrderBy(x => x.Position));
        }

        var textBytes = Encoding.UTF8.GetBytes(text);
        return new ArticleArchive(article.ArticleHash, finalUri.ToString(), contentType,
            body.Length, textBytes.Length, Gzip(body), Gzip(textBytes), images);
    }

    private async Task<ArchivedImage?> FetchArchivedImageAsync(
        ImageCandidate candidate,
        int position,
        CancellationToken cancellationToken)
    {
        using var imageResponse = await _client.GetAsync(candidate.ResolvedUrl,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        imageResponse.EnsureSuccessStatusCode();
        var declaredSize = imageResponse.Content.Headers.ContentLength;
        if (declaredSize is > MaxImageBytes) return null;
        var imageBytes = await ReadLimitedAsync(
            await imageResponse.Content.ReadAsStreamAsync(cancellationToken), MaxImageBytes, cancellationToken);
        var imageType = imageResponse.Content.Headers.ContentType?.MediaType ?? GuessImageType(candidate.ResolvedUrl);
        if (!imageType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;
        return new ArchivedImage(position, candidate.SourceUrl, candidate.ResolvedUrl.ToString(),
            imageType, candidate.AltText, imageBytes.Length, Gzip(imageBytes));
    }

    private static IEnumerable<ImageCandidate> ExtractImageCandidates(string html, Uri baseUri)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match meta in MetaImageRegex().Matches(html))
        {
            var source = WebUtility.HtmlDecode(meta.Groups["url"].Value.Trim());
            if (ResolveImage(source, baseUri, seen) is { } resolved)
                yield return new ImageCandidate(source, resolved, "article preview");
        }

        foreach (Match tagMatch in ImageTagRegex().Matches(html))
        {
            var tag = tagMatch.Value;
            var source = Attribute(tag, "src") ?? Attribute(tag, "data-src") ?? Attribute(tag, "data-original");
            if (string.IsNullOrWhiteSpace(source))
            {
                var srcset = Attribute(tag, "srcset") ?? Attribute(tag, "data-srcset");
                source = srcset?.Split(',').LastOrDefault()?.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            }
            if (string.IsNullOrWhiteSpace(source)) continue;
            source = WebUtility.HtmlDecode(source);
            if (ResolveImage(source, baseUri, seen) is not { } resolved) continue;
            yield return new ImageCandidate(source, resolved, NormalizeText(Attribute(tag, "alt") ?? ""));
        }
    }

    private static Uri? ResolveImage(string source, Uri baseUri, HashSet<string> seen)
    {
        if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(baseUri, source, out var resolved) ||
            resolved.Scheme is not ("http" or "https") || !seen.Add(resolved.AbsoluteUri))
            return null;
        return resolved;
    }

    private static string? Attribute(string tag, string name)
    {
        var match = Regex.Match(tag,
            $@"\b{Regex.Escape(name)}\s*=\s*(?:[""'](?<value>.*?)[""']|(?<value>[^\s>]+))",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static string ExtractArticleText(string html)
    {
        var cleaned = NonContentBlockRegex().Replace(html, " ");
        return NormalizeText(cleaned);
    }

    private static string DecodeBody(byte[] body, string? charset)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(charset))
                return Encoding.GetEncoding(charset.Trim('"', '\'' )).GetString(body);
        }
        catch (ArgumentException) { }
        return Encoding.UTF8.GetString(body);
    }

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(data, 0, data.Length);
        return output.ToArray();
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream stream, int limit, CancellationToken cancellationToken)
    {
        await using (stream)
        using (var output = new MemoryStream())
        {
            var buffer = new byte[81920];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                if (output.Length + read > limit) throw new InvalidDataException("Image exceeds archive size limit.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            return output.ToArray();
        }
    }

    private static string GuessImageType(Uri uri)
    {
        var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        return extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".gif" => "image/gif",
            ".webp" => "image/webp", ".svg" => "image/svg+xml", ".avif" => "image/avif",
            _ => "application/octet-stream"
        };
    }

    private static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var decoded = WebUtility.HtmlDecode(value);
        var withoutTags = HtmlTagRegex().Replace(decoded, " ");
        return WhitespaceRegex().Replace(WebUtility.HtmlDecode(withoutTags), " ").Trim();
    }

    private static string ParsePublished(string value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed.ToUniversalTime().ToString("O") :
        string.IsNullOrWhiteSpace(value) ? "Unknown" : NormalizeText(value);

    private static string ArticleIdentifier(string feedName, string guid, string title, string url)
    {
        var source = string.IsNullOrWhiteSpace(guid)
            ? $"fallback\0{feedName}\0{url}\0{title}"
            : $"guid\0{feedName}\0{NormalizeText(guid)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    }

    private void Log(string level, string message)
    {
        if(level=="DEBUG")return;
        lock (_logLock)
        {
            try
            {
                if(File.Exists(_logPath)&&new FileInfo(_logPath).Length>10*1024*1024)
                    for(int i=3;i>=1;i--){var from=i==1?_logPath:_logPath+"."+(i-1);if(File.Exists(from))File.Move(from,_logPath+"."+i,true);}
                File.AppendAllText(_logPath,System.Text.Json.JsonSerializer.Serialize(new{timestamp=DateTimeOffset.UtcNow,level,message})+Environment.NewLine);
            }
            catch(IOException){ /* Non-audit diagnostics must not take down collection. */ }
        }
    }

    public void Dispose() => _client.Dispose();


    private sealed record ImageCandidate(string SourceUrl, Uri ResolvedUrl, string AltText);

    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"<meta\b[^>]*(?:property|name)\s*=\s*[""'](?:og:image(?::url)?|twitter:image(?::src)?)[""'][^>]*content\s*=\s*[""'](?<url>.*?)[""'][^>]*>|<meta\b[^>]*content\s*=\s*[""'](?<url>.*?)[""'][^>]*(?:property|name)\s*=\s*[""'](?:og:image(?::url)?|twitter:image(?::src)?)[""'][^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex MetaImageRegex();

    [GeneratedRegex(@"<img\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ImageTagRegex();

    [GeneratedRegex(@"<(?:script|style|noscript|svg|template|head|nav|footer|aside)\b[^>]*>.*?</(?:script|style|noscript|svg|template|head|nav|footer|aside)>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex NonContentBlockRegex();
}
