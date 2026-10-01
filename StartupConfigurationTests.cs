// SPDX-License-Identifier: LicenseRef-AllianceWatch-Free-Use-No-Resale
// Copyright (c) 2026 Jesse Lee Shelley. All Rights Reserved.
// Free to run; selling or paid access requires Owner's paid written permission.
// See LICENSE and NOTICE for terms and required attribution.
// Creator: https://linkedin.com/in/jesse-shelley
// Repository: https://github.com/ultros/alliance-watch

using System.Text.Json;

namespace AllianceWatch;

internal static class StartupConfigurationTests
{
    internal static void VerifyLaunchDirectory() => InFolder(folder =>
    {
        File.WriteAllText(Path.Combine(folder, "config.json"), "{}");
        File.WriteAllText(Path.Combine(folder, "AllianceWatch.csproj"), "<Project />");
        var published = Path.Combine(folder, "release-next");
        var development = Path.Combine(folder, "bin", "Release", "net8.0-windows");
        Require(StartupConfiguration.ResolveAppDirectory(published, folder) == published,
            "A published app must not select a different archive just because its working folder has a configuration.");
        Require(StartupConfiguration.ResolveAppDirectory(development, folder) == folder,
            "A development run must preserve the source folder's existing archive.");
    });

    internal static void VerifyMissingConfiguration() => InFolder(folder =>
    {
        var database = Path.Combine(folder, "alliance_watch.db");
        byte[] existing = [10, 20, 30, 40];
        File.WriteAllBytes(database, existing);
        var config = StartupConfiguration.Load(folder);
        Require(config.Feeds.Count >= 56, "Recovery must use the full bundled feed catalog.");
        Require(AppConfig.Load(Path.Combine(folder, "config.json")).Feeds.Count == config.Feeds.Count, "Recovered configuration must be complete and readable.");
        Require(File.ReadAllBytes(database).SequenceEqual(existing), "Configuration recovery must not touch an existing database.");
    });

    internal static void VerifyExistingConfiguration() => InFolder(folder =>
    {
        var path = Path.Combine(folder, "config.json");
        var custom = new AppConfig { PollMinutes = 37, ArchiveEnabled = false, Feeds = [new() { Name = "Custom feed", Url = "https://example.invalid/custom" }] };
        File.WriteAllText(path, JsonSerializer.Serialize(custom));
        var before = File.ReadAllBytes(path);
        var config = StartupConfiguration.Load(folder);
        Require(config.PollMinutes == 37 && !config.ArchiveEnabled && config.Feeds.Single().Name == "Custom feed", "Existing settings must take precedence over defaults.");
        Require(File.ReadAllBytes(path).SequenceEqual(before), "Startup must never overwrite existing settings.");
    });

    internal static void VerifyInvalidConfiguration() => InFolder(folder =>
    {
        var path = Path.Combine(folder, "config.json");
        File.WriteAllText(path, "{invalid settings");
        bool rejected = false;
        try { StartupConfiguration.Load(folder); } catch (JsonException) { rejected = true; }
        Require(rejected && File.ReadAllText(path) == "{invalid settings", "Malformed user settings must be reported and retained, not reset.");
    });

    internal static void VerifyConcurrentStartup() => InFolder(folder =>
    {
        Parallel.For(0, 12, _ => Require(StartupConfiguration.Load(folder).Feeds.Count >= 56, "Concurrent startup must never read a partially written configuration."));
        Require(Directory.GetFiles(folder, "*.tmp").Length == 0, "Configuration recovery must remove its temporary files.");
    });

    private static void InFolder(Action<string> test)
    {
        var folder = Path.Combine(Path.GetTempPath(), "aw-startup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try { test(folder); }
        finally { foreach (var file in Directory.GetFiles(folder)) File.Delete(file); Directory.Delete(folder); }
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
