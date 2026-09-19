using System.Text.RegularExpressions;

namespace AllianceWatch;

internal static partial class IndicatorEngine
{
    // A strong phrase is not automatically a critical event. RED now requires
    // corroboration from actors, operational language, or multiple signal groups.
    public const int YellowThreshold = 5;
    public const int OrangeThreshold = 10;
    public const int RedThreshold = 15;

    private static readonly IReadOnlyDictionary<string, int> CategoryPoints =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["red"] = 7, ["orange"] = 5, ["yellow"] = 3, ["green"] = 1
        };

    private static readonly IReadOnlyDictionary<string, string[]> PhraseGroups =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["red"] =
            [
                "mutual defense", "mutual defence", "collective defense", "collective defence",
                "mutual defense treaty", "mutual defence treaty", "military alliance", "defense treaty",
                "defence treaty", "joint defense obligations", "joint defence obligations",
                "security guarantee", "mutual security guarantee", "collective response", "attack on one",
                "attack on either", "attack against either", "military assistance obligation",
                "treaty obligation", "common military command", "integrated military command",
                "combined military command"
            ],
            ["orange"] =
            [
                "joint operational planning", "combined operational planning", "integrated command",
                "combined command", "joint headquarters", "permanent joint headquarters",
                "wartime planning", "joint contingency planning", "coordinated military response",
                "simultaneous operations", "joint strategic command", "integrated military operations"
            ],
            ["yellow"] =
            [
                "joint logistics", "military logistics agreement", "reciprocal base access",
                "reciprocal military access", "base access agreement", "pre-positioned weapons",
                "prepositioned weapons", "pre-positioned ammunition", "prepositioned ammunition",
                "ammunition stockpile", "fuel stockpile", "wartime logistics", "repair agreement",
                "military rail agreement", "military port access", "interoperability",
                "strategic coordination", "deepening military cooperation"
            ],
            ["green"] =
            [
                "non-alliance", "non alliance", "not an alliance", "non-confrontation",
                "not directed against third parties", "not targeting third parties",
                "strategic partnership", "comprehensive strategic partnership"
            ]
        };

    private static readonly string[] SuppressionTerms =
    [
        "historical", "history of", "during the cold war", "analyst argues", "opinion",
        "commentary", "book review", "hypothetical", "simulation", "war game scenario"
    ];

    private static readonly IReadOnlyDictionary<string, string[]> ActorPatterns =
        new Dictionary<string, string[]>
        {
            ["China"] = ["china", "chinese", "beijing", "pla"],
            ["Russia"] = ["russia", "russian", "moscow", "kremlin"],
            ["Taiwan"] = ["taiwan", "taipei"],
            ["United States"] = ["united states", @"u\.?\s*s\.?", "us military", "pentagon", "washington"],
            ["NATO"] = ["nato"],
            ["North Korea"] = ["north korea", "dprk", "pyongyang"],
            ["Iran"] = ["iran", "iranian", "tehran"],
            ["BRICS"] = ["brics"]
        };

    public static Dictionary<string, List<string>> DetectIndicators(string text)
    {
        var found = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in PhraseGroups)
        {
            var matches = group.Value
                .Where(phrase => BoundedPhrase(phrase).IsMatch(text))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(x => x.Length)
                .ThenBy(x => x)
                .ToList();
            if (matches.Count > 0)
                found[group.Key] = matches;
        }
        return found;
    }

    public static List<string> FlattenIndicators(Dictionary<string, List<string>> indicators) =>
        PhraseGroups.Keys.SelectMany(group => indicators.GetValueOrDefault(group) ?? []).ToList();

    public static List<string> DetectActors(string text)
    {
        var actors = new List<string>();
        foreach (var actor in ActorPatterns)
        {
            if (actor.Value.Any(pattern =>
                Regex.IsMatch(text, $@"(?<!\w)(?:{pattern})(?!\w)", RegexOptions.IgnoreCase)))
                actors.Add(actor.Key);
        }
        return actors;
    }

    public static int CalculateScore(
        Dictionary<string, List<string>> indicators,
        IReadOnlyCollection<string> actors,
        int sourceWeight,
        string text)
    {
        if (indicators.Count == 0)
            return 0;

        var score = indicators.Keys.Max(category => CategoryPoints[category]);
        score += ActorCombinationScore(actors);
        score += (int)Math.Round(Math.Clamp(sourceWeight, 0, 10) * .3, MidpointRounding.ToEven);

        var red = indicators.ContainsKey("red");
        var orange = indicators.ContainsKey("orange");
        var yellow = indicators.ContainsKey("yellow");
        var phrases = indicators.Values.SelectMany(x => x).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (red && orange) score += 3;
        if (yellow && orange) score += 2;
        if (red && actors.Contains("Taiwan")) score += 2;
        if (red && actors.Contains("NATO")) score += 2;

        // Concrete implementation language matters more than commentary that merely
        // contains a phrase such as "military alliance".
        string[] actionTerms =
        [
            "signed", "ratified", "entered into force", "activated", "invoked",
            "mobilized", "mobilised", "deployed", "blockade", "live-fire",
            "live fire", "wartime footing", "combat deployment"
        ];
        if (red && actionTerms.Any(term => BoundedPhrase(term).IsMatch(text)))
            score += 3;

        string[] jointCommandTerms =
        [
            "integrated command", "combined command", "joint headquarters", "permanent joint headquarters",
            "joint strategic command", "common military command", "integrated military command",
            "combined military command"
        ];
        if (new[] { "China", "Russia", "North Korea" }.All(actors.Contains) &&
            jointCommandTerms.Any(phrases.Contains))
            score += 3;

        if (SuppressionTerms.Any(term => BoundedPhrase(term).IsMatch(text)))
            score -= 3;

        return Math.Max(0, score);
    }

    public static string ClassifySeverity(int score) => score switch
    {
        >= RedThreshold => "RED",
        >= OrangeThreshold => "ORANGE",
        >= YellowThreshold => "YELLOW",
        _ => "LOG ONLY"
    };

    private static int ActorCombinationScore(IReadOnlyCollection<string> actors)
    {
        (string[] Required, int Points)[] rules =
        [
            (["China", "Russia", "Taiwan", "United States"], 5),
            (["China", "Russia", "Taiwan"], 4),
            (["China", "Russia", "NATO"], 4),
            (["China", "Russia", "North Korea"], 4),
            (["China", "Russia", "United States"], 3),
            (["China", "Russia"], 2),
            (["China", "Taiwan"], 2),
            (["Russia", "NATO"], 2)
        ];
        return rules.Where(rule => rule.Required.All(actors.Contains))
            .Select(rule => rule.Points)
            .DefaultIfEmpty(0)
            .Max();
    }

    private static Regex BoundedPhrase(string phrase)
    {
        var escaped = Regex.Escape(phrase).Replace(@"\ ", @"\s+");
        return new Regex($@"(?<!\w){escaped}(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
