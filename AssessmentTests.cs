using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AllianceWatch;

internal static class AssessmentTests
{
    public static void Run()
    {
        var report=new List<string>();int failures=0;
        void Test(string name,Action action){try{action();report.Add("PASS "+name);}catch(Exception ex){failures++;report.Add("FAIL "+name+": "+ex.Message);}}
        static void Assert(bool value,string message="Assertion failed"){if(!value)throw new InvalidOperationException(message);}
        var settings=new AssessmentSettings();var engine=new AssessmentEngine(settings);var now=new DateTimeOffset(2026,9,19,12,0,0,TimeSpan.Zero);
        EvidenceEvent Event(string id,string title,string origin="wire",DateTimeOffset? published=null,DateTimeOffset? seen=null,string summary="")=>engine.Extract(new(id,title,summary,"https://example.org/"+id,"test",published??now,seen??now,.8,origin));
        Test("30 stable protocols",()=>Assert(settings.Protocols.Select(p=>p.Id).SequenceEqual(Enumerable.Range(1,30))));
        string[] fixtures=["mutual defense","integrated command","joint operational planning","wartime logistics","reciprocal base access","strategic coordination","reserve mobilization","force dispersal","strategic force posture","airspace closure","maritime exclusion zone","embassy evacuation","civil defense activation","martial law","mass logistics movement","field hospitals deployed","munitions surge","command relocation","national cyber alert","critical infrastructure disruption","satellite disruption","diplomatic breakdown","ultimatum","nuclear posture","border closure","capital controls","war risk premium","war economy","interstate attack","multi-theater synchronization"];
        foreach(var p in settings.Protocols)
        {
            Test($"Protocol {p.Id:00} fixture",()=>Assert(Event(p.Id.ToString(),"Russia "+fixtures[p.Id-1]).Protocols.Contains(p.Id)));
        }
        Test("RSS parsing",()=>Assert(new XmlSourceAdapter().Parse("<rss><channel><item><title>Test</title><link>https://example.org/a</link><guid>1</guid></item></channel></rss>",new()).Single().Title=="Test"));
        Test("Atom alternate URL",()=>Assert(new XmlSourceAdapter().Parse("<feed xmlns='http://www.w3.org/2005/Atom'><entry><title>Test</title><link rel='self' href='https://example.org/self'/><link rel='alternate' href='https://example.org/story'/></entry></feed>",new()).Single().Url=="https://example.org/story"));
        Test("Malformed feed recovery",()=>Assert(new XmlSourceAdapter().Parse("<rss><channel><item><title>A & B</title></item></channel></rss>",new()).Single().Title=="A & B"));
        Test("Unsafe XML rejected",()=>{bool blocked=false;try{new XmlSourceAdapter().Parse("<!DOCTYPE rss [<!ENTITY x SYSTEM 'file:///secret'>]><rss>&x;</rss>",new());}catch(System.Xml.XmlException){blocked=true;}Assert(blocked);});
        Test("JSON field mappings",()=>Assert(new JsonSourceAdapter().Parse("{\"data\":[{\"headline\":\"Test\"}]}",new(){ItemsPath="data",FieldMap=new(){["title"]="headline"}}).Single().Title=="Test"));
        Test("CSV quotations",()=>Assert(new CsvSourceAdapter().Parse("title,summary\r\n\"A, B\",\"Line 1\nLine 2\"",new()).Single().Title=="A, B"));
        Test("Configured theater source coverage",()=>
        {
            var configured=AppConfig.Load(Path.Combine(Directory.GetCurrentDirectory(),"config.json"));
            Assert(configured.Feeds.Count>=56,"Expected the expanded source catalog");
            foreach(var theater in TheaterCatalog.All.Select(theater=>theater.Name))
                Assert(configured.Feeds.Count(feed=>feed.Group.StartsWith(theater,StringComparison.OrdinalIgnoreCase))>=4,$"Insufficient configured coverage for {theater}");
            Assert(configured.Feeds.Count(feed=>feed.Group.StartsWith("Central America",StringComparison.OrdinalIgnoreCase))>=5,"Central America needs distinct local, official, and discovery feeds");
        });
        Test("Virtual image gallery windowing", GalleryBrowserTests.VerifyVirtualGalleryWindowing);
        Test("Gallery readers never make monitor writes read-only", GalleryBrowserTests.VerifyGalleryReadsDoNotMakeMonitorReadOnly);
        Test("Shared image payloads and legacy migration", GalleryBrowserTests.VerifyImageDeduplication);
        Test("Database browser paging and global search", GalleryBrowserTests.VerifyBrowserPagingAndSearch);
        Test("All-field archive search includes old articles and compressed text", ArticleSearchTests.VerifyAllFieldsAndOldArticles);
        Test("Archives continuously drain with bounded concurrency", ArchiveWorkerTests.VerifyContinuousDrainAndConcurrency);
        Test("Article timeouts back off without stopping the archive queue", ArchiveWorkerTests.VerifyArticleTimeoutIsolation);
        Test("Image timeouts preserve the page and successful images", ArchiveWorkerTests.VerifyImageTimeoutIsolation);
        Test("Archive disposal cancels without recording false failures", ArchiveWorkerTests.VerifyDisposeCancelsArchive);
        Test("Title similarity",()=>Assert(Storage.TextSimilarity("Russia announces reserve mobilization","Russia announces reserve mobilization today")>=.8));
        Test("Protocol detection and actor aliases",()=>{var e=Event("1","DPRK reserve mobilization with NATO");Assert(e.Protocols.Contains(7)&&e.Actors.Contains("North Korea")&&e.Actors.Contains("NATO"));});
        Test("Exclusion patterns",()=>Assert(Event("1","History of Russia reserve mobilization").Protocols.Length==0));
        Test("Scenario assignment",()=>Assert(Event("1","China airspace closure over Taiwan").Scenarios.Contains("taiwan")));
        Test("Central America scenario needs two regional actors",()=>
        {
            var paired=Event("ca-pair","Guatemala and Honduras announce mutual defense talks");
            Assert(paired.Scenarios.Contains("central-america-regional"),"Two regional actors should assign Central America");
            Assert(paired.Geography.Contains("Central America"),"Central America geography should be retained");
            Assert(!paired.Scenarios.Contains("north-america-regional"),"Central America should not be conflated with North America");
            Assert(!Event("ca-single","Panama Canal security talks").Scenarios.Contains("central-america-regional"),"One incidental regional actor should not assign a theater");
        });
        Test("Canonical tracking removal",()=>Assert(AssessmentEngine.CanonicalUrl("https://example.org/x?utm_source=a&b=2#top")=="https://example.org/x?b=2"));
        Test("No future ingestion leakage",()=>Assert(engine.Evaluate([Event("1","Russia reserve mobilization",seen:now.AddHours(1))],now).Risk==0));
        Test("No future publication leakage",()=>Assert(engine.Evaluate([Event("1","Russia reserve mobilization",published:now.AddHours(1))],now).Risk==0));
        Test("Age decay",()=>{var e=Event("1","Russia reserve mobilization");Assert(engine.Evaluate([e],now.AddDays(14)).Risk<engine.Evaluate([e],now).Risk);});
        Test("Syndication does not amplify risk",()=>{var a=Event("1","Russia reserve mobilization");var b=Event("2",a.Source.Title);Assert(Math.Abs(engine.Evaluate([a],now).Risk-engine.Evaluate([a,b],now).Risk)<1e-8);});
        Test("Different websites are not independent proof",()=>{var a=Event("1","Russia reserve mobilization","site-a");var b=Event("2",a.Source.Title,"site-b");Assert(engine.Evaluate([a,b],now).Contributions.Single().IndependentSources==1);});
        Test("Copied text from verified outlets counts once",()=>{var a=Event("1","Russia reserve mobilization","site-a");var b=Event("2",a.Source.Title,"site-b");a=a with{Source=a.Source with{OriginalReporting=true}};b=b with{Source=b.Source with{OriginalReporting=true}};Assert(engine.Evaluate([a,b],now).Contributions.Single().IndependentSources==1);});
        Test("Independent corroborated convergence",()=>
        {
            var records=new List<EvidenceEvent>();int i=0;
            foreach(var phrase in new[]{"reserve mobilization","airspace closure","embassy evacuation"})
            {
                var a=Event((i++).ToString(),"Russia "+phrase,"source-a",summary:"Original observation one");a=a with{Source=a.Source with{OriginalReporting=true}};
                var b=Event((i++).ToString(),"Russia "+phrase,"source-b",summary:"Separately verified observation two");b=b with{Source=b.Source with{OriginalReporting=true}};
                records.AddRange([a,b]);
            }
            Assert(engine.Evaluate(records,now).Convergence>0);
        });
        Test("Independent risk and confidence",()=>{var a=Event("1","Russia reserve mobilization");var b=a with{Source=a.Source with{Quality=.2}};var x=engine.Evaluate([a],now);var y=engine.Evaluate([b],now);Assert(x.Risk==y.Risk&&x.Confidence>y.Confidence);});
        Test("Contradiction preserved and moderated",()=>{var a=Event("1","Russia reserve mobilization");var b=a with{Disputed=true};Assert(engine.Evaluate([b],now).Risk<engine.Evaluate([a],now).Risk);});
        Test("Retraction contributes zero",()=>Assert(engine.Evaluate([Event("1","Russia reserve mobilization") with{Retraction=true}],now).Risk==0));
        Test("Conflicting figures lower confidence",()=>{var a=Event("1","Russia reserve mobilization",summary:"Officials report 100 troops");var b=Event("2",a.Source.Title,summary:"Officials report 200 troops");Assert(engine.Evaluate([a,b],now).Confidence<engine.Evaluate([a],now).Confidence);});
        Test("Linked correction never adds risk",()=>{var a=Event("1","Russia reserve mobilization");var b=Event("2","Russia denies reserve mobilization") with{CorrectionOfEventId=a.EventId};Assert(engine.Evaluate([a,b],now).Risk<engine.Evaluate([a],now).Risk);});
        Test("Configured half-life affects existing events",()=>{var e=Event("1","Russia reserve mobilization");var changed=new AssessmentSettings{Protocols=settings.Protocols.Select(p=>p.Id==7?p with{HalfLifeHours=1}:p).ToArray()};Assert(new AssessmentEngine(changed).Evaluate([e],now.AddHours(6)).Risk<engine.Evaluate([e],now.AddHours(6)).Risk);});
        Test("Negative evidence",()=>Assert(engine.Evaluate([Event("1","Russia reserve mobilization")],now).Contributions.Single().NegativeEvidence>0));
        Test("No unsupported convergence",()=>Assert(engine.Evaluate([Event("1","Russia reserve mobilization airspace closure embassy evacuation")],now).Convergence==0));
        Test("Audit delta reconciles",()=>{var a=Event("1","Russia reserve mobilization");var before=engine.Evaluate([a],now);var after=engine.Evaluate([a,Event("2","Taiwan embassy evacuation")],now.AddHours(6),before);Assert(Math.Abs(after.Changes.Sum(c=>c.Delta)-after.Delta)<1e-8);});
        Test("Nonadjacent score explanation reconciles",()=>
        {
            var a=Event("1","Russia reserve mobilization");
            var b=Event("2","Taiwan embassy evacuation",published:now.AddHours(6),seen:now.AddHours(6));
            var before=engine.Evaluate([a],now);
            var jumped=engine.Evaluate([a,b],now.AddHours(6),before);
            var latest=engine.Evaluate([a,b],now.AddHours(6).AddSeconds(8),jumped);
            var changes=AssessmentEngine.ExplainChanges(latest,before);
            Assert(Math.Abs(changes.Sum(c=>c.Delta)-(latest.Risk-before.Risk))<1e-8,"Raw, convergence and normalization rows must reconcile across scans");
            Assert(changes.Any(c=>c.EventId==b.ClusterId && c.Delta>0),"New evidence must remain visible after a tiny follow-up scan");
        });
        Test("Last gauge move comparison skips tiny snapshots",()=>
        {
            var baseline=engine.Evaluate([],now) with{Risk=56.98};
            var moved=baseline with{Timestamp=now.AddMinutes(10),Risk=61.47};
            var latest=moved with{Timestamp=now.AddMinutes(10).AddSeconds(8),Risk=61.472};
            var history=new[]{latest,moved,baseline};
            Assert(AssessmentForm.ChangeBaseline(latest,history,"LAST GAUGE MOVE")==baseline.Timestamp);
            Assert(AssessmentForm.ChangeBaseline(latest,history,"PREVIOUS SNAPSHOT")==moved.Timestamp);
            Assert(AssessmentForm.ChangeBaseline(latest,history,"24 HOURS")==null);
        });
        Test("Coverage distinguishes stale feeds and independent origins",()=>
        {
            var config=new AppConfig{Feeds=[new(){Name="Asia wire",Url="https://example.org/asia",Group="Asia / News",IntervalMinutes=10},
                new(){Name="Europe wire",Url="https://example.org/europe",Group="Europe / News",IntervalMinutes=10}]};
            var report=Event("coverage","China reserve mobilization") with{Source=Event("coverage","China reserve mobilization").Source with
                {Publisher="Asia wire",OriginalReporting=true,Origin="Original newsroom"}};
            var health=new[]{new FeedHealth("https://example.org/asia","Asia wire",LastSuccess:now.AddMinutes(-5)),
                new FeedHealth("https://example.org/europe","Europe wire",LastSuccess:now.AddHours(-3),Failures:2)};
            var rows=OperationsAnalytics.Coverage(config,health,[report],now);
            Assert(rows.Single(r=>r.Theater=="Asia").VerifiedOrigins==1);
            Assert(rows.Single(r=>r.Theater=="Asia").Status=="LIMITED INDEPENDENCE");
            Assert(rows.Single(r=>r.Theater=="Europe").Status=="COLLECTION GAP");
            var warnings=OperationsAnalytics.Quality(config,health,[report],rows,[],1,now);
            Assert(warnings.Any(w=>w.Area=="TIMESTAMPS") && warnings.Any(w=>w.Area=="Asia"));
        });
        Test("Scan briefing retains new events and claim comparisons",()=>
        {
            var a=Event("brief-a","Russia reserve mobilization");var b=Event("brief-b","Taiwan embassy evacuation",published:now.AddHours(1),seen:now.AddHours(1));
            var before=engine.Evaluate([a],now);var after=engine.Evaluate([a,b],now.AddHours(1),before);
            Assert(OperationsAnalytics.SinceLastAssessment(after,before,[a,b]).Any(r=>r.Kind=="NEW EVENT" && r.ClusterId==b.ClusterId));
            Assert(OperationsAnalytics.Claims([a,b,b with{EventId="AW-E-brief-b2",Source=b.Source with{RecordId="brief-b2"},Disputed=true}])
                .Any(c=>c.ClusterId==b.ClusterId && c.Challenges==1));
            var correction=Event("brief-c","Russia denies reserve mobilization") with{CorrectionOfEventId=a.EventId};
            Assert(OperationsAnalytics.ClaimGroups([a,correction])[a.ClusterId].Length==2,
                "A linked correction must appear alongside the original claim even when its cluster differs");
        });
        Test("What-if is isolated and shows rule versus source effects",()=>
        {
            var e=Event("whatif","Russia reserve mobilization");var originalQuality=e.Source.Quality;
            var settingsCopy=JsonSerializer.Serialize(settings);
            var rule=OperationsAnalytics.CompareWhatIf(settings,[e],now,20,2,7,10,"",.5);
            Assert(rule.Variant.Risk>rule.Baseline.Risk,"Rule change should alter the simulated index");
            var source=OperationsAnalytics.CompareWhatIf(settings,[e],now,settings.NormalizationScale,settings.ConvergenceBonus,0,0,e.Source.Origin,.1);
            Assert(Math.Abs(source.Variant.Risk-source.Baseline.Risk)<1e-9,"Quality changes should not covertly change risk");
            Assert(source.Variant.Confidence<source.Baseline.Confidence);
            Assert(e.Source.Quality==originalQuality && JsonSerializer.Serialize(settings)==settingsCopy,"Live input must remain untouched");
        });
        Test("No invented momentum",()=>Assert(engine.Evaluate([],now).Momentum=="INSUFFICIENT HISTORY"));
        Test("Bounds",()=>{var a=engine.Evaluate(Enumerable.Range(0,1000).Select(i=>Event(i.ToString(),$"Russia reserve mobilization {i}")),now);Assert(a.Risk is >=0 and <=100 && a.Confidence is >=0 and <=100);});
        Test("Deterministic scoring corpus",()=>
        {
            var corpus=Enumerable.Range(0,120).Select(i=>Event(i.ToString(),$"Russia {fixtures[i%30]} episode {i%5}","origin-"+(i%4),now.AddHours(-i),now.AddHours(-i),"Independent text "+i)).ToArray();
            var snapshot=JsonSerializer.SerializeToElement(engine.Evaluate(corpus,now));
            using var stream=new MemoryStream();
            using(var writer=new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach(var property in snapshot.EnumerateObject())
                    if(property.Name is not ("ArticleCount" or "EventCount" or "ProtocolCount")){writer.WritePropertyName(property.Name);writer.WriteRawValue(property.Value.GetRawText());}
                writer.WriteEndObject();
            }
            File.WriteAllText("assessment-scoring-fingerprint.txt",AssessmentEngine.Hash(System.Text.Encoding.UTF8.GetString(stream.ToArray())));
        });
        Test("Invalid configuration rejected",()=>{bool caught=false;try{new AssessmentSettings{NormalizationScale=0}.Validate();}catch(InvalidDataException){caught=true;}Assert(caught);});
        Test("Export excludes full text",()=>{var e=Event("1","Russia reserve mobilization",summary:"PRIVATE FULL TEXT");Assert(!AssessmentExport.Render(engine.Evaluate([e],now),[e],"JSON").Contains("PRIVATE FULL TEXT"));});
        Test("Database migrations / preservation / append-only log",()=>
        {
            var path=Path.Combine(Path.GetTempPath(),"aw-test-"+Guid.NewGuid()+".db");
            try
            {
                var storage=new Storage(path);storage.Initialize();storage.InsertArticle("old","test","Russia reserve mobilization","https://example.org/old",now.ToString("O"),"");storage.MigrateAssessment();storage.MigrateAssessment();storage.SaveRules(settings);
                var config=new AppConfig{Assessment=settings,Feeds=[new(){Name="test",Url="https://example.org/rss"}]};
                storage.RefreshAssessment(engine,config);storage.RefreshAssessment(engine,config);
                Assert(storage.GetStats().TotalArticles==1,"Legacy article preserved");
                Assert(storage.AssessmentHistory().Length==2,"Two appended assessments");
                Assert(storage.LoadEvidence(now.AddYears(1)).Length==1,"Normalized evidence persisted");
                Assert(storage.ScoreHistory().Length==2,"Lightweight timeline summaries persisted");
                Assert(storage.ResolveArticleVersion("old","Russia reserve mobilization","")=="old","Unchanged GUID stable");
                Assert(storage.ResolveArticleVersion("old","Correction: Russia reserve mobilization","")!="old","Changed GUID creates immutable version");
                using var db=new SqliteConnection("Data Source="+path);db.Open();using var cmd=db.CreateCommand();cmd.CommandText="UPDATE aw_assessments SET version='tampered'";bool blocked=false;try{cmd.ExecuteNonQuery();}catch(SqliteException){blocked=true;}Assert(blocked);
            }
            finally{SqliteConnection.ClearAllPools();foreach(var suffix in new[]{"","-wal","-shm"})if(File.Exists(path+suffix))File.Delete(path+suffix);}
        });
        Test("Ignored news leaves the score and can be restored",()=>
        {
            var path=Path.Combine(Path.GetTempPath(),"aw-ignore-test-"+Guid.NewGuid()+".db");
            try
            {
                var storage=new Storage(path);storage.Initialize();storage.MigrateAssessment();
                var config=new AppConfig{Feeds=[new(){Name="fixture",Url="https://example.org/rss"}]};
                storage.InsertArticle("ignore-fixture","fixture","Russia reserve mobilization","https://example.org/ignore",
                    DateTimeOffset.UtcNow.AddMinutes(-2).ToString("O"),"");
                storage.InsertMatch(new MatchResult{ArticleHash="ignore-fixture",Severity="YELLOW",Score=5,SourceWeight=5});
                var before=storage.RefreshAssessment(new(config.Assessment),config);
                Assert(before.Risk>0 && storage.RecentEvents().Count==1,"The fixture should affect the current score and news list.");
                Assert(storage.SetNewsIgnored("ignore-fixture",true,"Duplicated or irrelevant report"));
                Assert(!storage.SetNewsIgnored("ignore-fixture",true,"Repeated click"),"Repeated ignores should not append duplicate decisions.");
                var ignored=storage.RefreshAssessment(new(config.Assessment),config);
                Assert(ignored.Risk<before.Risk && storage.LoadEvidence(forScoring:true).Length==0,
                    "Ignored evidence must not enter the new score calculation.");
                Assert(storage.LoadEvidence().Length==1 && storage.GetStats().TotalArticles==1,
                    "The original article and normalized evidence must remain intact.");
                Assert(storage.RecentEvents().Count==0 && storage.UnreadAlerts().Count==0 && storage.GetStats().TotalMatches==0,
                    "Ignored news must leave the main list and active signal counts.");
                Assert(storage.IgnoredNewsCount()==1 && storage.IgnoredNews().Single().Reason=="Duplicated or irrelevant report",
                    "The ignored-news section must retain the reason and article details.");
                using var ignoredForm=new IgnoredNewsForm(storage,config);_ = ignoredForm.Handle;
                var reload=typeof(IgnoredNewsForm).GetMethod("Reload",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
                reload.Invoke(ignoredForm,null);
                var ignoredGrid=ignoredForm.Controls.OfType<DataGridView>().Single();
                Assert(ignoredGrid.Rows.Count==1 && ignoredGrid.Rows[0].Cells[nameof(IgnoredNewsItem.Title)].Value?.ToString()=="Russia reserve mobilization",
                    "The dedicated ignored-news view must show the excluded article.");
                Assert(storage.SetNewsIgnored("ignore-fixture",false,"Restored after review"));
                var restored=storage.RefreshAssessment(new(config.Assessment),config);
                Assert(restored.Risk>ignored.Risk && storage.RecentEvents().Count==1 && storage.IgnoredNewsCount()==0,
                    "Restoring the article must return it to the score and news list.");
                reload.Invoke(ignoredForm,null);
                Assert(ignoredGrid.Rows.Count==0,"Restored articles must leave the ignored-news list.");
                using var db=new SqliteConnection("Data Source="+path);db.Open();using var command=db.CreateCommand();
                command.CommandText="SELECT group_concat(action,',') FROM aw_score_exclusion_log ORDER BY id";
                Assert(command.ExecuteScalar()?.ToString()=="IGNORE,RESTORE","Ignore and restore must have an append-only audit trail.");
            }
            finally{SqliteConnection.ClearAllPools();foreach(var suffix in new[]{"","-wal","-shm"})if(File.Exists(path+suffix))File.Delete(path+suffix);}
        });
        Test("Collector isolation / conditional HTTP / bounded workers",()=>
        {
            var path=Path.Combine(Path.GetTempPath(),"aw-feed-test-"+Guid.NewGuid()+".db");
            try
            {
                var storage=new Storage(path);storage.Initialize();storage.MigrateAssessment();
                var config=new AppConfig{ArchiveEnabled=false,Feeds=Enumerable.Range(0,10).Select(i=>new FeedConfig{Name="feed"+i,Url="https://example.org/"+i}).ToList()};
                using var handler=new FixtureHttpHandler();using var monitor=new FeedMonitor(config,storage,path+".log",handler);
                var first=Task.Run(() => monitor.ScanAsync()).GetAwaiter().GetResult();Assert(storage.GetStats().TotalArticles==9,"Healthy feeds survive one malformed feed");Assert(handler.MaximumConcurrency<=4,"Worker limit");
                Assert(first.Alerts.Count==9&&storage.UnreadAlerts().Count==9,"Actionable signals reach the alert center");
                foreach(var h in storage.FeedHealthRecords())storage.SaveFeedHealth(h with{NextFetch=DateTimeOffset.UtcNow.AddMinutes(-1)});
                Task.Run(() => monitor.ScanAsync()).GetAwaiter().GetResult();Assert(handler.ConditionalRequests==9,"ETags sent");Assert(storage.GetStats().TotalArticles==9,"304 does not duplicate articles");
            }
            finally{SqliteConnection.ClearAllPools();foreach(var suffix in new[]{"","-wal","-shm",".log"})if(File.Exists(path+suffix))File.Delete(path+suffix);}
        });
        Test("Rule alert deduplication",()=>
        {
            var path=Path.Combine(Path.GetTempPath(),"aw-alert-test-"+Guid.NewGuid()+".db");
            try
            {
                var storage=new Storage(path);storage.Initialize();storage.MigrateAssessment();var cfg=new AppConfig{Assessment=new(){AlertThreshold=.01}};
                storage.InsertArticle("alert-fixture","fixture","Russia reserve mobilization","https://example.org/test",DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"),"");
                storage.RefreshAssessment(new(cfg.Assessment),cfg);storage.RefreshAssessment(new(cfg.Assessment),cfg);Assert(storage.AssessmentAlerts().Length==1,"One threshold crossing alert");
                var a=storage.AssessmentAlerts().Single();storage.SetAlertState(a.Id,"SNOOZED",DateTimeOffset.UtcNow.AddHours(1));Assert(storage.AssessmentAlerts().Single().State=="SNOOZED");
            }
            finally{SqliteConnection.ClearAllPools();foreach(var suffix in new[]{"","-wal","-shm"})if(File.Exists(path+suffix))File.Delete(path+suffix);}
        });
        Test("Watchlist alerts, review audit, and verified restore copy",()=>
        {
            var path=Path.Combine(Path.GetTempPath(),"aw-ops-test-"+Guid.NewGuid()+".db");
            var backup=path+".backup.db";var restored=path+".restored.db";
            try
            {
                var storage=new Storage(path);storage.Initialize();storage.MigrateAssessment();
                var config=new AppConfig{Feeds=[new(){Name="fixture",Url="https://example.org/rss"}]};
                storage.SaveWatchItem("ACTOR","Russia",0);
                storage.InsertArticle("ops-fixture","fixture","Russia reserve mobilization","https://example.org/ops",DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"),"");
                var first=storage.RefreshAssessment(new(config.Assessment),config);
                Assert(storage.AssessmentAlerts().Count(a=>a.Key.StartsWith("watch|"))==1,"New matching evidence should alert once");
                storage.RefreshAssessment(new(config.Assessment),config);
                Assert(storage.AssessmentAlerts().Count(a=>a.Key.StartsWith("watch|"))==1,"Unchanged evidence should not re-alert");
                var watch=storage.WatchItems().Single();storage.SetWatchItemEnabled(watch.Id,false);
                Assert(!storage.WatchItems().Single().Enabled);
                var cluster=first.Contributions.Single().EventId;
                storage.SaveReview(cluster,"FALSE POSITIVE","Context suggests historical reference.");
                storage.SaveReview(cluster,"NEEDS REVIEW","Reassessment pending.");
                Assert(storage.ReviewHistory().Length==2 && storage.AssessmentHistory(1).Single().Contributions.Single().EventId==cluster,
                    "Reviews should append without modifying evidence");
                using(var db=new SqliteConnection("Data Source="+path))
                {db.Open();using var cmd=db.CreateCommand();cmd.CommandText="UPDATE aw_review_log SET outcome='SUPPORTED'";
                    bool blocked=false;try{cmd.ExecuteNonQuery();}catch(SqliteException){blocked=true;}Assert(blocked,"Review audit must be immutable");}
                var verified=storage.CreateVerifiedBackup(backup);
                Assert(verified.Integrity=="ok" && verified.Articles==1 && verified.Assessments==2);
                bool liveRejected=false;try{storage.VerifyBackup(path);}catch(ArgumentException){liveRejected=true;}
                Assert(liveRejected,"The running database must not be mislabeled as a verified backup");
                var copy=new Storage(backup).CreateVerifiedBackup(restored);
                Assert(copy.Integrity=="ok" && copy.Articles==1 && copy.Assessments==2,"Restored copy must open and match counts");
            }
            finally
            {
                SqliteConnection.ClearAllPools();foreach(var file in new[]{path,backup,restored})foreach(var suffix in new[]{"","-wal","-shm"})
                    if(File.Exists(file+suffix))File.Delete(file+suffix);
            }
        });
        Test("Since-last-scan baseline ignores startup and import runs",()=>
        {
            var path=Path.Combine(Path.GetTempPath(),"aw-run-test-"+Guid.NewGuid()+".db");
            try
            {
                var storage=new Storage(path);storage.Initialize();storage.MigrateAssessment();var config=new AppConfig();
                storage.RefreshAssessment(new(config.Assessment),config,"STARTUP");
                var scan=storage.RefreshAssessment(new(config.Assessment),config,"SCAN");
                storage.RefreshAssessment(new(config.Assessment),config,"IMPORT");
                var latest=storage.RefreshAssessment(new(config.Assessment),config,"SCAN");
                Assert(storage.PreviousCompletedScan(latest.Timestamp)==scan.Timestamp);
            }
            finally{SqliteConnection.ClearAllPools();foreach(var suffix in new[]{"","-wal","-shm"})if(File.Exists(path+suffix))File.Delete(path+suffix);}
        });
        Test("All assessment tabs load before hidden grids bind",()=>
        {
            var path=Path.Combine(Path.GetTempPath(),"aw-console-test-"+Guid.NewGuid()+".db");
            var previousContext=SynchronizationContext.Current;
            try
            {
                var storage=new Storage(path);storage.Initialize();storage.MigrateAssessment();
                var config=new AppConfig();storage.InsertArticle("console-fixture","fixture","Russia reserve mobilization","https://example.invalid/test",DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"),"");
                storage.RefreshAssessment(new(config.Assessment),config);
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                using var form=new AssessmentForm(storage,config);
                var load=form.LoadAsync();var timeout=Stopwatch.StartNew();
                while(!load.IsCompleted&&timeout.Elapsed<TimeSpan.FromSeconds(10)){Application.DoEvents();Thread.Sleep(1);}
                Assert(load.IsCompleted,"Console load timed out");load.GetAwaiter().GetResult();
                var status=form.Controls.OfType<Label>().Single().Text;
                Assert(!status.Contains("LOAD FAILED"),status);
                var tabs=form.Controls.OfType<TabControl>().Single();
                Assert(tabs.TabPages.Count==17,$"Expected all 17 tabs, got {tabs.TabPages.Count}");
                var grid=tabs.TabPages.Cast<TabPage>().Single(p=>p.Text=="WHY SCORE MOVED").Controls.OfType<DataGridView>().Single();
                Assert(grid.Columns["Delta"]?.DisplayIndex==0,"Delta column must be visible first before data binding");
                storage.RefreshAssessment(new(config.Assessment),config);
                var reload=form.LoadAsync();timeout.Restart();
                while(!reload.IsCompleted&&timeout.Elapsed<TimeSpan.FromSeconds(10)){Application.DoEvents();Thread.Sleep(1);}
                Assert(reload.IsCompleted,$"Console reload timed out; status={form.Controls.OfType<Label>().Single().Text}; tabs={tabs.TabPages.Count}");reload.GetAwaiter().GetResult();
                Assert(tabs.TabPages.Count==17,"Reload must replace tabs rather than duplicating them");
                Assert(!form.Controls.OfType<Label>().Single().Text.Contains("LOAD FAILED"),"Reload should succeed");
                UiLayoutTests.Verify(form);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                SqliteConnection.ClearAllPools();foreach(var suffix in new[]{"","-wal","-shm"})if(File.Exists(path+suffix))File.Delete(path+suffix);
            }
        });
        Test("Operations workspace loads all eight workflows offline",()=>
        {
            var path=Path.Combine(Path.GetTempPath(),"aw-ops-ui-"+Guid.NewGuid()+".db");
            var previousContext=SynchronizationContext.Current;
            try
            {
                var storage=new Storage(path);storage.Initialize();storage.MigrateAssessment();
                var config=new AppConfig{Feeds=[new(){Name="fixture",Url="https://example.org/rss",Group="Europe / News"}]};
                storage.InsertArticle("ops-ui","fixture","Russia reserve mobilization","https://example.org/ops-ui",DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"),"");
                storage.RefreshAssessment(new(config.Assessment),config);
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                using var form=new OperationsForm(storage,config);
                var load=form.RefreshAsync();var timeout=Stopwatch.StartNew();
                while(!load.IsCompleted&&timeout.Elapsed<TimeSpan.FromSeconds(10)){Application.DoEvents();Thread.Sleep(1);}
                Assert(load.IsCompleted,"Operations workspace load timed out");load.GetAwaiter().GetResult();
                Assert(!form.Controls.OfType<Label>().Single().Text.Contains("FAILED"),"Operations workspace should load without errors");
                var tabs=form.Controls.OfType<TabControl>().Single();
                Assert(tabs.TabPages.Count==8,"All eight operations tabs must be available");
                var coverage=tabs.TabPages.Cast<TabPage>().Single(p=>p.Text=="COVERAGE");
                Assert(coverage.Controls.OfType<SplitContainer>().Single().Panel1.Controls.OfType<DataGridView>().Single().Rows.Count==TheaterCatalog.All.Length);
                static IEnumerable<Control> Descendants(Control root)=>root.Controls.Cast<Control>().SelectMany(child=>new[]{child}.Concat(Descendants(child)));
                using var main=new MainForm(Directory.GetCurrentDirectory(),config,storage,true);
                Assert(Descendants(main).OfType<Button>().Any(b=>b.Text.Contains("OPERATIONS WORKSPACE")),"Main dashboard must open operations");
                Assert(Descendants(main).OfType<Button>().Any(b=>b.Text.Contains("IGNORED NEWS")),"Ignored news must be reachable from the main interface.");
                Assert(Descendants(main).OfType<TextBox>().Any(box=>Equals(box.Tag,"archive-search")),"Main dashboard must expose all-article search.");
                Assert(Descendants(main).OfType<DataGridView>().Any(g=>g.ContextMenuStrip?.Items.Cast<ToolStripItem>()
                    .Any(item=>item.Text?.Contains("IGNORE THIS NEWS ITEM")==true)==true),"Main news rows must have an ignore context menu.");
                using var ignoredForm=new IgnoredNewsForm(storage,config);
                Assert(Descendants(ignoredForm).OfType<DataGridView>().Any(),"The ignored-news view must expose a restorable list.");
                using var browser=new DatabaseBrowserForm(storage);
                Assert(Descendants(browser).OfType<ComboBox>().Any(c=>c.Items.Contains("ANALYST REVIEWS")&&c.Items.Contains("ASSESSMENT RUNS")&&c.Items.Contains("SCORE EXCLUSION LOG")),
                    "New audit datasets should be available in the database browser");
                using var map=new MapForm(storage,config);
                using var help=new HelpForm(config.Assessment.Protocols);
                using var archiveSearch=new ArticleSearchForm(storage,config,"fixture");
                UiLayoutTests.Verify(main,form,ignoredForm,browser,map,help,archiveSearch);
                var viewPicker=Descendants(browser).OfType<ComboBox>().Single(c=>c.Items.Contains("ARCHIVED IMAGES"));
                viewPicker.SelectedItem="ARCHIVED IMAGES";
                UiLayoutTests.Verify(browser);
            }
            finally{SynchronizationContext.SetSynchronizationContext(previousContext);SqliteConnection.ClearAllPools();
                foreach(var suffix in new[]{"","-wal","-shm"})if(File.Exists(path+suffix))File.Delete(path+suffix);}
        });
        File.WriteAllLines("assessment-test-results.txt",report.Append($"{report.Count-failures} passed / {failures} failed"));Environment.ExitCode=failures==0?0:1;
    }
}
internal sealed class FixtureHttpHandler(int itemsPerFeed = 1) : HttpMessageHandler
{
    private int _active;
    public int MaximumConcurrency;
    public int ConditionalRequests;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
    {
        var active=Interlocked.Increment(ref _active);int observed;
        do{observed=MaximumConcurrency;}while(active>observed&&Interlocked.CompareExchange(ref MaximumConcurrency,active,observed)!=observed);
        try
        {
            await Task.Delay(5,cancellationToken);
            if(request.Headers.Contains("If-None-Match")){Interlocked.Increment(ref ConditionalRequests);return new(System.Net.HttpStatusCode.NotModified);}
            var id=request.RequestUri!.AbsolutePath.Trim('/');
            var items=string.Join("",Enumerable.Range(0,itemsPerFeed).Select(i=>$"<item><title>Russia reserve mobilization {id} report {i}</title><guid>{id}-{i}</guid></item>"));
            var response=new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new StringContent(id=="0"?"<broken>":$"<rss><channel>{items}</channel></rss>")};
            response.Headers.ETag=new System.Net.Http.Headers.EntityTagHeaderValue("\"fixture\"");return response;
        }
        finally{Interlocked.Decrement(ref _active);}
    }
}
internal static class AssessmentBenchmark
{
    public static void Run()
    {
        var rows=new List<string>();var sw=Stopwatch.StartNew();var settings=new AssessmentSettings();var engine=new AssessmentEngine(settings);rows.Add($"Engine startup: {sw.Elapsed.TotalMilliseconds:F2} ms");
        var now=DateTimeOffset.UtcNow;sw.Restart();var records=Enumerable.Range(0,1000).Select(i=>engine.Extract(new(i.ToString(),$"Russia reserve mobilization region {i}","","https://example.org/"+i,"synthetic",now,now,.7,"origin-"+(i%10)))).ToArray();rows.Add($"Extract 1,000 articles: {sw.Elapsed.TotalMilliseconds:F2} ms");
        sw.Restart();var score=engine.Evaluate(records,now);rows.Add($"Score 1,000 articles: {sw.Elapsed.TotalMilliseconds:F2} ms; risk {score.Risk:R}");
        sw.Restart();var hashes=records.Select(e=>e.CanonicalHash).ToHashSet();for(int i=0;i<10000;i++)_ = hashes.Contains(records[i%1000].CanonicalHash);rows.Add($"10,000 hash duplicate lookups: {sw.Elapsed.TotalMilliseconds:F2} ms");
        rows.Add($"Managed memory: {GC.GetTotalMemory(true)/1048576d:F2} MiB");rows.Add("Synthetic local benchmark; network polling, multi-day memory, and large-archive UI require separate measurement.");File.WriteAllLines("assessment-benchmark.txt",rows);
        var path=Path.Combine(Path.GetTempPath(),"aw-benchmark-"+Guid.NewGuid()+".db");
        try
        {
            sw.Restart();var storage=new Storage(path);storage.Initialize();storage.MigrateAssessment();rows.Add($"Cold database startup / migrations: {sw.Elapsed.TotalMilliseconds:F2} ms");
            sw.Restart();storage.Initialize();storage.MigrateAssessment();rows.Add($"Warm database startup / migrations: {sw.Elapsed.TotalMilliseconds:F2} ms");
            var config=new AppConfig{ArchiveEnabled=false,Feeds=Enumerable.Range(1,100).Select(i=>new FeedConfig{Name="benchmark-"+i,Url="https://example.org/"+i}).ToList()};
            using var handler=new FixtureHttpHandler(10);using var monitor=new FeedMonitor(config,storage,path+".log",handler);
            sw.Restart();Task.Run(() => monitor.ScanAsync()).GetAwaiter().GetResult();rows.Add($"100-feed polling / 1,000 new articles (mock transport, 4 workers): {sw.Elapsed.TotalMilliseconds:F2} ms");
            sw.Restart();storage.NormalizePending(engine,config);rows.Add($"Normalize / duplicate cluster / persist 1,000 articles: {sw.Elapsed.TotalMilliseconds:F2} ms");
            using(var db=new SqliteConnection("Data Source="+path))
            {
                db.Open();using var cmd=db.CreateCommand();cmd.CommandText="""
                    WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<99000)
                    INSERT INTO articles(article_hash,feed_name,title,url,published,summary,first_seen)
                    SELECT 'archive-'||x,'synthetic archive','Non-indicator archive record '||x,'','2020-01-01T00:00:00.0000000+00:00','','2020-01-01T00:00:00.0000000+00:00' FROM n;
                    ANALYZE;
                    """;
                sw.Restart();cmd.ExecuteNonQuery();rows.Add($"Prepare archive of 100,000 stored records: {sw.Elapsed.TotalMilliseconds:F2} ms");
                cmd.CommandText="SELECT article_hash,title FROM articles WHERE title LIKE '%9999%' ORDER BY first_seen DESC LIMIT 250";
                sw.Restart();using(var reader=cmd.ExecuteReader()){while(reader.Read()){} }rows.Add($"100,000-record text search (LIMIT 250): {sw.Elapsed.TotalMilliseconds:F2} ms");
            }
            sw.Restart();_ = storage.GetStats();_ = storage.RecentEvents();rows.Add($"100,000-record dashboard query: {sw.Elapsed.TotalMilliseconds:F2} ms");
            sw.Restart();using(var form=new MainForm(Directory.GetCurrentDirectory(),config,storage,true)){_ = form.Handle;form.PerformLayout();form.Refresh();}rows.Add($"Dashboard construction and initial layout (no network): {sw.Elapsed.TotalMilliseconds:F2} ms");
            sw.Restart();var recent=storage.RecentEvidence(now.AddDays(-7));var groups=OperationsAnalytics.ClaimGroups(recent);
            var claimRows=OperationsAnalytics.Claims(groups);var coverage=OperationsAnalytics.Coverage(config,storage.FeedHealthRecords(),recent,now);
            rows.Add($"Operations recent-evidence load, claim grouping and coverage: {sw.Elapsed.TotalMilliseconds:F2} ms; {claimRows.Length} families / {coverage.Length} theatres");
            sw.Restart();using(var form=new OperationsForm(storage,config)){_ = form.Handle;form.PerformLayout();form.Refresh();}
            rows.Add($"Operations workspace construction and initial layout (no network): {sw.Elapsed.TotalMilliseconds:F2} ms");
            var before=GC.GetTotalMemory(true);sw.Restart();
            for(int i=0;i<10;i++)
            {
                foreach(var h in storage.FeedHealthRecords())storage.SaveFeedHealth(h with{NextFetch=DateTimeOffset.UtcNow.AddMinutes(-1)});
                Task.Run(() => monitor.ScanAsync()).GetAwaiter().GetResult();
            }
            var after=GC.GetTotalMemory(true);rows.Add($"10 additional 100-feed conditional cycles: {sw.Elapsed.TotalMilliseconds:F2} ms; managed memory delta {(after-before)/1048576d:F2} MiB; conditional requests {handler.ConditionalRequests}");
            rows.Add("Live internet latency and multi-day continuous operation are NOT simulated by these local measurements.");
        }
        finally{SqliteConnection.ClearAllPools();foreach(var suffix in new[]{"","-wal","-shm",".log"})if(File.Exists(path+suffix))File.Delete(path+suffix);File.WriteAllLines("assessment-benchmark.txt",rows);}
    }
}
