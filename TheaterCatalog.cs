namespace AllianceWatch;

internal sealed record TheaterDefinition(string Id, string Name, int Priority, bool IsPrimaryFocus);

/// <summary>
/// Shared regional monitoring hierarchy. It ranks the display only; it does not
/// change the evidence score, evidence rules, or alert thresholds.
/// </summary>
internal static class TheaterCatalog
{
    public static readonly TheaterDefinition[] All =
    [
        new("asia", "Asia", 0, true),
        new("europe", "Europe", 1, true),
        new("middle-east", "Middle East", 2, true),
        new("africa", "Africa", 10, false),
        new("north-america", "North America", 11, false),
        new("central-america", "Central America", 12, false),
        new("south-america", "South America", 13, false)
    ];

    public static TheaterDefinition Find(string name) =>
        All.FirstOrDefault(theater => theater.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? new TheaterDefinition(name.ToLowerInvariant().Replace(' ', '-'), name, 100, false);
}
