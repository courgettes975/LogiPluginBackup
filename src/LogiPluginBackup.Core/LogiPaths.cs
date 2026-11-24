namespace LogiPluginBackup.Core;

/// <summary>Where the Logi Plugin Service keeps its data. The root can be replaced (tests, other profile).</summary>
public sealed class LogiPaths
{
    public LogiPaths(string? root = null)
    {
        // LOGIPLUGINBACKUP_ROOT lets you try a restore on a scratch folder instead of the real install.
        Root = root ?? Environment.GetEnvironmentVariable("LOGIPLUGINBACKUP_ROOT") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Logi", "LogiPluginService");
    }

    public string Root { get; }

    public string PluginsDir => Path.Combine(Root, "Plugins");

    /// <summary>Folder where the previous state is copied before every restore.</summary>
    public static string SafetyRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LogiPluginBackup", "pre-restore");

    /// <summary>
    /// Configuration that goes with the plugins. Logs, Temp and PluginHosts (caches rebuilt by the service)
    /// are deliberately not part of it.
    /// </summary>
    public static readonly ConfigItem[] ConfigItems =
    [
        new("Applications", "Profils et dispositions des touches (par application)", true),
        new("PluginData", "Données des plugins", true),
        new("Snapshots", "Snapshots du service", true),
        new("Media", "Icônes et médias", true),
        new("LoupedeckSettings.ini", "Réglages généraux du service", false),
        new("Applications.Backups", "Copies automatiques du service (volumineux)", true, DefaultSelected: false),
    ];

    public string ConfigPath(string name) => Path.Combine(Root, name);

    public string PluginServiceVersion
    {
        get
        {
            try
            {
                var exe = @"C:\Program Files\Logi\LogiPluginService\LogiPluginService.exe";
                return File.Exists(exe) ? System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).FileVersion ?? "" : "";
            }
            catch
            {
                return "";
            }
        }
    }
}
