using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AllianceWatch;

internal sealed class AssessmentSettings
{
    public string Version { get; set; } = "AW-1.0";
    public double NormalizationScale { get; set; } = 60;
    public double ConvergenceBonus { get; set; } = 2;
    public double AlertThreshold { get; set; } = 60;
    public double ConfidenceAlertThreshold { get; set; } = 70;
    public ProtocolDefinition[] Protocols { get; set; } = ProtocolCatalog.Defaults;
    public ScenarioDefinition[] Scenarios { get; set; } =
    [
        new("russia-nato", "Russia / NATO", ["Russia", "NATO"], "Europe", "Europe"),
        new("taiwan", "Taiwan Strait / China / United States", ["Taiwan", "China"], "East Asia", "Asia"),
        new("korea", "Korean Peninsula", ["North Korea", "South Korea"], "Korea", "Asia"),
        new("middle-east", "Middle East interstate escalation", ["Iran", "Israel"], "Middle East", "Middle East"),
        new("india-pakistan", "India / Pakistan", ["India", "Pakistan"], "South Asia", "Asia"),
        // Regional lenses require two named regional actors, avoiding continent-wide
        // scoring from a single incidental mention in a global source.
        new("africa-regional", "Africa regional security", ["Egypt", "Ethiopia", "Sudan", "Somalia", "Nigeria", "South Africa", "Kenya"], "Africa", "Africa", 2),
        new("north-america-regional", "North America regional security", ["United States", "Canada", "Mexico"], "North America", "North America", 2),
        new("central-america-regional", "Central America regional security", ["Belize", "Guatemala", "Honduras", "El Salvador", "Nicaragua", "Costa Rica", "Panama"], "Central America", "Central America", 2),
        new("south-america-regional", "South America regional security", ["Brazil", "Argentina", "Colombia", "Venezuela", "Chile", "Peru"], "South America", "South America", 2)
    ];
    public Dictionary<string, string[]> ActorAliases { get; set; } = new()
    {
        ["United States"] = ["United States", "U.S.", "US military", "Pentagon"],
        ["NATO"] = ["NATO", "North Atlantic Treaty Organization"],
        ["Russia"] = ["Russia", "Russian", "Moscow"], ["China"] = ["China", "Chinese", "Beijing"],
        ["Taiwan"] = ["Taiwan", "Taipei"], ["North Korea"] = ["North Korea", "DPRK", "Pyongyang"],
        ["South Korea"] = ["South Korea", "Seoul"], ["Iran"] = ["Iran", "Iranian"],
        ["Israel"] = ["Israel", "Israeli"], ["India"] = ["India", "Indian"], ["Pakistan"] = ["Pakistan", "Pakistani"],
        ["Egypt"] = ["Egypt", "Egyptian", "Cairo"], ["Ethiopia"] = ["Ethiopia", "Ethiopian", "Addis Ababa"],
        ["Sudan"] = ["Sudan", "Sudanese", "Khartoum"], ["Somalia"] = ["Somalia", "Somali", "Mogadishu"],
        ["Nigeria"] = ["Nigeria", "Nigerian", "Abuja"], ["South Africa"] = ["South Africa", "South African", "Pretoria"], ["Kenya"] = ["Kenya", "Kenyan", "Nairobi"],
        ["Canada"] = ["Canada", "Canadian", "Ottawa"], ["Mexico"] = ["Mexico", "Mexican", "Mexico City"],
        ["Belize"] = ["Belize", "Belizean"], ["Guatemala"] = ["Guatemala", "Guatemalan"],
        ["Honduras"] = ["Honduras", "Honduran"], ["El Salvador"] = ["El Salvador", "Salvadoran"],
        ["Nicaragua"] = ["Nicaragua", "Nicaraguan"], ["Costa Rica"] = ["Costa Rica", "Costa Rican"],
        ["Panama"] = ["Panama", "Panamá", "Panamanian"],
        ["Brazil"] = ["Brazil", "Brazilian", "Brasilia"], ["Argentina"] = ["Argentina", "Argentine", "Buenos Aires"],
        ["Colombia"] = ["Colombia", "Colombian", "Bogota", "Bogotá"], ["Venezuela"] = ["Venezuela", "Venezuelan", "Caracas"],
        ["Chile"] = ["Chile", "Chilean", "Santiago"], ["Peru"] = ["Peru", "Peruvian", "Lima"]
    };
    public string[] NarrativeTerms { get; set; } = ["collective defense", "mutual defense", "national emergency", "wartime footing", "mobilization", "defense of allies", "all necessary measures", "strategic deterrence", "evacuate nationals", "military response", "joint command", "war economy", "territorial integrity", "red line"];
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Version) || !double.IsFinite(NormalizationScale) || NormalizationScale <= 0 || !double.IsFinite(ConvergenceBonus) || ConvergenceBonus < 0 || !double.IsFinite(AlertThreshold) || AlertThreshold is < 0 or > 100 || !double.IsFinite(ConfidenceAlertThreshold) || ConfidenceAlertThreshold is < 0 or > 100)
            throw new InvalidDataException("Invalid assessment normalization, version, bonus or threshold.");
        if (Protocols.Length != 30 || Protocols.Select(p => p.Id).Distinct().Count() != 30 || Protocols.Any(p => p.Id is < 1 or > 30 || !double.IsFinite(p.HalfLifeHours) || p.HalfLifeHours <= 0 || !double.IsFinite(p.Severity) || p.Severity is < 0 or > 10 || p.Patterns.Length == 0 || p.RequiredIndependentSources < 1))
            throw new InvalidDataException("Exactly 30 unique protocols (01–30), positive half-lives and severity 0–10 are required.");
        if (Scenarios.Select(s => s.Id).Distinct().Count() != Scenarios.Length || Scenarios.Any(s => string.IsNullOrWhiteSpace(s.Id) || s.Actors.Length == 0 || string.IsNullOrWhiteSpace(s.Theater) || s.MinimumMatchedActors is < 1 || s.MinimumMatchedActors > s.Actors.Length))
            throw new InvalidDataException("Scenario IDs must be unique and actor lists nonempty.");
        foreach (var p in Protocols) foreach (var pattern in p.Patterns.Concat(p.Exclusions)) _ = ProtocolCatalog.Pattern(pattern);
    }
}
internal sealed record ScenarioDefinition(string Id, string Name, string[] Actors, string Geography, string Theater = "Global", int MinimumMatchedActors = 1);
internal sealed record SourceRecord(string RecordId, string Title, string Summary, string Url, string Publisher, DateTimeOffset PublishedAt, DateTimeOffset FirstSeenAt, double Quality, string Origin, string Tier = "C", string Language = "und", bool OriginalReporting = false);
internal sealed record EvidenceEvent(string EventId, SourceRecord Source, string CanonicalUrl, string CanonicalHash, string ClusterId, string[] Actors, string[] Scenarios, string[] Geography, int[] Protocols, double Severity, double HalfLifeHours, bool Disputed, bool Retraction, string[] NarrativeTerms, string ParserVersion = "rules-1.0")
{
    public string? CorrectionOfEventId { get; init; }
    public string[] Notes { get; init; } = [];
    public string ActorPrimary => Actors.FirstOrDefault() ?? "Uncertain";
    public string[] ActorSecondary => Actors.Skip(1).ToArray();
    public string[] TargetActors { get; init; } = [];
    public string SourceCountry { get; init; } = "unknown";
    public DateTimeOffset DetectedAt => Source.FirstSeenAt;
    public DateTimeOffset LastSeenAt => Source.FirstSeenAt;
}
internal sealed record Contribution(string EventId, string Title, string[] RecordIds, int[] Protocols, string[] Scenarios, double Severity, double Corroboration, double Novelty, double SourceQuality, double Relevance, double Recency, double Contradiction, double NegativeEvidence, double Confidence, double RawScore, int IndependentSources);
internal sealed record ScenarioScore(string Id, double Risk, double Confidence, double RawScore, int Events, int IndependentSources, int[] Protocols, double Convergence);
internal sealed record ScoreChange(string EventId, string Reason, double Before, double After, double Delta);
internal sealed record Assessment(DateTimeOffset Timestamp, string Version, double Risk, double RawScore, double Confidence, string Momentum, double Delta, double Convergence, int Ladder, Contribution[] Contributions, ScenarioScore[] Scenarios, ScoreChange[] Changes, Dictionary<string, double?> Vector, string SettingsHash)
{
    public int ArticleCount { get; init; }
    public int EventCount { get; init; }
    public int ProtocolCount { get; init; }
}

internal sealed class AssessmentEngine
{
    private readonly AssessmentSettings _settings;
    private readonly (ProtocolDefinition Rule, Regex[] Patterns, Regex[] Exclusions)[] _protocols;
    private readonly (string Actor, Regex[] Patterns)[] _actors;
    public AssessmentEngine(AssessmentSettings settings)
    {
        settings.Validate(); _settings = settings;
        _protocols = settings.Protocols.Select(p => (p, p.Patterns.Select(ProtocolCatalog.Pattern).ToArray(), p.Exclusions.Select(ProtocolCatalog.Pattern).ToArray())).ToArray();
        _actors = settings.ActorAliases.Select(a => (a.Key, a.Value.Select(v => ProtocolCatalog.Pattern(Regex.Escape(v))).ToArray())).ToArray();
    }
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    public static string Normalize(string value) => Regex.Replace(value.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
    public static string CanonicalUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return "";
        var builder = new UriBuilder(uri) { Fragment = "" };
        builder.Query = string.Join("&", uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Where(x => !Regex.IsMatch(x, @"^(utm_\w+|fbclid|gclid)=", RegexOptions.IgnoreCase)).Order());
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }
    public EvidenceEvent Extract(SourceRecord source)
    {
        var text = source.Title + " " + source.Summary;
        var actors = _actors.Where(a => a.Patterns.Any(p => p.IsMatch(text))).Select(a => a.Actor).ToArray();
        var protocols = _protocols.Where(p => p.Patterns.Any(r => r.IsMatch(text)) && !p.Exclusions.Any(r => r.IsMatch(text)) && (p.Rule.ActorConstraints.Length == 0 || p.Rule.ActorConstraints.Intersect(actors).Any())).Select(p => p.Rule).ToArray();
        var scenarios = _settings.Scenarios.Where(s => s.Actors.Intersect(actors).Count() >= s.MinimumMatchedActors).ToArray();
        var geography = scenarios.Select(s => s.Geography).Distinct().ToArray();
        protocols = protocols.Where(p => p.GeographyConstraints.Length == 0 || p.GeographyConstraints.Intersect(geography).Any()).ToArray();
        // An event family is separate from its originating-report/source family.
        var cluster = Hash(Normalize(source.Title) + "|" + source.PublishedAt.UtcDateTime.ToString("yyyy-MM-dd"));
        var canonical = CanonicalUrl(source.Url);
        return new("AW-E-" + source.RecordId, source, canonical, Hash(Normalize(text)), cluster, actors, scenarios.Select(s => s.Id).DefaultIfEmpty("unassigned").ToArray(), geography, protocols.Select(p => p.Id).ToArray(), protocols.Select(p => p.Severity).DefaultIfEmpty().Max(), protocols.Select(p => p.HalfLifeHours).DefaultIfEmpty(24).Max(),
            Regex.IsMatch(text, @"\b(denied|denies|correction|retracted|disputed|unconfirmed)\b", RegexOptions.IgnoreCase),
            Regex.IsMatch(source.Title, @"\b(retraction|retracted)\b", RegexOptions.IgnoreCase),
            _settings.NarrativeTerms.Where(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)).ToArray());
    }

    public Assessment Evaluate(IEnumerable<EvidenceEvent> records, DateTimeOffset at, Assessment? previous = null, IReadOnlyList<Assessment>? history = null)
    {
        var eligible = records.Where(e => e.Source.FirstSeenAt <= at && e.Source.PublishedAt <= at).ToArray();
        var byRecord = eligible.ToDictionary(e=>e.Source.RecordId);
        var ruleById = _settings.Protocols.ToDictionary(p=>p.Id);
        var correctionsByEvent = eligible.Where(e=>e.CorrectionOfEventId!=null).ToLookup(e=>e.CorrectionOfEventId!);
        var companionIndex = eligible.Where(e=>e.Protocols.Intersect(new[]{4,12,15,17}).Any()).SelectMany(e=>e.Scenarios.Select(s=>(Key:s,Event:e))).GroupBy(x=>x.Key).ToDictionary(g=>g.Key,g=>g.Select(x=>x.Event).OrderBy(e=>e.Source.PublishedAt).ToArray());
        var noveltyIndex = eligible.SelectMany(e=>e.Actors.SelectMany(a=>e.Protocols.Select(p=>(Key:(a,p),Event:e)))).GroupBy(x=>x.Key).ToDictionary(g=>g.Key,g=>g.Select(x=>x.Event).OrderBy(e=>e.Source.PublishedAt).ToArray());
        var components = new List<Contribution>();
        foreach (var group in eligible.Where(e => e.Protocols.Length > 0).GroupBy(e => e.ClusterId))
        {
            var entries = group.OrderBy(e => e.Source.FirstSeenAt).ThenBy(e => e.EventId).ToArray();
            var first = entries[0];
            var originals = entries.GroupBy(e => e.Source.Summary.Length>80 ? Hash(Normalize(e.Source.Summary)) : e.CanonicalHash).Select(g => g.OrderByDescending(e => e.Source.OriginalReporting).First()).ToArray();
            var verifiedOrigins = originals.Where(e => e.Source.OriginalReporting).Select(e => e.Source.Origin).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var origins = Math.Max(1, verifiedOrigins);
            var protocols = entries.SelectMany(e => e.Protocols).Distinct().Order().ToArray();
            var age = Math.Max(0, (at - first.Source.PublishedAt).TotalHours);
            var halfLife = protocols.Select(id => ruleById[id].HalfLifeHours).Max();
            var severity = protocols.Select(id => ruleById[id].Severity).Max();
            var recency = Math.Pow(.5, age / halfLife);
            if(recency < 1e-6) recency=0;
            var quality = entries.GroupBy(e => e.Source.Origin).Average(g => g.Max(e => e.Source.Quality));
            var corroboration = Math.Min(1, .5 + (origins - 1) * .25);
            var corrections = entries.SelectMany(e=>correctionsByEvent[e.EventId]).ToArray();
            var contradiction = entries.Concat(corrections).Any(e => e.Retraction) ? 1 : entries.Concat(corrections).Any(e => e.Disputed) || ConflictingFigures(entries) ? .4 : 0;
            var companion = first.Scenarios.Any(s=>HasOtherInWindow(companionIndex.GetValueOrDefault(s),first.ClusterId,first.Source.PublishedAt.AddHours(-72),first.Source.PublishedAt.AddHours(72),true));
            var negative = protocols.Intersect(new[] {7, 8, 23}).Any() && !companion ? .15 : 0;
            var novelty = first.Actors.Any(a=>protocols.Any(p=>HasOtherInWindow(noveltyIndex.GetValueOrDefault((a,p)),first.ClusterId,first.Source.PublishedAt.AddDays(-30),first.Source.PublishedAt,false))) ? .7 : 1;
            var relevance = first.Actors.Length == 0 ? .5 : 1;
            var confidence = 100 * quality * corroboration * (1 - contradiction) * (1 - negative);
            var correctionOnly = entries.All(e => e.CorrectionOfEventId != null);
            var raw = correctionOnly ? 0 : severity * corroboration * novelty * relevance * recency * (1 - contradiction) * (1 - negative);
            components.Add(new(first.ClusterId, first.Source.Title, entries.Select(e => e.Source.RecordId).ToArray(), protocols, first.Scenarios, severity, corroboration, novelty, quality, relevance, recency, contradiction, negative, confidence, raw, origins));
        }
        var scenarios = components.SelectMany(c => c.Scenarios).Distinct().Select(id =>
        {
            var members = components.Where(c => c.Scenarios.Contains(id)).ToArray();
            var active = members.Where(c => c.Recency > .25 && c.Contradiction < 1).ToArray();
            var recentIds = eligible.Where(e => e.Source.PublishedAt >= at.AddHours(-72) && e.Scenarios.Contains(id)).Select(e => e.ClusterId).ToHashSet();
            var recent = active.Where(c => recentIds.Contains(c.EventId) && c.IndependentSources >= c.Protocols.Max(id=>ruleById[id].RequiredIndependentSources)).ToArray();
            // Convergence requires independent reports, shared actors AND geography, within 72h.
            var recentById=recent.ToDictionary(c=>c.EventId);
            var linked = eligible.Where(e => recentById.ContainsKey(e.ClusterId)).SelectMany(e => e.Actors.SelectMany(a => e.Geography.Select(g => (Key: a + "|" + g, e.ClusterId)))).GroupBy(x => x.Key);
            var diversity = linked.Select(g=>g.Select(x=>x.ClusterId).Distinct().SelectMany(key=>recentById[key].Protocols).Distinct().Count()).DefaultIfEmpty().Max();
            var bonus = id == "unassigned" ? 0 : Math.Max(0, diversity - 2) * _settings.ConvergenceBonus;
            var raw = members.Sum(c => c.RawScore / c.Scenarios.Length) + bonus;
            return new ScenarioScore(id, NormalizeScore(raw), active.Length == 0 ? 0 : active.Average(c => c.Confidence), raw, members.Length, active.SelectMany(c => c.RecordIds).Select(r => byRecord[r].Source.Origin).Distinct().Count(), active.SelectMany(c => c.Protocols).Distinct().Order().ToArray(), bonus);
        }).ToArray();
        var total = scenarios.Sum(s => s.RawScore);
        var risk = NormalizeScore(total);
        var contributions = components.ToArray();
        var bonusTotal = scenarios.Sum(s => s.Convergence);
        var changes = ExplainChanges(contributions, risk, total, bonusTotal, previous);
        var vector = new Dictionary<string, double?>();
        foreach (var (label, hours) in new[] { ("6H",6), ("24H",24), ("72H",72), ("7D",168), ("30D",720) })
        {
            var prior = history?.Where(h => h.Timestamp <= at.AddHours(-hours)).OrderByDescending(h => h.Timestamp).FirstOrDefault();
            vector[label] = prior == null ? null : risk - prior.Risk;
        }
        var momentum = vector["6H"] is not double delta ? "INSUFFICIENT HISTORY" : delta > 2 ? "ACCELERATING" : delta < -2 ? "DECELERATING" : "STABLE";
        var supported = contributions.Where(c => c.IndependentSources >= 2 && c.Confidence >= 60 && c.Recency >= .5).SelectMany(c => c.Protocols).ToHashSet();
        // Higher war classifications need verified human adjudication; keywords cannot establish them.
        var ladder = supported.Contains(29) ? 6 : supported.Contains(7) ? 4 : supported.Overlaps([8, 15, 17]) ? 3 : supported.Overlaps([9, 23]) ? 2 : supported.Contains(22) ? 1 : 0;
        var activeComponents = contributions.Where(c => c.Recency > .25).ToArray();
        return new(at, _settings.Version, risk, total, activeComponents.Length == 0 ? 0 : activeComponents.Average(c => c.Confidence), momentum, risk - (previous?.Risk ?? 0), bonusTotal, ladder, contributions, scenarios, changes, vector, Hash(System.Text.Json.JsonSerializer.Serialize(_settings)))
        {ArticleCount=eligible.Length,EventCount=contributions.Length,ProtocolCount=contributions.SelectMany(c=>c.Protocols).Distinct().Count()};
    }
    // Recompute the bridge for any two stored assessments, not just adjacent scans.
    // The dashboard rounds Risk to an integer, so a useful explanation may span
    // several nearly identical intervening snapshots.
    internal static ScoreChange[] ExplainChanges(Assessment current, Assessment previous) =>
        ExplainChanges(current.Contributions, current.Risk, current.RawScore, current.Convergence, previous);

    private static ScoreChange[] ExplainChanges(Contribution[] contributions, double risk, double rawScore, double convergence, Assessment? previous)
    {
        var old = previous?.Contributions.ToDictionary(c => c.EventId) ?? new();
        var current = contributions.ToDictionary(c => c.EventId);
        var changes = old.Keys.Union(current.Keys).Select(id =>
        {
            var before = old.GetValueOrDefault(id); var after = current.GetValueOrDefault(id);
            var b = before?.RawScore ?? 0; var a = after?.RawScore ?? 0;
            return new ScoreChange(id, (after ?? before)!.Title + (before == null ? " [new evidence]" : after?.Recency < before.Recency && a < b ? " [decay / evidence revision]" : " [evidence revision]"), b, a, a - b);
        }).Where(c => Math.Abs(c.Delta) > 1e-9).ToList();
        changes.Add(new("convergence", "Independent protocol convergence", previous?.Convergence ?? 0, convergence, convergence - (previous?.Convergence ?? 0)));
        changes.Add(new("normalization", "0–100 normalization adjustment", (previous?.Risk ?? 0) - (previous?.RawScore ?? 0), risk - rawScore, (risk - rawScore) - ((previous?.Risk ?? 0) - (previous?.RawScore ?? 0))));
        return changes.ToArray();
    }
    private double NormalizeScore(double raw) => 100 * (1 - Math.Exp(-Math.Max(0, raw) / _settings.NormalizationScale));
    private static readonly Regex FigurePattern=new(@"\b(?<n>\d[\d,]*)\s+(?<unit>troops|soldiers|casualties|aircraft|tanks)\b",RegexOptions.IgnoreCase|RegexOptions.Compiled,TimeSpan.FromMilliseconds(100));
    private static bool ConflictingFigures(EvidenceEvent[] entries)
    {
        if(entries.Length<2)return false;
        return entries.SelectMany(e=>FigurePattern.Matches(e.Source.Title+" "+e.Source.Summary).Cast<Match>().Select(m=>(Unit:m.Groups["unit"].Value.ToLowerInvariant(),Value:m.Groups["n"].Value.Replace(",",""),Record:e.Source.RecordId)))
            .GroupBy(x=>x.Unit).Any(g=>g.Select(x=>x.Record).Distinct().Count()>1&&g.Select(x=>x.Value).Distinct().Count()>1);
    }
    private static bool HasOtherInWindow(EvidenceEvent[]? items,string cluster,DateTimeOffset from,DateTimeOffset to,bool inclusiveEnd)
    {
        if(items==null)return false;
        int low=0,high=items.Length;
        while(low<high){int mid=low+(high-low)/2;if(items[mid].Source.PublishedAt<from)low=mid+1;else high=mid;}
        for(int i=low;i<items.Length;i++)
        {
            var item=items[i];if(inclusiveEnd?item.Source.PublishedAt>to:item.Source.PublishedAt>=to)break;
            if(item.ClusterId!=cluster)return true;
        }
        return false;
    }
}
