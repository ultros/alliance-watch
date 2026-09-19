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
        Test("Title similarity",()=>Assert(Storage.TextSimilarity("Russia announces reserve mobilization","Russia announces reserve mobilization today")>=.8));
        Test("Protocol detection and actor aliases",()=>{var e=Event("1","DPRK reserve mobilization with NATO");Assert(e.Protocols.Contains(7)&&e.Actors.Contains("North Korea")&&e.Actors.Contains("NATO"));});
        Test("Exclusion patterns",()=>Assert(Event("1","History of Russia reserve mobilization").Protocols.Length==0));
        Test("Scenario assignment",()=>Assert(Event("1","China airspace closure over Taiwan").Scenarios.Contains("taiwan")));
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
        Test("Collector isolation / conditional HTTP / bounded workers",()=>
        {
            var path=Path.Combine(Path.GetTempPath(),"aw-feed-test-"+Guid.NewGuid()+".db");
            try
            {
                var storage=new Storage(path);storage.Initialize();storage.MigrateAssessment();
                var config=new AppConfig{ArchiveEnabled=false,Feeds=Enumerable.Range(0,10).Select(i=>new FeedConfig{Name="feed"+i,Url="https://example.org/"+i}).ToList()};
                using var handler=new FixtureHttpHandler();using var monitor=new FeedMonitor(config,storage,path+".log",handler);
                Task.Run(() => monitor.ScanAsync()).GetAwaiter().GetResult();Assert(storage.GetStats().TotalArticles==9,"Healthy feeds survive one malformed feed");Assert(handler.MaximumConcurrency<=4,"Worker limit");
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
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                SqliteConnection.ClearAllPools();foreach(var suffix in new[]{"","-wal","-shm"})if(File.Exists(path+suffix))File.Delete(path+suffix);
            }
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
