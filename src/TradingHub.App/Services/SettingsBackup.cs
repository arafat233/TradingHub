namespace TradingHub.App.Services;

/// <summary>
/// Copies the settings files (connections, workspace, settings) to <c>Backups\yyyyMMdd-HHmmss</c> at startup when
/// they changed since the last copy, and keeps the newest few: a bad write is then one file copy away from fixed.
/// </summary>
public static class SettingsBackup
{
    private static readonly string[] Files = { "connections.json", "workspace.json", "settings.json" };
    private const int Keep = 10;

    private static string BackupsRoot => Path.Combine(AppPaths.Root, "Backups");
    private static string PendingFile => Path.Combine(AppPaths.Root, "restore-pending.txt");

    /// <summary>Backup names, newest first.</summary>
    public static IReadOnlyList<string> List() =>
        Directory.Exists(BackupsRoot) ? Directory.GetDirectories(BackupsRoot).Select(Path.GetFileName).OfType<string>().OrderByDescending(n => n, StringComparer.Ordinal).ToList() : new List<string>();

    /// <summary>
    /// Asks for a backup to be put back at the next start. It is not applied now: the running app writes its settings
    /// again when it closes, which would undo the restore.
    /// </summary>
    public static void RequestRestore(string name) => File.WriteAllText(PendingFile, name);

    private static void ApplyPending()
    {
        if (!File.Exists(PendingFile)) return;
        var name = File.ReadAllText(PendingFile).Trim();
        File.Delete(PendingFile);
        var from = Path.Combine(BackupsRoot, name);
        if (name.Length == 0 || name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar) || !Directory.Exists(from)) return;
        // what is being replaced is kept too, as a backup of its own
        var keep = Path.Combine(BackupsRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-before-restore");
        Directory.CreateDirectory(keep);
        foreach (var f in Files)
        {
            var current = Path.Combine(AppPaths.Root, f);
            if (File.Exists(current)) File.Copy(current, Path.Combine(keep, f), true);
            var saved = Path.Combine(from, f);
            if (File.Exists(saved)) File.Copy(saved, current, true);
        }
    }

    public static void Run()
    {
        try
        {
            ApplyPending();
            var root = BackupsRoot;
            var sets = Directory.Exists(root) ? Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal).ToList() : new List<string>();
            var last = sets.LastOrDefault();
            bool changed = false;
            foreach (var f in Files)
            {
                var src = Path.Combine(AppPaths.Root, f);
                if (!File.Exists(src)) continue;
                var prev = last == null ? null : Path.Combine(last, f);
                if (prev == null || !File.Exists(prev) || !File.ReadAllBytes(src).AsSpan().SequenceEqual(File.ReadAllBytes(prev))) changed = true;
            }

            if (!changed) return;
            var dir = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(dir);
            foreach (var f in Files)
            {
                var src = Path.Combine(AppPaths.Root, f);
                if (File.Exists(src)) File.Copy(src, Path.Combine(dir, f));
            }

            sets.Add(dir);
            foreach (var old in sets.Take(Math.Max(0, sets.Count - Keep))) Directory.Delete(old, true);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex); // a failed backup must never stop the app from starting
        }
    }
}
