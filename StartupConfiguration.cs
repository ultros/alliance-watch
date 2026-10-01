// SPDX-License-Identifier: LicenseRef-AllianceWatch-Free-Use-No-Resale
// Copyright (c) 2026 Jesse Lee Shelley. All Rights Reserved.
// Free to run; selling or paid access requires Owner's paid written permission.
// See LICENSE and NOTICE for terms and required attribution.
// Creator: https://linkedin.com/in/jesse-shelley
// Repository: https://github.com/ultros/alliance-watch

namespace AllianceWatch;

internal static class StartupConfiguration
{
    internal static string ResolveAppDirectory(string executableDirectory, string workingDirectory)
    {
        var app = Path.GetFullPath(executableDirectory);
        var working = Path.GetFullPath(workingDirectory);
        var relative = Path.GetRelativePath(working, app);
        var firstPart = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        // Development runs reuse the source folder's existing archive. A
        // published app always uses its own folder, regardless of shortcut CWD.
        if (firstPart.Equals("bin", StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(working, "AllianceWatch.csproj"))
            && File.Exists(Path.Combine(working, "config.json"))) return working;
        return app;
    }

    internal static AppConfig Load(string appDirectory)
    {
        var path = Path.Combine(Path.GetFullPath(appDirectory), "config.json");
        if (!File.Exists(path)) CreateDefault(path);
        // Existing settings, including invalid settings, are never replaced.
        return AppConfig.Load(path);
    }

    private static void CreateDefault(string path)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var defaults = typeof(StartupConfiguration).Assembly.GetManifestResourceStream("AllianceWatch.DefaultConfig.json")
                ?? throw new InvalidDataException("The bundled default configuration is missing.");
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                defaults.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            // Publish a complete file atomically, without overwriting settings
            // another instance or the user supplied during startup.
            try { File.Move(temporary, path); }
            catch (IOException) when (File.Exists(path)) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Could not create config.json at '{path}'. Extract the complete ZIP to a writable folder and run AllianceWatch.exe there.", ex);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
