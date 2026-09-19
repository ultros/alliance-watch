using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AllianceWatch;

internal sealed record FeedEntry(string Title,string Summary,string Url,string Guid,string Published);
internal interface ISourceAdapter { FeedEntry[] Parse(string content,FeedConfig config); }
internal static class SourceAdapters
{
    public static ISourceAdapter For(string name) => name.ToLowerInvariant() switch
    {
        "rss" or "atom" => new XmlSourceAdapter(),
        "json" or "gdelt" or "acled" or "ucdp" => new JsonSourceAdapter(),
        "csv" => new CsvSourceAdapter(),
        "watch" => new WatchSourceAdapter(),
        _ => throw new InvalidDataException("Unknown source adapter: "+name)
    };
}
internal sealed class XmlSourceAdapter : ISourceAdapter
{
    public FeedEntry[] Parse(string content,FeedConfig config)
    {
        XDocument Read(string text)
        {
            using var input=new StringReader(text);
            using var reader=XmlReader.Create(input,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=8*1024*1024});
            return XDocument.Load(reader);
        }
        XDocument document;
        try{document=Read(content);}catch(XmlException)
        {
            // Recover only bare ampersands. Structural errors and DTDs remain failures.
            document=Read(Regex.Replace(content,@"&(?!amp;|lt;|gt;|quot;|apos;|#\d+;|#x[0-9a-fA-F]+;)","&amp;"));
        }
        return document.Descendants().Where(x=>x.Name.LocalName is "item" or "entry").Take(5000).Select(item=>
        {
            string Value(params string[] names)=>item.Elements().FirstOrDefault(x=>names.Contains(x.Name.LocalName))?.Value??"";
            var link=item.Elements().FirstOrDefault(x=>x.Name.LocalName=="link" && ((string?)x.Attribute("rel") is null or "alternate"));
            return new FeedEntry(Value("title"),string.Join(" ",new[]{Value("summary"),Value("description"),Value("content","encoded")}.Where(x=>x.Length>0)),link?.Attribute("href")?.Value??link?.Value??"",Value("id","guid"),Value("published","pubDate","updated"));
        }).ToArray();
    }
}
internal sealed class JsonSourceAdapter : ISourceAdapter
{
    public FeedEntry[] Parse(string content,FeedConfig config)
    {
        using var doc=JsonDocument.Parse(content,new JsonDocumentOptions{MaxDepth=64});var array=doc.RootElement;
        var path=config.ItemsPath.Length>0?config.ItemsPath:config.Adapter switch{"gdelt"=>"articles","acled"=>"data","ucdp"=>"Result",_=>""};
        foreach(var part in path.Split('.',StringSplitOptions.RemoveEmptyEntries))array=array.GetProperty(part);
        if(array.ValueKind!=JsonValueKind.Array)throw new InvalidDataException("JSON items_path must point to an array.");
        return array.EnumerateArray().Take(5000).Select(item=>
        {
            string Field(string name)
            {
                var value=item;foreach(var part in config.FieldMap.GetValueOrDefault(name,name).Split('.'))if(!value.TryGetProperty(part,out value))return "";
                return value.ValueKind==JsonValueKind.String?value.GetString()??"":value.ToString();
            }
            return new FeedEntry(Field("title"),Field("summary"),Field("url"),Field("id"),Field("published"));
        }).ToArray();
    }
}
internal sealed class CsvSourceAdapter : ISourceAdapter
{
    public FeedEntry[] Parse(string content,FeedConfig config)
    {
        var rows=Rows(content).ToArray();if(rows.Length==0)return [];
        var headers=rows[0];return rows.Skip(1).Take(5000).Select(row=>
        {
            string Field(string name){int index=Array.FindIndex(headers,h=>h.Equals(config.FieldMap.GetValueOrDefault(name,name),StringComparison.OrdinalIgnoreCase));return index>=0&&index<row.Length?row[index]:"";}
            return new FeedEntry(Field("title"),Field("summary"),Field("url"),Field("id"),Field("published"));
        }).ToArray();
    }
    private static IEnumerable<string[]> Rows(string text)
    {
        var row=new List<string>();var field=new StringBuilder();bool quoted=false;
        for(int i=0;i<text.Length;i++)
        {
            char c=text[i];if(c=='"'){if(quoted&&i+1<text.Length&&text[i+1]=='"'){field.Append('"');i++;}else quoted=!quoted;}
            else if(c==','&&!quoted){row.Add(field.ToString());field.Clear();}
            else if((c=='\r'||c=='\n')&&!quoted){if(c=='\r'&&i+1<text.Length&&text[i+1]=='\n')i++;row.Add(field.ToString());field.Clear();yield return row.ToArray();row.Clear();}
            else field.Append(c);
        }
        if(quoted)throw new InvalidDataException("Unclosed CSV quotation.");
        if(field.Length>0||row.Count>0){row.Add(field.ToString());yield return row.ToArray();}
    }
}
internal sealed class WatchSourceAdapter : ISourceAdapter
{
    public FeedEntry[] Parse(string content,FeedConfig config)
    {
        var title=Regex.Match(content,@"<title[^>]*>(.*?)</title>",RegexOptions.IgnoreCase|RegexOptions.Singleline,TimeSpan.FromMilliseconds(100)).Groups[1].Value;
        var text=Regex.Replace(content,@"<(script|style)\b[^>]*>.*?</\1>"," ",RegexOptions.IgnoreCase|RegexOptions.Singleline,TimeSpan.FromMilliseconds(100));
        text=System.Net.WebUtility.HtmlDecode(Regex.Replace(text,"<[^>]+>"," "));text=Regex.Replace(text,@"\s+"," ").Trim();
        return [new(title.Length>0?title:config.Name,text[..Math.Min(text.Length,20000)],config.Url,AssessmentEngine.Hash(content),DateTimeOffset.UtcNow.ToString("O"))];
    }
}
