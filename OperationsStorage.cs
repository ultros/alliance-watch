using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace AllianceWatch;

internal sealed record WatchItem(long Id, string Kind, string Value, double MinConfidence, bool Enabled);
internal sealed record AnalystReview(long Id, DateTimeOffset Timestamp, string ClusterId, string Outcome, string Notes);
internal sealed record BackupCheck(string Path, long Bytes, long Articles, long Assessments, string Integrity);

internal sealed partial class Storage
{
    internal string DatabasePath => Path.GetFullPath(databasePath);

    public WatchItem[] WatchItems()
    {
        using var db=OpenReadOnly();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT id,kind,value,min_confidence,enabled FROM aw_watch_items ORDER BY enabled DESC,kind,value";
        using var reader=cmd.ExecuteReader();var rows=new List<WatchItem>();
        while(reader.Read())rows.Add(new(reader.GetInt64(0),reader.GetString(1),reader.GetString(2),reader.GetDouble(3),reader.GetInt64(4)!=0));
        return rows.ToArray();
    }

    public void SaveWatchItem(string kind,string value,double minConfidence)
    {
        kind=kind.Trim().ToUpperInvariant();value=value.Trim();
        if(kind is not ("THEATER" or "ACTOR" or "PROTOCOL") || value.Length is < 1 or > 120 || !double.IsFinite(minConfidence) || minConfidence is < 0 or > 100)
            throw new ArgumentException("Invalid watchlist kind, value or confidence threshold.");
        if(kind=="PROTOCOL" && (!int.TryParse(value,out var id) || id is < 1 or > 30))throw new ArgumentException("Protocol must be 1–30.");
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="INSERT INTO aw_watch_items(kind,value,min_confidence,enabled) VALUES($k,$v,$c,1) ON CONFLICT(kind,value) DO UPDATE SET min_confidence=excluded.min_confidence,enabled=1";
        cmd.Parameters.AddWithValue("$k",kind);cmd.Parameters.AddWithValue("$v",value);cmd.Parameters.AddWithValue("$c",minConfidence);cmd.ExecuteNonQuery();
    }

    public void SetWatchItemEnabled(long id,bool enabled)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="UPDATE aw_watch_items SET enabled=$e WHERE id=$id";
        cmd.Parameters.AddWithValue("$e",enabled?1:0);cmd.Parameters.AddWithValue("$id",id);cmd.ExecuteNonQuery();
    }

    public AnalystReview[] ReviewHistory(int limit=500)
    {
        using var db=OpenReadOnly();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT id,timestamp,cluster_id,outcome,notes FROM aw_review_log ORDER BY id DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit",Math.Clamp(limit,1,5000));
        using var reader=cmd.ExecuteReader();var rows=new List<AnalystReview>();
        while(reader.Read())rows.Add(new(reader.GetInt64(0),DateTimeOffset.Parse(reader.GetString(1)),reader.GetString(2),reader.GetString(3),reader.GetString(4)));
        return rows.ToArray();
    }

    public AnalystReview SaveReview(string clusterId,string outcome,string notes)
    {
        clusterId=clusterId.Trim();outcome=outcome.Trim().ToUpperInvariant();notes=notes.Trim();
        if(outcome is not ("NEEDS REVIEW" or "SUPPORTED" or "FALSE POSITIVE" or "DISPUTED") || notes.Length>4000)
            throw new ArgumentException("Choose a review outcome and keep notes under 4,000 characters.");
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT 1 FROM aw_events WHERE cluster_id=$id LIMIT 1";cmd.Parameters.AddWithValue("$id",clusterId);
        if(cmd.ExecuteScalar() is null)throw new ArgumentException("The selected event family was not found.");
        var now=DateTimeOffset.UtcNow;
        cmd.CommandText="INSERT INTO aw_review_log(timestamp,cluster_id,outcome,notes) VALUES($t,$id,$o,$n);SELECT last_insert_rowid()";
        cmd.Parameters.AddWithValue("$t",now.ToString("O"));cmd.Parameters.AddWithValue("$o",outcome);cmd.Parameters.AddWithValue("$n",notes);
        var id=Convert.ToInt64(cmd.ExecuteScalar());
        return new(id,now,clusterId,outcome,notes);
    }

    public EvidenceEvent[] RecentEvidence(DateTimeOffset since,int limit=100000)
    {
        limit=Math.Clamp(limit,1,100000);
        using var db=OpenReadOnly();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT payload FROM aw_events WHERE first_seen_at >= $since AND first_seen_at <= $now ORDER BY first_seen_at DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$since",since.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));cmd.Parameters.AddWithValue("$limit",limit+1);
        using var reader=cmd.ExecuteReader();var rows=new List<EvidenceEvent>();
        while(reader.Read())rows.Add(JsonSerializer.Deserialize<EvidenceEvent>(reader.GetString(0))!);
        if(rows.Count>limit)throw new InvalidOperationException($"Operations view exceeds its {limit:N0}-record capacity; no partial coverage was displayed.");
        return rows.ToArray();
    }

    public long FutureDatedEvidenceCount(DateTimeOffset at)
    {
        using var db=OpenReadOnly();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT COUNT(*) FROM aw_events WHERE published_at > $future";
        cmd.Parameters.AddWithValue("$future",at.AddHours(1).ToUniversalTime().ToString("O"));
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public BackupCheck VerifyBackup(string path)
    {
        path=Path.GetFullPath(path);
        if(string.Equals(path,DatabasePath,StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a backup copy, not the live database.");
        if(!File.Exists(path))throw new FileNotFoundException("Backup file not found.",path);
        var builder=new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadOnly,Pooling=false};
        using var db=new SqliteConnection(builder.ToString());db.Open();
        using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA quick_check";
        var result=Convert.ToString(cmd.ExecuteScalar())??"unknown";
        if(result!="ok")throw new InvalidDataException("SQLite verification failed: "+result);
        cmd.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('articles','aw_events','aw_assessments','aw_score_history')";
        if(Convert.ToInt32(cmd.ExecuteScalar())!=4)throw new InvalidDataException("This is not a complete AllianceWatch database.");
        cmd.CommandText="SELECT COUNT(*) FROM articles";var articles=Convert.ToInt64(cmd.ExecuteScalar());
        cmd.CommandText="SELECT COUNT(*) FROM aw_assessments";var assessments=Convert.ToInt64(cmd.ExecuteScalar());
        return new(path,new FileInfo(path).Length,articles,assessments,result);
    }

    public BackupCheck CreateVerifiedBackup(string destination)
    {
        destination=Path.GetFullPath(destination);
        if(string.Equals(destination,DatabasePath,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Choose a different path from the live database.");
        if(File.Exists(destination))throw new IOException("Backup destination already exists; choose a new filename.");
        var directory=Path.GetDirectoryName(destination)!;
        if(!Directory.Exists(directory))throw new DirectoryNotFoundException(directory);
        var temporary=destination+".partial-"+Guid.NewGuid().ToString("N");
        try
        {
            using(var source=OpenReadOnly())
            using(var target=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=temporary,Mode=SqliteOpenMode.ReadWriteCreate,Pooling=false}.ToString()))
            {target.Open();source.BackupDatabase(target);}
            _=VerifyBackup(temporary);
            File.Move(temporary,destination);
            return VerifyBackup(destination);
        }
        finally {if(File.Exists(temporary))File.Delete(temporary);}
    }
}
