using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

namespace LogiPluginBackup.Core;

/// <summary>Stops and waits for the Logi Plugin Service. Replaced by a fake in tests.</summary>
public interface IServiceControl
{
    /// <summary>Stops the plugin service processes. Returns false when some could not be stopped.</summary>
    bool Stop(IProgress<string>? log);

    /// <summary>Waits until the service is running again (Options+ restarts it). Returns false on timeout.</summary>
    bool WaitUntilBack(TimeSpan timeout, IProgress<string>? log);
}

/// <summary>
/// The Logi Plugin Service (LogiPluginService.exe and its host LogiPluginServiceExt.exe) runs under the user account:
/// no administrator rights needed. Logi Options+ starts it again by itself a few seconds after it stops.
/// </summary>
public sealed class PluginServiceControl : IServiceControl
{
    private static readonly string[] ProcessNames = ["LogiPluginServiceExt", "LogiPluginService"];

    public bool Stop(IProgress<string>? log)
    {
        foreach (var name in ProcessNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    log?.Report($"Arrêt de {name} (PID {p.Id})");
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(10000);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    log?.Report($"Impossible d'arrêter {name} : {ex.Message}");
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        return ProcessNames.All(n => Process.GetProcessesByName(n).Length == 0);
    }

    public bool WaitUntilBack(TimeSpan timeout, IProgress<string>? log)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (Process.GetProcessesByName("LogiPluginService").Length > 0)
            {
                log?.Report("Le service de plugins est reparti.");
                return true;
            }
            Thread.Sleep(1000);
        }
        return false;
    }
}

/// <summary>An opened .lpbak file: manifest, integrity check and extraction helpers.</summary>
public sealed class BackupArchive : IDisposable
{
    private readonly ZipArchive _zip;

    private BackupArchive(string path, ZipArchive zip, BackupManifest manifest)
    {
        Path = path;
        _zip = zip;
        Manifest = manifest;
    }

    public string Path { get; }

    public BackupManifest Manifest { get; }

    public static BackupArchive Open(string path)
    {
        var zip = ZipFile.OpenRead(path);
        try
        {
            var entry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("manifest.json absent : ce n'est pas une sauvegarde LogiPluginBackup.");
            BackupManifest? manifest;
            using (var s = entry.Open())
            {
                manifest = JsonSerializer.Deserialize<BackupManifest>(s);
            }
            if (manifest is null || manifest.Format != BackupManifest.CurrentFormat)
            {
                throw new InvalidDataException($"Format de sauvegarde non pris en charge : '{manifest?.Format}'.");
            }
            // Every path must stay inside the folder it is extracted to.
            var bad = zip.Entries.FirstOrDefault(e => !FileUtil.IsSafeEntryName(e.FullName));
            if (bad is not null)
            {
                throw new InvalidDataException($"Chemin dangereux dans l'archive : '{bad.FullName}'. Sauvegarde refusée.");
            }
            return new BackupArchive(path, zip, manifest);
        }
        catch
        {
            zip.Dispose();
            throw;
        }
    }

    /// <summary>Compares every file with the SHA-256 stored in the manifest. Returns the problems found.</summary>
    public List<string> Verify(IProgress<string>? log = null)
    {
        var problems = new List<string>();
        foreach (var (name, expected) in Manifest.Files)
        {
            var entry = _zip.GetEntry(name);
            if (entry is null)
            {
                problems.Add($"Fichier manquant : {name}");
                continue;
            }
            try
            {
                using var s = entry.Open();
                var actual = FileUtil.Sha256Hex(s);
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"Fichier modifié ou corrompu : {name}");
                }
            }
            catch (InvalidDataException ex)
            {
                problems.Add($"Fichier illisible : {name} ({ex.Message})");
            }
        }
        var extra = _zip.Entries.Where(e => e.Length > 0 && e.FullName != "manifest.json" && !Manifest.Files.ContainsKey(e.FullName));
        problems.AddRange(extra.Select(e => $"Fichier non déclaré dans le manifeste : {e.FullName}"));
        log?.Report(problems.Count == 0 ? "Intégrité vérifiée : aucune différence." : $"{problems.Count} problème(s) d'intégrité.");
        return problems;
    }

    /// <summary>Extracts every entry under <paramref name="prefix"/> into <paramref name="destinationDir"/>.</summary>
    internal int ExtractPrefix(string prefix, string destinationDir)
    {
        var root = System.IO.Path.GetFullPath(destinationDir) + System.IO.Path.DirectorySeparatorChar;
        var count = 0;
        foreach (var entry in _zip.Entries.Where(e => e.FullName.StartsWith(prefix + "/", StringComparison.Ordinal) && e.Length >= 0 && !e.FullName.EndsWith('/')))
        {
            var target = System.IO.Path.GetFullPath(System.IO.Path.Combine(destinationDir, entry.FullName[(prefix.Length + 1)..]));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Chemin dangereux : {entry.FullName}");
            }
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
            File.SetLastWriteTime(target, entry.LastWriteTime.LocalDateTime);
            count++;
        }
        return count;
    }

    internal void ExtractFile(string entryName, string target)
    {
        var entry = _zip.GetEntry(entryName) ?? throw new FileNotFoundException(entryName);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        entry.ExtractToFile(target, overwrite: true);
    }

    public void Dispose() => _zip.Dispose();
}

public sealed record RestoreOptions(bool StopService = true, bool VerifyFirst = true);

public static class RestoreService
{
    /// <summary>
    /// Restores the chosen plugins and configuration sets.
    /// Order: verify the archive, copy what will be overwritten to a safety folder, stop the service,
    /// replace folders, let the service come back.
    /// </summary>
    public static OperationResult Restore(
        LogiPaths paths, BackupArchive archive, BackupSelection selection, RestoreOptions options,
        IServiceControl service, IProgress<string>? log = null)
    {
        var result = new OperationResult { File = archive.Path };
        try
        {
            if (options.VerifyFirst)
            {
                var problems = archive.Verify(log);
                if (problems.Count > 0)
                {
                    result.Errors.AddRange(problems.Select(p => "Archive invalide : " + p));
                    return result; // nothing has been touched
                }
            }

            var plugins = archive.Manifest.Plugins.Where(p => selection.PluginKeys.Contains(p.Key)).ToList();
            var configs = LogiPaths.ConfigItems.Where(c => selection.ConfigNames.Contains(c.Name) && archive.Manifest.Configs.Contains(c.Name)).ToList();

            // Same-name dev link already on this PC: restoring a copy would load the plugin twice.
            var devLinked = Inventory.ScanPlugins(paths).Where(x => x.IsDevLink)
                .Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var p in plugins.Where(p => devLinked.Contains(p.Key)))
            {
                result.Warnings.Add($"{p.DisplayName} ignoré : ce PC le charge déjà par un lien de développement (.link). Supprimez le lien pour installer la copie.");
            }
            plugins = plugins.Where(p => !devLinked.Contains(p.Key)).ToList();

            // 1. safety copy of everything that is about to be replaced
            var safety = Path.Combine(LogiPaths.SafetyRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            foreach (var p in plugins)
            {
                var existing = Path.Combine(paths.PluginsDir, p.Key);
                if (Directory.Exists(existing))
                {
                    FileUtil.CopyDirectoryShared(existing, Path.Combine(safety, "plugins", p.Key));
                    result.SafetyCopy = safety;
                }
            }
            foreach (var c in configs)
            {
                var existing = paths.ConfigPath(c.Name);
                if (c.IsDirectory && Directory.Exists(existing))
                {
                    FileUtil.CopyDirectoryShared(existing, Path.Combine(safety, "config", c.Name));
                    result.SafetyCopy = safety;
                }
                else if (!c.IsDirectory && File.Exists(existing))
                {
                    FileUtil.CopyShared(existing, Path.Combine(safety, "config", c.Name));
                    result.SafetyCopy = safety;
                }
            }
            if (result.SafetyCopy is not null)
            {
                log?.Report($"Copie de sécurité : {result.SafetyCopy}");
            }

            // 2. stop the service: its host keeps plugin DLLs and LoupedeckSettings.ini open
            var stopped = false;
            if (options.StopService)
            {
                stopped = service.Stop(log);
                if (!stopped)
                {
                    result.Errors.Add("Le service de plugins n'a pas pu être arrêté. Fermez Logi Options+ puis recommencez.");
                    return result;
                }
            }

            // 3. replace (delete first so files removed since the backup do not linger)
            Directory.CreateDirectory(paths.PluginsDir);
            foreach (var p in plugins)
            {
                log?.Report($"Restauration du plugin {p.DisplayName}...");
                var target = Path.Combine(paths.PluginsDir, p.Key);
                FileUtil.DeleteDirectoryWithRetry(target);
                var count = archive.ExtractPrefix($"plugins/{p.Key}", target);
                result.Done.Add($"{p.DisplayName} {p.Version} ({count} fichiers)");
            }
            foreach (var c in configs)
            {
                log?.Report($"Restauration de {c.Name}...");
                var target = paths.ConfigPath(c.Name);
                if (c.IsDirectory)
                {
                    FileUtil.DeleteDirectoryWithRetry(target);
                    var count = archive.ExtractPrefix($"config/{c.Name}", target);
                    result.Done.Add($"{c.Label} ({count} fichiers)");
                }
                else
                {
                    archive.ExtractFile($"config/{c.Name}", target);
                    result.Done.Add(c.Label);
                }
            }

            // 4. Options+ starts the service again by itself
            if (stopped && !service.WaitUntilBack(TimeSpan.FromSeconds(30), log))
            {
                result.Warnings.Add("Le service de plugins n'est pas reparti tout seul : relancez Logi Options+ (ou redémarrez le PC).");
            }
        }
        catch (Exception ex)
        {
            result.Errors.Add(ex.Message);
        }
        return result;
    }
}
