namespace AllianceWatch;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--self-test")) { AssessmentTests.Run(); return; }
        if (args.Contains("--benchmark")) { AssessmentBenchmark.Run(); return; }

        // Prefer the working folder so `dotnet run` reuses the existing Python-era database.
        var appDirectory = Directory.GetCurrentDirectory();
        if (!File.Exists(Path.Combine(appDirectory, "config.json")))
            appDirectory = AppContext.BaseDirectory;

        try
        {
            var config = AppConfig.Load(Path.Combine(appDirectory, "config.json"));
            var smoke = args.Contains("--ui-smoke");
            var storage = new Storage(Path.Combine(appDirectory, smoke ? "assessment-ui-smoke.db" : "alliance_watch.db"));
            storage.Initialize();
            storage.MigrateAssessment();
            storage.SaveRules(config.Assessment);
            if(smoke)
            {
                string[] titles=["SYNTHETIC Russia reserve mobilization", "SYNTHETIC Taiwan airspace closure", "SYNTHETIC NATO embassy evacuation", "SYNTHETIC Russia denies reserve mobilization"];
                for(int i=0;i<titles.Length;i++)storage.InsertArticle("smoke-"+i,"SYNTHETIC TEST",titles[i],"https://example.invalid/"+i,DateTimeOffset.UtcNow.AddHours(-i).ToString("O"),"Synthetic local fixture; not a real-world report.");
            }
            var form = new MainForm(appDirectory, config, storage, smoke);
            if (args.Any(a => a.Equals("--test-alert", StringComparison.OrdinalIgnoreCase)))
                form.QueueTestAlert();
            Application.Run(form);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"AllianceWatch could not start.\n\n{ex.Message}",
                "AllianceWatch - Startup Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
