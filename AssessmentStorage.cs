using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace AllianceWatch;

internal sealed record FeedHealth(string Url, string Name, string ETag = "", string LastModified = "", DateTimeOffset? LastSuccess = null, DateTimeOffset? NextFetch = null, int Status = 0, long LatencyMs = 0, int Failures = 0, int Items = 0, int Duplicates = 0, int Signals = 0, string Error = "");
internal sealed record AssessmentAlert(long Id, string Key, string Rule, DateTimeOffset Timestamp, double Confidence, string[] EventIds, string State, DateTimeOffset? SnoozedUntil);
internal sealed record IgnoredNewsItem(string RecordId, string Title, string FeedName, string Url, string Published, string IgnoredAt, string Reason);

internal sealed partial class Storage
{
    private readonly object _assessmentLock = new();
    public void MigrateAssessment()
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations(version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS aw_events(record_id TEXT PRIMARY KEY, event_id TEXT NOT NULL, cluster_id TEXT NOT NULL,
                canonical_url TEXT NOT NULL, canonical_hash TEXT NOT NULL, published_at TEXT NOT NULL, first_seen_at TEXT NOT NULL, payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS aw_events_cluster ON aw_events(cluster_id);
            CREATE INDEX IF NOT EXISTS aw_events_url ON aw_events(canonical_url);
            CREATE INDEX IF NOT EXISTS aw_events_hash ON aw_events(canonical_hash);
            CREATE INDEX IF NOT EXISTS aw_events_published ON aw_events(published_at);
            CREATE INDEX IF NOT EXISTS aw_events_hash_published ON aw_events(canonical_hash,published_at,first_seen_at);
            CREATE INDEX IF NOT EXISTS aw_events_url_published ON aw_events(canonical_url,published_at,first_seen_at);
            CREATE INDEX IF NOT EXISTS aw_events_seen ON aw_events(first_seen_at);
            CREATE TRIGGER IF NOT EXISTS aw_events_no_update BEFORE UPDATE ON aw_events BEGIN SELECT RAISE(ABORT,'Evidence records are append-only'); END;
            CREATE TRIGGER IF NOT EXISTS aw_events_no_delete BEFORE DELETE ON aw_events BEGIN SELECT RAISE(ABORT,'Evidence records are append-only'); END;
            CREATE TABLE IF NOT EXISTS aw_assessments(id INTEGER PRIMARY KEY AUTOINCREMENT, timestamp TEXT NOT NULL, version TEXT NOT NULL, payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS aw_assessments_time ON aw_assessments(timestamp);
            CREATE TABLE IF NOT EXISTS aw_assessment_runs(assessment_id INTEGER PRIMARY KEY, timestamp TEXT NOT NULL, trigger_kind TEXT NOT NULL,
                FOREIGN KEY(assessment_id) REFERENCES aw_assessments(id));
            CREATE INDEX IF NOT EXISTS aw_assessment_runs_kind ON aw_assessment_runs(trigger_kind,assessment_id DESC);
            CREATE TRIGGER IF NOT EXISTS aw_assessment_runs_no_update BEFORE UPDATE ON aw_assessment_runs BEGIN SELECT RAISE(ABORT,'Assessment run history is append-only'); END;
            CREATE TRIGGER IF NOT EXISTS aw_assessment_runs_no_delete BEFORE DELETE ON aw_assessment_runs BEGIN SELECT RAISE(ABORT,'Assessment run history is append-only'); END;
            CREATE TABLE IF NOT EXISTS aw_score_history(id INTEGER PRIMARY KEY, timestamp TEXT NOT NULL, payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS aw_score_history_time ON aw_score_history(timestamp);
            CREATE TRIGGER IF NOT EXISTS aw_assessment_summary AFTER INSERT ON aw_assessments BEGIN
                INSERT INTO aw_score_history VALUES(new.id,new.timestamp,json_set(new.payload,'$.Contributions',json('[]'),'$.Changes',json('[]')));
            END;
            INSERT OR IGNORE INTO aw_score_history SELECT id,timestamp,json_set(payload,'$.Contributions',json('[]'),'$.Changes',json('[]')) FROM aw_assessments WHERE id>(SELECT COALESCE(MAX(id),0) FROM aw_score_history);
            CREATE TRIGGER IF NOT EXISTS aw_assessment_no_update BEFORE UPDATE ON aw_assessments BEGIN SELECT RAISE(ABORT,'Assessments are append-only'); END;
            CREATE TRIGGER IF NOT EXISTS aw_assessment_no_delete BEFORE DELETE ON aw_assessments BEGIN SELECT RAISE(ABORT,'Assessments are append-only'); END;
            CREATE TABLE IF NOT EXISTS aw_rule_versions(hash TEXT PRIMARY KEY, payload TEXT NOT NULL);
            CREATE TRIGGER IF NOT EXISTS aw_rules_no_update BEFORE UPDATE ON aw_rule_versions BEGIN SELECT RAISE(ABORT,'Rule versions are append-only'); END;
            CREATE TRIGGER IF NOT EXISTS aw_rules_no_delete BEFORE DELETE ON aw_rule_versions BEGIN SELECT RAISE(ABORT,'Rule versions are append-only'); END;
            CREATE TABLE IF NOT EXISTS aw_feed_health(url TEXT PRIMARY KEY, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS aw_feed_fetches(id INTEGER PRIMARY KEY, timestamp TEXT NOT NULL, url TEXT NOT NULL, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS aw_alerts(id INTEGER PRIMARY KEY, alert_key TEXT NOT NULL UNIQUE, rule TEXT NOT NULL, timestamp TEXT NOT NULL,
                confidence REAL NOT NULL, event_ids TEXT NOT NULL, state TEXT NOT NULL DEFAULT 'UNREAD', snoozed_until TEXT);
            CREATE INDEX IF NOT EXISTS aw_alert_state ON aw_alerts(state,snoozed_until);
            CREATE TABLE IF NOT EXISTS aw_evaluations(id INTEGER PRIMARY KEY, timestamp TEXT NOT NULL, event_id TEXT NOT NULL, outcome TEXT NOT NULL, notes TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS aw_score_exclusions(record_id TEXT PRIMARY KEY, ignored_at TEXT NOT NULL, reason TEXT NOT NULL,
                active INTEGER NOT NULL DEFAULT 1, updated_at TEXT NOT NULL, FOREIGN KEY(record_id) REFERENCES articles(article_hash));
            CREATE INDEX IF NOT EXISTS aw_score_exclusions_active ON aw_score_exclusions(active,ignored_at DESC);
            CREATE TABLE IF NOT EXISTS aw_score_exclusion_log(id INTEGER PRIMARY KEY, record_id TEXT NOT NULL, timestamp TEXT NOT NULL,
                action TEXT NOT NULL, reason TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS aw_score_exclusion_log_record ON aw_score_exclusion_log(record_id,id DESC);
            CREATE TRIGGER IF NOT EXISTS aw_score_exclusion_log_no_update BEFORE UPDATE ON aw_score_exclusion_log BEGIN SELECT RAISE(ABORT,'Score exclusion history is append-only'); END;
            CREATE TRIGGER IF NOT EXISTS aw_score_exclusion_log_no_delete BEFORE DELETE ON aw_score_exclusion_log BEGIN SELECT RAISE(ABORT,'Score exclusion history is append-only'); END;
            CREATE TABLE IF NOT EXISTS aw_watch_items(id INTEGER PRIMARY KEY, kind TEXT NOT NULL, value TEXT NOT NULL, min_confidence REAL NOT NULL DEFAULT 50,
                enabled INTEGER NOT NULL DEFAULT 1, UNIQUE(kind,value));
            CREATE TABLE IF NOT EXISTS aw_review_log(id INTEGER PRIMARY KEY, timestamp TEXT NOT NULL, cluster_id TEXT NOT NULL,
                outcome TEXT NOT NULL, notes TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS aw_review_cluster ON aw_review_log(cluster_id,id DESC);
            CREATE TRIGGER IF NOT EXISTS aw_review_no_update BEFORE UPDATE ON aw_review_log BEGIN SELECT RAISE(ABORT,'Review history is append-only'); END;
            CREATE TRIGGER IF NOT EXISTS aw_review_no_delete BEFORE DELETE ON aw_review_log BEGIN SELECT RAISE(ABORT,'Review history is append-only'); END;
            CREATE TABLE IF NOT EXISTS actor_aliases(actor TEXT NOT NULL, alias TEXT NOT NULL, PRIMARY KEY(actor,alias));
            CREATE INDEX IF NOT EXISTS idx_matches_detected ON matches(detected_at DESC);
            INSERT OR IGNORE INTO schema_migrations VALUES(1,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            INSERT OR IGNORE INTO schema_migrations VALUES(2,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            INSERT OR IGNORE INTO schema_migrations VALUES(3,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            INSERT OR IGNORE INTO schema_migrations VALUES(4,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            INSERT OR IGNORE INTO schema_migrations VALUES(5,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            INSERT OR IGNORE INTO schema_migrations VALUES(6,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            """;
        cmd.ExecuteNonQuery(); tx.Commit();
    }

    public void SaveRules(AssessmentSettings settings)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        var payload = JsonSerializer.Serialize(settings);
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR IGNORE INTO aw_rule_versions VALUES($hash,$payload)";
        cmd.Parameters.AddWithValue("$hash", AssessmentEngine.Hash(payload)); cmd.Parameters.AddWithValue("$payload", payload); cmd.ExecuteNonQuery();
        foreach (var actor in settings.ActorAliases) foreach (var alias in actor.Value)
        {
            using var a = db.CreateCommand(); a.Transaction = tx; a.CommandText = "INSERT OR IGNORE INTO actor_aliases VALUES($a,$b)";
            a.Parameters.AddWithValue("$a", actor.Key); a.Parameters.AddWithValue("$b", alias); a.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // Migration is resumable. Original article rows and first-seen times are never rewritten.
    public int NormalizePending(AssessmentEngine engine, AppConfig config, int limit = 1000)
    {
        var pending = new List<SourceRecord>();
        using var db = Open();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT a.article_hash,a.title,a.summary,a.url,a.feed_name,a.published,a.first_seen
                FROM articles a LEFT JOIN aw_events e ON a.article_hash=e.record_id
                WHERE e.record_id IS NULL ORDER BY a.id LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$limit", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var feed = config.Feeds.FirstOrDefault(f => f.Name == r.GetString(4));
                var seen = DateTimeOffset.TryParse(r.GetString(6), out var s) ? s : DateTimeOffset.UtcNow;
                var published = DateTimeOffset.TryParse(r.GetString(5), out var p) ? p : seen;
                var origin = feed?.Origin;
                var text = r.GetString(1) + " " + r.GetString(2);
                foreach (var wire in new[] {"Reuters", "Associated Press", "Agence France-Presse", "TASS"})
                    if (text.Contains(wire, StringComparison.OrdinalIgnoreCase)) { origin = wire; break; }
                if (string.IsNullOrWhiteSpace(origin)) origin = Uri.TryCreate(r.GetString(3), UriKind.Absolute, out var u) ? u.Host : r.GetString(4);
                pending.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),published,seen,feed?.SourceQuality ?? .5,origin,feed?.SourceClass ?? "C",feed?.Language ?? "und",feed?.OriginalReporting ?? false));
            }
        }
        using var tx = db.BeginTransaction();
        foreach (var source in pending)
        {
            var e = engine.Extract(source);
            using (var duplicate = db.CreateCommand())
            {
                duplicate.Transaction = tx;
                duplicate.CommandText = """
                    SELECT cluster_id FROM aw_events
                    WHERE canonical_hash=$hash AND published_at >= $from AND published_at <= $to
                    UNION ALL
                    SELECT cluster_id FROM aw_events
                    WHERE canonical_url=$url AND $url<>'' AND published_at >= $from AND published_at <= $to
                    LIMIT 1
                    """;
                duplicate.Parameters.AddWithValue("$hash", e.CanonicalHash); duplicate.Parameters.AddWithValue("$url", e.CanonicalUrl);
                duplicate.Parameters.AddWithValue("$from", source.PublishedAt.AddDays(-3).ToUniversalTime().ToString("O")); duplicate.Parameters.AddWithValue("$to", source.PublishedAt.AddDays(3).ToUniversalTime().ToString("O"));
                if (duplicate.ExecuteScalar() is string cluster) e = e with { ClusterId = cluster };
            }
            // Bounded candidate window; never compare against the entire archive.
            if (e.Protocols.Length > 0)
            {
                using var candidates = db.CreateCommand(); candidates.Transaction = tx;
                candidates.CommandText = "SELECT payload FROM aw_events WHERE published_at >= $from AND published_at <= $to ORDER BY published_at DESC LIMIT 200";
                candidates.Parameters.AddWithValue("$from", source.PublishedAt.AddHours(-48).ToUniversalTime().ToString("O"));
                candidates.Parameters.AddWithValue("$to", source.PublishedAt.AddHours(48).ToUniversalTime().ToString("O"));
                using var reader = candidates.ExecuteReader();
                while(reader.Read())
                {
                    var candidate = JsonSerializer.Deserialize<EvidenceEvent>(reader.GetString(0))!;
                    if(!candidate.Protocols.Intersect(e.Protocols).Any() || !candidate.Actors.Intersect(e.Actors).Any()) continue;
                    var similarity = TextSimilarity(source.Title,candidate.Source.Title);
                    var bodyCopy=source.Summary.Length>100 && candidate.Source.Summary.Length>100 && TextSimilarity(source.Summary,candidate.Source.Summary)>=.9;
                    if((similarity >= .82 || bodyCopy) && !e.Disputed && !e.Retraction) { e=e with { ClusterId=candidate.ClusterId }; break; }
                    if((e.Disputed || e.Retraction) && candidate.Source.FirstSeenAt<=source.FirstSeenAt && candidate.Source.PublishedAt<=source.PublishedAt && similarity>=.6)
                    { e=e with {CorrectionOfEventId=candidate.EventId,Notes=["Candidate correction link inferred from actor/protocol/title overlap; verify source claim."]}; break; }
                }
            }
            using var insert = db.CreateCommand(); insert.Transaction = tx;
            insert.CommandText = "INSERT OR IGNORE INTO aw_events VALUES($r,$e,$c,$u,$h,$p,$s,$j)";
            foreach (var (key,value) in new[] {("$r", source.RecordId),("$e",e.EventId),("$c",e.ClusterId),("$u",e.CanonicalUrl),("$h",e.CanonicalHash),("$p",source.PublishedAt.ToUniversalTime().ToString("O")),("$s",source.FirstSeenAt.ToUniversalTime().ToString("O")),("$j",JsonSerializer.Serialize(e))}) insert.Parameters.AddWithValue(key,value);
            insert.ExecuteNonQuery();
        }
        tx.Commit(); return pending.Count;
    }
    internal static double TextSimilarity(string a,string b)
    {
        var x=AssessmentEngine.Normalize(a).Split(' ',StringSplitOptions.RemoveEmptyEntries).ToHashSet();var y=AssessmentEngine.Normalize(b).Split(' ',StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        return x.Count==0||y.Count==0?0:(double)x.Intersect(y).Count()/x.Union(y).Count();
    }
    public EvidenceEvent[] LoadEvidence(DateTimeOffset? until = null, int limit = 100000, bool requireComplete = false, bool forScoring = false)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT e.payload FROM aw_events e WHERE e.first_seen_at <= $until AND e.published_at <= $until " +
            (forScoring ? "AND NOT EXISTS (SELECT 1 FROM aw_score_exclusions x WHERE x.record_id=e.record_id AND x.active=1) " : "") +
            "ORDER BY e.first_seen_at DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$until", (until ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString("O")); cmd.Parameters.AddWithValue("$limit", requireComplete ? limit+1 : limit);
        using var r = cmd.ExecuteReader(); var items = new List<EvidenceEvent>();
        while (r.Read()) items.Add(JsonSerializer.Deserialize<EvidenceEvent>(r.GetString(0))!);
        if(requireComplete && items.Count>limit)throw new InvalidOperationException($"Assessment capacity exceeded ({limit:N0} records). No partial score was published; archive is preserved.");
        return items.ToArray();
    }
    public bool IsNewsIgnored(string recordId)
    {
        using var db = OpenReadOnly(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM aw_score_exclusions WHERE record_id=$id AND active=1";
        cmd.Parameters.AddWithValue("$id", recordId);
        return cmd.ExecuteScalar() is not null;
    }
    public HashSet<string> IgnoredRecordIds()
    {
        using var db = OpenReadOnly(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT record_id FROM aw_score_exclusions WHERE active=1";
        using var reader = cmd.ExecuteReader(); var ids = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }
    public bool SetNewsIgnored(string recordId, bool ignored, string reason = "")
    {
        if (string.IsNullOrWhiteSpace(recordId)) throw new ArgumentException("An article ID is required.", nameof(recordId));
        reason = reason.Trim();
        if (reason.Length > 1000) reason = reason[..1000];
        lock (_assessmentLock)
        {
            using var db = Open(); using var tx = db.BeginTransaction();
            using var article = db.CreateCommand(); article.Transaction = tx;
            article.CommandText = "SELECT 1 FROM articles WHERE article_hash=$id";
            article.Parameters.AddWithValue("$id", recordId);
            if (article.ExecuteScalar() is null) throw new ArgumentException("The selected article was not found.", nameof(recordId));
            using var state = db.CreateCommand(); state.Transaction = tx;
            state.CommandText = "SELECT active FROM aw_score_exclusions WHERE record_id=$id";
            state.Parameters.AddWithValue("$id", recordId);
            var previous = state.ExecuteScalar();
            if (ignored == (previous is not null && Convert.ToInt32(previous) == 1)) return false;
            var now = DateTimeOffset.UtcNow.ToString("O");
            using var change = db.CreateCommand(); change.Transaction = tx;
            change.CommandText = ignored
                ? "INSERT INTO aw_score_exclusions(record_id,ignored_at,reason,active,updated_at) VALUES($id,$now,$reason,1,$now) " +
                  "ON CONFLICT(record_id) DO UPDATE SET ignored_at=$now,reason=$reason,active=1,updated_at=$now"
                : "UPDATE aw_score_exclusions SET active=0,updated_at=$now WHERE record_id=$id AND active=1";
            change.Parameters.AddWithValue("$id", recordId);
            change.Parameters.AddWithValue("$now", now);
            change.Parameters.AddWithValue("$reason", reason);
            change.ExecuteNonQuery();
            using var log = db.CreateCommand(); log.Transaction = tx;
            log.CommandText = "INSERT INTO aw_score_exclusion_log(record_id,timestamp,action,reason) VALUES($id,$now,$action,$reason)";
            log.Parameters.AddWithValue("$id", recordId);
            log.Parameters.AddWithValue("$now", now);
            log.Parameters.AddWithValue("$action", ignored ? "IGNORE" : "RESTORE");
            log.Parameters.AddWithValue("$reason", reason);
            log.ExecuteNonQuery();
            tx.Commit();
            return true;
        }
    }
    public int IgnoredNewsCount(string search = "")
    {
        using var db = OpenReadOnly(); using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM aw_score_exclusions x JOIN articles a ON a.article_hash=x.record_id
            WHERE x.active=1 AND ($search='' OR instr(lower(a.title),lower($search))>0 OR
                instr(lower(a.feed_name),lower($search))>0 OR instr(lower(x.reason),lower($search))>0 OR
                instr(lower(x.record_id),lower($search))>0)
            """;
        cmd.Parameters.AddWithValue("$search", search.Trim());
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }
    public IgnoredNewsItem[] IgnoredNews(int limit = 500, int offset = 0, string search = "")
    {
        using var db = OpenReadOnly(); using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT x.record_id,a.title,a.feed_name,a.url,a.published,x.ignored_at,x.reason
            FROM aw_score_exclusions x JOIN articles a ON a.article_hash=x.record_id
            WHERE x.active=1 AND ($search='' OR instr(lower(a.title),lower($search))>0 OR
                instr(lower(a.feed_name),lower($search))>0 OR instr(lower(x.reason),lower($search))>0 OR
                instr(lower(x.record_id),lower($search))>0)
            ORDER BY x.ignored_at DESC LIMIT $limit OFFSET $offset
            """;
        cmd.Parameters.AddWithValue("$search", search.Trim());
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
        cmd.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        using var reader = cmd.ExecuteReader(); var items = new List<IgnoredNewsItem>();
        while (reader.Read()) items.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),
            reader.GetString(4),reader.GetString(5),reader.GetString(6)));
        return items.ToArray();
    }
    public Assessment[] AssessmentHistory(int limit = 2000)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT payload FROM aw_assessments ORDER BY id DESC LIMIT $limit"; cmd.Parameters.AddWithValue("$limit",limit);
        using var r = cmd.ExecuteReader(); var result = new List<Assessment>();
        while (r.Read()) result.Add(JsonSerializer.Deserialize<Assessment>(r.GetString(0))!);
        return result.ToArray();
    }
    public Assessment[] ScoreHistory(int limit = 60000)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT payload FROM aw_score_history ORDER BY id DESC LIMIT $limit";cmd.Parameters.AddWithValue("$limit",limit);
        using var r=cmd.ExecuteReader();var rows=new List<Assessment>();while(r.Read())rows.Add(JsonSerializer.Deserialize<Assessment>(r.GetString(0))!);return rows.ToArray();
    }
    public DateTimeOffset? LatestAssessmentTimestamp()
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT timestamp FROM aw_assessments ORDER BY id DESC LIMIT 1";
        return cmd.ExecuteScalar() is string timestamp ? DateTimeOffset.Parse(timestamp) : null;
    }
    public Assessment? AssessmentAt(DateTimeOffset at)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT payload FROM aw_assessments WHERE timestamp=$t ORDER BY id DESC LIMIT 1";cmd.Parameters.AddWithValue("$t",at.ToString("O"));
        return cmd.ExecuteScalar() is string payload ? JsonSerializer.Deserialize<Assessment>(payload):null;
    }
    public DateTimeOffset? PreviousCompletedScan(DateTimeOffset latest)
    {
        using var db=OpenReadOnly();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT timestamp FROM aw_assessment_runs WHERE trigger_kind='SCAN' AND timestamp<$latest ORDER BY assessment_id DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$latest",latest.ToUniversalTime().ToString("O"));
        return cmd.ExecuteScalar() is string timestamp?DateTimeOffset.Parse(timestamp):null;
    }
    public Assessment RefreshAssessment(AssessmentEngine engine, AppConfig config,string triggerKind="MANUAL")
    {
        triggerKind=triggerKind.ToUpperInvariant();
        if(triggerKind is not ("MANUAL" or "STARTUP" or "SCAN" or "IMPORT"))throw new ArgumentException("Unsupported assessment trigger.");
        lock(_assessmentLock) return RefreshAssessmentCore(engine,config,triggerKind);
    }
    private Assessment RefreshAssessmentCore(AssessmentEngine engine, AppConfig config,string triggerKind)
    {
        while (NormalizePending(engine, config) == 1000) { }
        var history = ScoreHistory();
        var previousAssessment=AssessmentHistory(1).FirstOrDefault();
        var evidence = LoadEvidence(requireComplete:true,forScoring:true);
        var result = engine.Evaluate(evidence, DateTimeOffset.UtcNow, previousAssessment, history);
        var watchItems = WatchItems().Where(item => item.Enabled).ToArray();
        using var db = Open(); using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO aw_assessments(timestamp,version,payload) VALUES($t,$v,$p)";
        cmd.Parameters.AddWithValue("$t", result.Timestamp.ToString("O")); cmd.Parameters.AddWithValue("$v",result.Version); cmd.Parameters.AddWithValue("$p",JsonSerializer.Serialize(result)); cmd.ExecuteNonQuery();
        using(var run=db.CreateCommand())
        {
            run.Transaction=tx;run.CommandText="INSERT INTO aw_assessment_runs(assessment_id,timestamp,trigger_kind) VALUES(last_insert_rowid(),$t,$kind)";
            run.Parameters.AddWithValue("$t",result.Timestamp.ToString("O"));run.Parameters.AddWithValue("$kind",triggerKind);run.ExecuteNonQuery();
        }
        foreach (var scenario in result.Scenarios)
        {
            var previous = history.FirstOrDefault()?.Scenarios.FirstOrDefault(s => s.Id == scenario.Id);
            var reason = scenario.Risk >= config.Assessment.AlertThreshold && (previous?.Risk ?? 0) < config.Assessment.AlertThreshold ? "Scenario threshold crossed" :
                scenario.Convergence > 0 && (previous?.Convergence ?? 0) == 0 ? "Independent protocol convergence" : "";
            if (reason.Length == 0) continue;
            InsertAlert(db, tx, scenario.Id + "|" + reason + "|" + result.Timestamp.ToString("yyyy-MM-dd"), reason + ": " + scenario.Id, result, result.Contributions.Where(c => c.Scenarios.Contains(scenario.Id)).Select(c => c.EventId).ToArray());
        }
        foreach (var change in result.Changes.Where(c => result.Contributions.Any(e => e.EventId == c.EventId && e.Contradiction > 0) && Math.Abs(c.Delta) > .5))
            InsertAlert(db, tx, "contradiction|" + change.EventId, "Contradictory wording changed evidence weight", result, [change.EventId]);
        var last=history.FirstOrDefault();
        var day=result.Timestamp.ToString("yyyy-MM-dd");
        var allIds=result.Contributions.Where(c=>c.RawScore>0).Select(c=>c.EventId).ToArray();
        if(last!=null && result.Ladder>last.Ladder) InsertAlert(db,tx,"ladder|"+result.Ladder+"|"+day,"Descriptive ladder state increased",result,allIds);
        if(last!=null && result.Confidence>=config.Assessment.ConfidenceAlertThreshold && last.Confidence<config.Assessment.ConfidenceAlertThreshold)InsertAlert(db,tx,"confidence|"+day,"Evidence confidence threshold crossed",result,allIds);
        if(result.Vector["6H"]>10 && last?.Vector.GetValueOrDefault("6H") is not >10)InsertAlert(db,tx,"acceleration|"+day,"Index increased more than 10 points in six hours",result,allIds);
        if(result.Scenarios.Count(s=>s.Convergence>0)>=2 && (last?.Scenarios.Count(s=>s.Convergence>0)??0)<2)InsertAlert(db,tx,"multi-theater|"+day,"Independent convergence in multiple scenarios",result,allIds);
        foreach(var critical in result.Contributions.Where(c=>c.Protocols.Intersect(new[]{9,24,29}).Any()&&c.IndependentSources>=2&&c.Confidence>=60&&c.Recency>=.5))
            InsertAlert(db,tx,"critical|"+critical.EventId,"Independently corroborated strategic protocol",result,[critical.EventId]);
        if(watchItems.Length>0)
        {
            var previousContributions=previousAssessment?.Contributions.ToDictionary(c=>c.EventId) ?? new Dictionary<string,Contribution>();
            var actorsByCluster=watchItems.Any(w=>w.Kind=="ACTOR")
                ?evidence.GroupBy(e=>e.ClusterId).ToDictionary(g=>g.Key,g=>g.SelectMany(e=>e.Actors).ToHashSet(StringComparer.OrdinalIgnoreCase))
                :new Dictionary<string,HashSet<string>>();
            var theaterByScenario=config.Assessment.Scenarios.ToDictionary(s=>s.Id,s=>s.Theater);
            foreach(var item in watchItems)
            foreach(var contribution in result.Contributions.Where(c=>c.RawScore>=1 && c.Recency>.25 && c.Confidence>=item.MinConfidence))
            {
                var prior=previousContributions.GetValueOrDefault(contribution.EventId);
                if(prior!=null && contribution.IndependentSources<=prior.IndependentSources && contribution.RawScore-prior.RawScore<.5)continue;
                var match=item.Kind switch
                {
                    "THEATER"=>contribution.Scenarios.Any(s=>theaterByScenario.GetValueOrDefault(s)?.Equals(item.Value,StringComparison.OrdinalIgnoreCase)==true),
                    "ACTOR"=>actorsByCluster.GetValueOrDefault(contribution.EventId)?.Contains(item.Value)==true,
                    "PROTOCOL"=>int.TryParse(item.Value,out var id)&&contribution.Protocols.Contains(id),
                    _=>false
                };
                if(match)InsertAlert(db,tx,$"watch|{item.Id}|{contribution.EventId}|{contribution.IndependentSources}|{(int)Math.Floor(contribution.RawScore*2)}",
                    $"Watchlist {item.Kind.ToLowerInvariant()} {item.Value}: {contribution.Title}",result,[contribution.EventId]);
            }
        }
        tx.Commit(); return result;
    }
    private static void InsertAlert(SqliteConnection db, SqliteTransaction tx, string key, string rule, Assessment a, string[] ids)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR IGNORE INTO aw_alerts(alert_key,rule,timestamp,confidence,event_ids) VALUES($k,$r,$t,$c,$e)";
        cmd.Parameters.AddWithValue("$k",key); cmd.Parameters.AddWithValue("$r",rule); cmd.Parameters.AddWithValue("$t",a.Timestamp.ToString("O")); cmd.Parameters.AddWithValue("$c",a.Confidence); cmd.Parameters.AddWithValue("$e",JsonSerializer.Serialize(ids)); cmd.ExecuteNonQuery();
    }
    public AssessmentAlert[] AssessmentAlerts()
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT id,alert_key,rule,timestamp,confidence,event_ids,state,snoozed_until FROM aw_alerts ORDER BY id DESC LIMIT 500";
        using var r = cmd.ExecuteReader(); var result = new List<AssessmentAlert>();
        while (r.Read()) result.Add(new(r.GetInt64(0),r.GetString(1),r.GetString(2),DateTimeOffset.Parse(r.GetString(3)),r.GetDouble(4),JsonSerializer.Deserialize<string[]>(r.GetString(5))!,r.GetString(6),r.IsDBNull(7)?null:DateTimeOffset.Parse(r.GetString(7))));
        return result.ToArray();
    }
    public AssessmentAlert[] ActionableAssessmentAlerts(int limit = 20)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id,alert_key,rule,timestamp,confidence,event_ids,state,snoozed_until
            FROM aw_alerts
            WHERE state='UNREAD' OR (state='SNOOZED' AND snoozed_until <= $now)
            ORDER BY id DESC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$limit", Math.Max(1, limit));
        using var r = cmd.ExecuteReader(); var result = new List<AssessmentAlert>();
        while (r.Read()) result.Add(new(r.GetInt64(0),r.GetString(1),r.GetString(2),DateTimeOffset.Parse(r.GetString(3)),r.GetDouble(4),JsonSerializer.Deserialize<string[]>(r.GetString(5))!,r.GetString(6),r.IsDBNull(7)?null:DateTimeOffset.Parse(r.GetString(7))));
        return result.ToArray();
    }
    public void SetAlertState(long id, string state, DateTimeOffset? until = null)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "UPDATE aw_alerts SET state=$s,snoozed_until=$u WHERE id=$id";
        cmd.Parameters.AddWithValue("$s",state); cmd.Parameters.AddWithValue("$u",(object?)until?.ToString("O") ?? DBNull.Value); cmd.Parameters.AddWithValue("$id",id); cmd.ExecuteNonQuery();
    }
    public FeedHealth[] FeedHealthRecords()
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT payload FROM aw_feed_health";
        using var r = cmd.ExecuteReader(); var result = new List<FeedHealth>(); while(r.Read()) result.Add(JsonSerializer.Deserialize<FeedHealth>(r.GetString(0))!); return result.ToArray();
    }
    public void SaveFeedHealth(FeedHealth health)
    {
        using var db = Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO aw_feed_health VALUES($u,$p) ON CONFLICT(url) DO UPDATE SET payload=excluded.payload; INSERT INTO aw_feed_fetches(timestamp,url,payload) VALUES($t,$u,$p)";
        cmd.Parameters.AddWithValue("$u",health.Url); cmd.Parameters.AddWithValue("$p",JsonSerializer.Serialize(health)); cmd.Parameters.AddWithValue("$t",DateTimeOffset.UtcNow.ToString("O")); cmd.ExecuteNonQuery(); tx.Commit();
    }
    public void SaveEvaluation(DateTimeOffset at, string eventId, string outcome, string notes)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT INTO aw_evaluations(timestamp,event_id,outcome,notes) VALUES($t,$e,$o,$n)";
        cmd.Parameters.AddWithValue("$t",at.ToString("O")); cmd.Parameters.AddWithValue("$e",eventId); cmd.Parameters.AddWithValue("$o",outcome); cmd.Parameters.AddWithValue("$n",notes); cmd.ExecuteNonQuery();
    }
}
