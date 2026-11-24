using System.IO;
using System.Runtime.InteropServices;
using LogiPluginBackup.Core;

namespace LogiPluginBackup.App;

/// <summary>
/// Command line mode:
///   LogiPluginBackup list
///   LogiPluginBackup backup [file.lpbak] [--no-config]
///   LogiPluginBackup restore file.lpbak [--no-config] [--no-stop] [--yes]
/// The program is a GUI application: output goes to the console it was started from. In PowerShell use
/// Start-Process -Wait -NoNewWindow, or the exit code: 0 = success, 1 = error, 2 = bad arguments.
/// </summary>
internal static class Cli
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    public static int Run(string[] args)
    {
        AttachConsole(-1);
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(stdout);
        var log = new Progress<string>(s => stdout.WriteLine(s));
        var paths = new LogiPaths();

        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "list":
                    foreach (var p in Inventory.ScanPlugins(paths))
                        stdout.WriteLine($"{p.DisplayName,-40} {p.Version,-14} {p.Kind,-9} {p.Author}");
                    foreach (var c in Inventory.ScanConfig(paths))
                        stdout.WriteLine($"[config] {c.Name}");
                    return 0;

                case "backup":
                {
                    var file = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"))
                        ?? $"LogiPlugins_{DateTime.Now:yyyy-MM-dd_HHmm}.lpbak";
                    var withConfig = !args.Contains("--no-config");
                    var sel = new BackupSelection(
                        Inventory.ScanPlugins(paths).Select(p => p.Key).ToList(),
                        withConfig ? Inventory.ScanConfig(paths).Where(c => c.DefaultSelected).Select(c => c.Name).ToList() : []);
                    return Report(BackupService.Create(paths, Path.GetFullPath(file), sel, log), stdout);
                }

                case "restore":
                {
                    var file = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
                    if (file is null) { stdout.WriteLine("Usage : restore <fichier.lpbak> [--no-config] [--no-stop] [--yes]"); return 2; }
                    using var archive = BackupArchive.Open(Path.GetFullPath(file));
                    if (!args.Contains("--yes"))
                    {
                        stdout.WriteLine($"Restaurer {archive.Manifest.Plugins.Count} plugin(s) et {archive.Manifest.Configs.Count} réglage(s) ? Relancez avec --yes pour confirmer.");
                        return 2;
                    }
                    var sel = new BackupSelection(
                        archive.Manifest.Plugins.Select(p => p.Key).ToList(),
                        args.Contains("--no-config") ? [] : archive.Manifest.Configs);
                    var opt = new RestoreOptions(StopService: !args.Contains("--no-stop"));
                    return Report(RestoreService.Restore(paths, archive, sel, opt, new PluginServiceControl(), log), stdout);
                }

                default:
                    stdout.WriteLine("Commandes : list | backup [fichier] [--no-config] | restore <fichier> [--no-config] [--no-stop] [--yes]");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            stdout.WriteLine("Erreur : " + ex.Message);
            return 1;
        }
    }

    private static int Report(OperationResult r, TextWriter w)
    {
        foreach (var d in r.Done) w.WriteLine("  OK  " + d);
        foreach (var x in r.Warnings) w.WriteLine("  !   " + x);
        foreach (var e in r.Errors) w.WriteLine("  ERREUR " + e);
        if (r.SafetyCopy is not null) w.WriteLine("Copie de sécurité : " + r.SafetyCopy);
        w.WriteLine(r.Success ? "Terminé." : "Échec.");
        return r.Success ? 0 : 1;
    }
}
