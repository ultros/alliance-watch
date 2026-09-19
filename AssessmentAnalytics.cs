namespace AllianceWatch;

internal sealed record BaselineResult(string Window,int ObservedDays,double Current,double? Mean,double? StandardDeviation,double? ZScore,double? Percentile,string Coverage);
internal sealed record RateOfChange(string Window,double Current,double? Prior,double? Delta,double? VelocityPerHour,double? AccelerationPerHourSquared);
internal static class AssessmentAnalytics
{
    public static BaselineResult[] Baselines(Assessment[] history,DateTimeOffset at)
    {
        var current=history.Where(a=>a.Timestamp<=at).OrderByDescending(a=>a.Timestamp).FirstOrDefault()?.Risk??0;
        return new[]{("30D",30),("90D",90),("1Y",365),("MULTI-YEAR",1095)}.Select(w=>
        {
            var dayStart=new DateTimeOffset(at.UtcDateTime.Date,TimeSpan.Zero);
            var days=history.Where(a=>a.Timestamp<dayStart && a.Timestamp>=at.AddDays(-w.Item2)).GroupBy(a=>a.Timestamp.UtcDateTime.Date).Select(g=>g.Average(a=>a.Risk)).ToArray();
            if(days.Length<7)return new BaselineResult(w.Item1,days.Length,current,null,null,null,null,"INSUFFICIENT OBSERVED DAYS");
            var mean=days.Average();var sd=Math.Sqrt(days.Select(x=>Math.Pow(x-mean,2)).Average());
            return new BaselineResult(w.Item1,days.Length,current,mean,sd,sd>0?(current-mean)/sd:null,100d*days.Count(x=>x<=current)/days.Length,$"{days.Length}/{w.Item2} days; missing days excluded");
        }).ToArray();
    }
    public static RateOfChange[] Rates(Assessment current,Assessment[] history)=>new[]{("6H",6), ("24H",24),("72H",72),("7D",168),("30D",720)}.Select(w=>
    {
        var prior=history.Where(a=>a.Timestamp<=current.Timestamp.AddHours(-w.Item2)).OrderByDescending(a=>a.Timestamp).FirstOrDefault();
        var earlier=prior==null?null:history.Where(a=>a.Timestamp<=prior.Timestamp.AddHours(-w.Item2)).OrderByDescending(a=>a.Timestamp).FirstOrDefault();
        double? velocity=prior==null?null:(current.Risk-prior.Risk)/(current.Timestamp-prior.Timestamp).TotalHours;
        double? oldVelocity=earlier==null?null:(prior!.Risk-earlier.Risk)/(prior.Timestamp-earlier.Timestamp).TotalHours;
        return new RateOfChange(w.Item1,current.Risk,prior?.Risk,prior==null?null:current.Risk-prior.Risk,velocity,oldVelocity==null?null:(velocity-oldVelocity)/(current.Timestamp-prior!.Timestamp).TotalHours);
    }).ToArray();
}
