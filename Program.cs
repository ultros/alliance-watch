// SPDX-License-Identifier: LicenseRef-AllianceWatch-Free-Use-No-Resale
// Copyright (c) 2026 Jesse Lee Shelley. All Rights Reserved.
// Free to run; selling or paid access requires Owner's paid written permission.
// See LICENSE and NOTICE for terms and required attribution.
// Creator: https://linkedin.com/in/jesse-shelley
// Repository: https://github.com/ultros/alliance-watch

namespace AllianceWatch;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--self-test")) { AssessmentTests.Run(); return; }
        if (args.Contains("--benchmark")) { AssessmentBenchmark.Run(); return; }

        var appDirectory = StartupConfiguration.ResolveAppDirectory(AppContext.BaseDirectory, Directory.GetCurrentDirectory());

        if (args.Contains("--dedupe-images") || args.Contains("--compress-db"))
        {
            var database = Path.Combine(appDirectory, "alliance_watch.db");
            if (!File.Exists(database)) throw new FileNotFoundException("The archive database was not found.", database);
            var storage = new Storage(database);
            storage.Initialize();
            var result = storage.CompressDatabase(new ConsoleCompressionProgress());
            Console.WriteLine($"Complete. Preserved {result.ImageLinks:N0} article-image links to {result.UniqueImages:N0} unique images.");
            Console.WriteLine($"Database: {result.BeforeBytes:N0} → {result.AfterBytes:N0} bytes. Backup: {result.BackupPath}");
            return;
        }

        try
        {
            var config = StartupConfiguration.Load(appDirectory);
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

    private sealed class ConsoleCompressionProgress : IProgress<DatabaseCompressionProgress>
    {
        public void Report(DatabaseCompressionProgress progress)
        {
            if (progress.Total == 0 || progress.Processed == 0 || progress.Processed == progress.Total || progress.Processed % 500 == 0)
                Console.WriteLine(progress.Total > 0 ? $"{progress.Stage}: {progress.Processed:N0}/{progress.Total:N0}" : progress.Stage + "…");
        }
    }
}
