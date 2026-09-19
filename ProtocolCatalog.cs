using System.Text.RegularExpressions;

namespace AllianceWatch;

internal sealed record ProtocolDefinition(int Id, string Name, double Severity, double HalfLifeHours, string[] Patterns)
{
    public string Version { get; init; } = "1.0";
    public string Description { get; init; } = "Public report mentioning this indicator; requires contextual verification.";
    public double MinimumSeverity { get; init; } = 0;
    public double MaximumSeverity { get; init; } = 10;
    public int RequiredIndependentSources { get; init; } = 2;
    public string SourceRequirements { get; init; } = "Attributed public report; official statements remain attributed claims.";
    public string[] ActorConstraints { get; init; } = [];
    public string[] GeographyConstraints { get; init; } = [];
    public string[] Exclusions { get; init; } = ["hypothetical", "book review", "war game scenario", "history of", "during the cold war"];
}

internal static class ProtocolCatalog
{
    public static readonly ProtocolDefinition[] Defaults =
    [
        new(1,"MUTUAL DEFENSE / TREATY",5,2160,["mutual defen[cs]e", "collective defen[cs]e", "defen[cs]e treaty"]),
        new(2,"INTEGRATED COMMAND",5,720,["integrated (military )?command", "combined command", "joint command", "joint headquarters"]),
        new(3,"JOINT OPERATIONS PLANNING",4,168,["joint operational planning", "combined operational planning", "joint contingency planning"]),
        new(4,"WARTIME LOGISTICS",5,168,["wartime logistics", "joint logistics", "pre.?positioned (weapons|ammunition)"]),
        new(5,"RECIPROCAL BASE ACCESS",4,720,["reciprocal (base|military) access", "base access agreement"]),
        new(6,"STRATEGIC COORDINATION",2,48,["strategic coordination", "deepening military cooperation"]),
        new(7,"RESERVE MOBILIZATION",7,336,["reserve mobili[sz]ation", "mobili[sz](ed|ing) reservists", "reserve activation"]),
        new(8,"FORCE DISPERSAL",6,72,["force dispersal", "dispers(ed|al of) (forces|aircraft)"]),
        new(9,"STRATEGIC FORCE POSTURE",7,168,["strategic force posture", "strategic deterrence", "nuclear alert"]),
        new(10,"AIRSPACE / NOTAM RESTRICTIONS",5,24,["airspace (restriction|closure|closed)", "NOTAM"]),
        new(11,"MARITIME EXCLUSION ZONES",5,48,["maritime exclusion zone", "naval blockade"]),
        new(12,"EMBASSY EVACUATION / DRAW-DOWN",6,168,["embassy evacuation", "embassy (draw.?down|personnel reduction)", "evacuate nationals"]),
        new(13,"CIVIL DEFENSE ACTIVATION",5,72,["civil defen[cs]e activation", "bomb shelters opened"]),
        new(14,"EMERGENCY POWERS / MARTIAL LAW",6,168,["martial law", "emergency powers", "national emergency"]),
        new(15,"MASS LOGISTICS MOVEMENT",6,96,["mass logistics movement", "military rail convoy", "troop transport surge"]),
        new(16,"MEDICAL / BLOOD SUPPLY MOBILIZATION",5,72,["blood supply mobili[sz]ation", "field hospitals deployed"]),
        new(17,"FUEL / MUNITIONS SURGE",6,96,["munitions surge", "fuel stockpile", "ammunition stockpile"]),
        new(18,"COMMAND RELOCATION",6,72,["command relocation", "headquarters relocated"]),
        new(19,"NATIONAL CYBER ALERT",4,24,["national cyber alert", "cyber emergency"]),
        new(20,"CRITICAL INFRASTRUCTURE DISRUPTION",5,48,["critical infrastructure disruption", "power grid attack"]),
        new(21,"SATELLITE / SPACE ASSET DISRUPTION",6,72,["satellite disruption", "anti.satellite attack"]),
        new(22,"DIPLOMATIC BREAKDOWN",4,72,["diplomatic breakdown", "sever(ed)? diplomatic ties", "expelled diplomats"]),
        new(23,"ULTIMATUM / DEADLINE LANGUAGE",4,12,["ultimatum", "all necessary measures", "red line"]),
        new(24,"NUCLEAR SAFEGUARD / POSTURE EVENT",8,168,["nuclear safeguard", "nuclear posture", "nuclear readiness"]),
        new(25,"BORDER CLOSURE",4,48,["border closure", "border closed"]),
        new(26,"CAPITAL / EXPORT CONTROLS",3,336,["capital controls", "export controls"]),
        new(27,"SHIPPING / INSURANCE DISLOCATION",3,72,["war risk premium", "shipping insurance surge"]),
        new(28,"STRATEGIC INDUSTRY MOBILIZATION",6,720,["war economy", "wartime footing", "defen[cs]e production surge"]),
        new(29,"DIRECT STATE-ON-STATE KINETIC EVENT",9,168,["interstate attack", "state.on.state attack", "cross.border missile strike"]),
        new(30,"MULTI-THEATER SYNCHRONIZATION",8,72,["multi.theater (synchronization|operations)"])
    ];

    public static Regex Pattern(string value) => new($@"(?<!\w)(?:{value})(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
}
