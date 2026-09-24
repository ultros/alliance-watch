using System.Text.Json;

namespace AllianceWatch;

internal sealed record CoverageRow(string Theater,int ConfiguredFeeds,int FreshFeeds,int FailedFeeds,int EventFamilies,int VerifiedOrigins,DateTimeOffset? LatestReport,string Status);
internal sealed record ScanChangeRow(string Kind,string Title,double RawDelta,int IndependentSources,DateTimeOffset? Detected,string ClusterId);
internal sealed record ClaimFamily(string ClusterId,string Title,int Reports,int OriginalOrigins,int Challenges,DateTimeOffset Latest,string Actors);
internal sealed record QualityWarning(string Level,string Area,string Finding,string Action);
internal sealed record WhatIfResult(Assessment Baseline,Assessment Variant,int ChangedSources,int ChangedProtocol);

internal static class OperationsAnalytics
{
    public static CoverageRow[] Coverage(AppConfig config,IReadOnlyList<FeedHealth> health,IReadOnlyList<EvidenceEvent> recent,DateTimeOffset at)
    {
        var byUrl=health.ToDictionary(h=>h.Url,StringComparer.OrdinalIgnoreCase);
        return TheaterCatalog.All.Select(theater=>
        {
            var feeds=config.Feeds.Where(f=>f.Enabled && f.Group.StartsWith(theater.Name,StringComparison.OrdinalIgnoreCase)).ToArray();
            var names=feeds.Select(f=>f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var reports=recent.Where(e=>names.Contains(e.Source.Publisher) && e.Source.FirstSeenAt>=at.AddHours(-24)).ToArray();
            var fresh=feeds.Count(f=>byUrl.TryGetValue(f.Url,out var h) && h.LastSuccess is {} success &&
                at-success<=TimeSpan.FromMinutes(Math.Max(60,f.IntervalMinutes*2)));
            var failed=feeds.Count(f=>byUrl.TryGetValue(f.Url,out var h) && h.Failures>0 &&
                (h.LastSuccess is null || at-h.LastSuccess>TimeSpan.FromMinutes(Math.Max(60,f.IntervalMinutes*2))));
            var origins=reports.Where(e=>e.Source.OriginalReporting).Select(e=>e.Source.Origin).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var status=feeds.Length==0?"NO FEEDS":fresh==0?"COLLECTION GAP":fresh<feeds.Length?"PARTIAL":reports.Length==0?"NO RECENT REPORTING":origins==0?"NO VERIFIED ORIGIN":
                origins==1?"LIMITED INDEPENDENCE":"MONITORED";
            return new CoverageRow(theater.Name,feeds.Length,fresh,failed,reports.Select(e=>e.ClusterId).Distinct().Count(),origins,
                reports.Length==0?null:reports.Max(e=>e.Source.FirstSeenAt),status);
        }).ToArray();
    }

    public static ScanChangeRow[] SinceLastAssessment(Assessment latest,Assessment previous,IReadOnlyList<EvidenceEvent> recent)
    {
        var byCluster=recent.GroupBy(e=>e.ClusterId).ToDictionary(g=>g.Key,g=>g.ToArray());
        var claimKeys=ClaimKeys(recent);
        var previousIds=previous.Contributions.Select(c=>c.EventId).ToHashSet();
        var rows=AssessmentEngine.ExplainChanges(latest,previous)
            .Where(c=>c.EventId is not ("normalization" or "convergence") && (Math.Abs(c.Delta)>=.001 || !previousIds.Contains(c.EventId)))
            .Select(c=>new ScanChangeRow(previousIds.Contains(c.EventId)?"WEIGHT CHANGE":"NEW EVENT",c.Reason,c.Delta,
                latest.Contributions.FirstOrDefault(x=>x.EventId==c.EventId)?.IndependentSources??0,
                byCluster.GetValueOrDefault(c.EventId)?.Max(e=>e.Source.FirstSeenAt),c.EventId)).ToList();
        foreach(var e in recent.Where(e=>e.Source.FirstSeenAt>previous.Timestamp && e.Source.FirstSeenAt<=latest.Timestamp &&
            (e.Retraction || e.Disputed || e.CorrectionOfEventId!=null)).GroupBy(e=>e.ClusterId).Select(g=>g.First()))
            rows.Add(new(e.Retraction?"RETRACTION":e.CorrectionOfEventId!=null?"CORRECTION":"DISPUTED",e.Source.Title,0,0,e.Source.FirstSeenAt,claimKeys.GetValueOrDefault(e.EventId)??e.ClusterId));
        return rows.OrderByDescending(r=>r.Kind is "RETRACTION" or "CORRECTION" ? 1 : 0)
            .ThenByDescending(r=>Math.Abs(r.RawDelta)).ThenByDescending(r=>r.Detected).Take(500).ToArray();
    }

    public static ClaimFamily[] Claims(IReadOnlyList<EvidenceEvent> records)
    {
        return Claims(ClaimGroups(records));
    }
    public static ClaimFamily[] Claims(IReadOnlyDictionary<string,EvidenceEvent[]> groups)
    {
        return groups.Select(g=>
        {
            var items=g.Value;
            return new ClaimFamily(g.Key,items.OrderBy(e=>e.Source.FirstSeenAt).First().Source.Title,items.Length,
                items.Where(e=>e.Source.OriginalReporting).Select(e=>e.Source.Origin).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                items.Count(e=>e.Disputed || e.Retraction || e.CorrectionOfEventId!=null),
                items.Max(e=>e.Source.FirstSeenAt),string.Join(" / ",items.SelectMany(e=>e.Actors).Distinct().Take(4)));
        }).OrderByDescending(g=>g.Challenges>0).ThenByDescending(g=>g.Latest).ToArray();
    }

    public static Dictionary<string,EvidenceEvent[]> ClaimGroups(IReadOnlyList<EvidenceEvent> records)
    {
        var keys=ClaimKeys(records);
        return records.Where(e=>e.Protocols.Length>0 || e.Disputed || e.Retraction || e.CorrectionOfEventId!=null)
            .GroupBy(e=>keys[e.EventId]).ToDictionary(g=>g.Key,g=>g.OrderBy(e=>e.Source.FirstSeenAt).ToArray());
    }

    private static Dictionary<string,string> ClaimKeys(IReadOnlyList<EvidenceEvent> records)
    {
        var byEvent=records.ToDictionary(e=>e.EventId);
        var result=new Dictionary<string,string>();
        foreach(var e in records)
        {
            var parent=e;var visited=new HashSet<string>{e.EventId};
            while(parent.CorrectionOfEventId is {} target && byEvent.TryGetValue(target,out var prior) && visited.Add(target))parent=prior;
            result[e.EventId]=parent.ClusterId;
        }
        return result;
    }

    public static QualityWarning[] Quality(AppConfig config,IReadOnlyList<FeedHealth> health,IReadOnlyList<EvidenceEvent> recent,
        IReadOnlyList<CoverageRow> coverage,IReadOnlyList<Assessment> history,long futureDated,DateTimeOffset at)
    {
        var warnings=new List<QualityWarning>();
        foreach(var row in coverage.Where(c=>c.Status is "COLLECTION GAP" or "PARTIAL" or "NO FEEDS"))
            warnings.Add(new(row.Status=="COLLECTION GAP"?"HIGH":"MEDIUM",row.Theater,
                $"{row.FreshFeeds}/{row.ConfiguredFeeds} enabled feeds recently successful; {row.FailedFeeds} failing or overdue.",
                "Inspect feed health; do not interpret absent reports as safety."));
        foreach(var row in coverage.Where(c=>c.EventFamilies>0 && c.Status is ("NO VERIFIED ORIGIN" or "LIMITED INDEPENDENCE")))
            warnings.Add(new("MEDIUM",row.Theater,$"{row.EventFamilies} recent event families but only {row.VerifiedOrigins} configured original-reporting origins.",
                "Seek independent reporting; copied stories and one origin do not establish corroboration."));
        if(futureDated>0)warnings.Add(new("HIGH","TIMESTAMPS",$"{futureDated} normalized records are dated more than one hour in the future.",
            "Check provider publication timestamps and timezone mappings."));
        foreach(var h in health.Where(h=>h.Items>=20 && h.Duplicates>=h.Items*.85).Take(10))
            warnings.Add(new("LOW",h.Name,$"{h.Duplicates}/{h.Items} fetched items duplicated an existing record.",
                "Confirm feed pagination and syndication; duplicates do not increase independent support."));
        if(history.Count>=2 && Math.Abs(history[0].Risk-history[1].Risk)>=10)
            warnings.Add(new("HIGH","SCORE",$"Index moved {history[0].Risk-history[1].Risk:+0.0;-0.0;0.0} points between adjacent assessments.",
                "Open Since Last Scan and verify the leading evidence and source provenance."));
        var recentOriginal=recent.Where(e=>e.Source.FirstSeenAt>=at.AddHours(-24) && e.Source.OriginalReporting)
            .GroupBy(e=>(e.ClusterId,Origin:e.Source.Origin.ToUpperInvariant())).Select(g=>g.First()).ToArray();
        var dominant=recentOriginal.GroupBy(e=>e.Source.Origin,StringComparer.OrdinalIgnoreCase).OrderByDescending(g=>g.Count()).FirstOrDefault();
        if(recentOriginal.Length>=10 && dominant!=null && dominant.Count()>=recentOriginal.Length*.75)
            warnings.Add(new("MEDIUM","SOURCE DIVERSITY",$"{dominant.Key} supplies {dominant.Count()}/{recentOriginal.Length} original-reporting records in the last 24 hours.",
                "Check whether other independent origins are available."));
        return warnings.OrderBy(w=>w.Level=="HIGH"?0:w.Level=="MEDIUM"?1:2).ThenBy(w=>w.Area).ToArray();
    }

    public static WhatIfResult CompareWhatIf(AssessmentSettings settings,IReadOnlyList<EvidenceEvent> evidence,DateTimeOffset at,
        double normalization,double convergence,int protocol,double severity,string origin,double sourceQuality)
    {
        if(normalization<=0 || convergence<0 || protocol is < 0 or > 30 || severity is < 0 or > 10 || sourceQuality is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(normalization),"Simulation settings are outside valid ranges.");
        var baseline=new AssessmentEngine(settings).Evaluate(evidence,at);
        var variant=JsonSerializer.Deserialize<AssessmentSettings>(JsonSerializer.Serialize(settings))!;
        variant.NormalizationScale=normalization;variant.ConvergenceBonus=convergence;
        if(protocol>0)variant.Protocols=variant.Protocols.Select(p=>p.Id==protocol?p with{Severity=severity}:p).ToArray();
        variant.Validate();
        var changed=string.IsNullOrWhiteSpace(origin)?0:evidence.Count(e=>e.Source.Origin.Equals(origin,StringComparison.OrdinalIgnoreCase));
        var modified=changed==0?evidence:evidence.Select(e=>e.Source.Origin.Equals(origin,StringComparison.OrdinalIgnoreCase)
            ?e with{Source=e.Source with{Quality=sourceQuality}}:e).ToArray();
        var simulated=new AssessmentEngine(variant).Evaluate(modified,at);
        return new(baseline,simulated,changed,protocol);
    }
}
