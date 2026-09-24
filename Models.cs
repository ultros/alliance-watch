using System.Text.Json.Serialization;

namespace AllianceWatch;

internal sealed class AppConfig
{
    [JsonPropertyName("archive_enabled")]
    public bool ArchiveEnabled { get; init; } = true;
    [JsonPropertyName("archive_concurrency")]
    public int ArchiveConcurrency { get; init; } = 6;
    [JsonPropertyName("assessment")]
    public AssessmentSettings Assessment { get; init; } = new();
    [JsonPropertyName("poll_minutes")]
    public double PollMinutes { get; init; } = 10;

    [JsonPropertyName("max_popups_per_cycle")]
    public int MaxPopupsPerCycle { get; init; } = 5;

    [JsonPropertyName("feeds")]
    public List<FeedConfig> Feeds { get; init; } = [];

    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("config.json was not found.", path);

        var config = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(
            File.ReadAllText(path),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("config.json is empty or invalid.");

        if (config.Feeds.Count == 0)
            throw new InvalidDataException("config.json must contain at least one feed.");
        config.Assessment.Validate();
        if (!double.IsFinite(config.PollMinutes) || config.PollMinutes <= 0 || config.PollMinutes > 1440)
            throw new InvalidDataException("poll_minutes must be greater than zero.");
        if (config.ArchiveConcurrency is < 1 or > 12)
            throw new InvalidDataException("archive_concurrency must be between 1 and 12.");

        foreach (var feed in config.Feeds)
        {
            _ = SourceAdapters.For(feed.Adapter);
            if (string.IsNullOrWhiteSpace(feed.Name) ||
                !Uri.TryCreate(feed.Url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidDataException($"Invalid feed configuration: '{feed.Name}'.");
            if (feed.Weight is < 0 or > 10 || feed.IntervalMinutes is < 1 or > 1440 || !double.IsFinite(feed.SourceQuality) || feed.SourceQuality is < 0 or > 1 || feed.SourceClass is not ("A" or "B" or "C" or "D"))
                throw new InvalidDataException($"Invalid weight, quality or interval for {feed.Name}.");
        }
        if (config.Feeds.Select(f => f.Url).Distinct().Count() != config.Feeds.Count)
            throw new InvalidDataException("Feed URLs must be unique.");

        return config;
    }
}

internal sealed class FeedConfig
{
    [JsonPropertyName("original_reporting")]
    public bool OriginalReporting { get; init; }
    [JsonPropertyName("adapter")]
    public string Adapter { get; init; } = "rss";
    [JsonPropertyName("items_path")]
    public string ItemsPath { get; init; } = "";
    [JsonPropertyName("field_map")]
    public Dictionary<string,string> FieldMap { get; init; } = new();
    [JsonPropertyName("bearer_token_env")]
    public string BearerTokenEnvironmentVariable { get; init; } = "";
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;
    [JsonPropertyName("interval_minutes")]
    public int IntervalMinutes { get; init; } = 10;
    [JsonPropertyName("group")]
    public string Group { get; init; } = "Journalism";
    [JsonPropertyName("source_class")]
    public string SourceClass { get; init; } = "C";
    [JsonPropertyName("source_quality")]
    public double SourceQuality { get; init; } = .5;
    [JsonPropertyName("origin")]
    public string Origin { get; init; } = "";
    [JsonPropertyName("language")]
    public string Language { get; init; } = "und";
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("url")]
    public string Url { get; init; } = "";

    [JsonPropertyName("weight")]
    public int Weight { get; set; } = 3;
}

internal sealed record FeedState(string Name, int EntryCount, bool Online, string Detail);

internal sealed class MatchResult
{
    public string ArticleHash { get; init; } = "";
    public string FeedName { get; init; } = "";
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public string Published { get; init; } = "";
    public string Severity { get; init; } = "";
    public int Score { get; init; }
    public List<string> Phrases { get; init; } = [];
    public List<string> Actors { get; init; } = [];
    public int SourceWeight { get; init; }
    public long MatchId { get; set; }
}

internal sealed record DashboardEvent(
    long Id,
    string Severity,
    int Score,
    string Title,
    string FeedName,
    string Published,
    string Actors,
    string Indicators,
    int SourceWeight,
    string Url,
    DateTime DetectedAt,
    bool Alerted,
    bool IsRuleAlert = false,
    string ArticleHash = "");

internal sealed record DashboardStats(
    int TotalArticles,
    int TotalMatches,
    int RedCount,
    int OrangeCount,
    int YellowCount,
    int HighestScore,
    IReadOnlyList<int> RecentScores);

internal sealed record ScanResult(
    IReadOnlyList<MatchResult> Alerts,
    IReadOnlyList<MatchResult> LogOnly,
    IReadOnlyList<FeedState> FeedStates,
    int NewArticles);

internal sealed record PendingArticle(string ArticleHash, string Url, string Title);

internal sealed record ArchivedImage(
    int Position,
    string SourceUrl,
    string ResolvedUrl,
    string MimeType,
    string AltText,
    int OriginalBytes,
    byte[] CompressedData);

internal sealed record ArticleArchive(
    string ArticleHash,
    string FinalUrl,
    string ContentType,
    int HtmlBytes,
    int TextBytes,
    byte[] CompressedHtml,
    byte[] CompressedText,
    IReadOnlyList<ArchivedImage> Images);
