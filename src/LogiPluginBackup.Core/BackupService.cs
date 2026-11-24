using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace LogiPluginBackup.Core;

/// <summary>
/// Creates a .lpbak archive (a standard zip):
///   manifest.json                versions, author, SHA-256 of every file
///   plugins/&lt;folder&gt;/...         one folder per plugin, same layout as in the Plugins folder
///   config/&lt;name&gt;/...            profiles, plugin data, snapshots, media
///   config/LoupedeckSettings.ini
/// Logi Options+ can stay open: files are read with shared access.
/// </summary>
public static class BackupService
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static OperationResult Create(LogiPaths paths, string destination, BackupSelection selection, IProgress<string>? log = null)
    {
        var result = new OperationResult { File = destination };
        var manifest = new BackupManifest
        {
            CreatedUtc = DateTime.UtcNow,
            Machine = Environment.MachineName,
            User = Environment.UserName,
            PluginServiceVersion = paths.PluginServiceVersion,
        };

        var plugins = Inventory.ScanPlugins(paths).Where(p => selection.PluginKeys.Contains(p.Key)).ToList();
        var temp = destination + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        if (File.Exists(temp))
        {
            File.Delete(temp);
        }

        try
        {
            using (var zipStream = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite))
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                foreach (var plugin in plugins)
                {
                    var source = plugin.IsDevLink ? plugin.LinkTarget : Path.Combine(paths.PluginsDir, plugin.Key);
                    log?.Report($"Plugin {plugin.DisplayName} {plugin.Version}...");
                    var count = AddDirectory(zip, manifest, result, source, $"plugins/{plugin.Key}");
                    manifest.Plugins.Add(new ManifestPlugin
                    {
                        Key = plugin.Key, Name = plugin.Name, DisplayName = plugin.DisplayName, Version = plugin.Version,
                        Author = plugin.Author, SizeBytes = plugin.SizeBytes, FromDevLink = plugin.IsDevLink, LinkTarget = plugin.LinkTarget,
                    });
                    result.Done.Add($"{plugin.DisplayName} {plugin.Version} ({count} fichiers)");
                }

                foreach (var cfg in LogiPaths.ConfigItems.Where(c => selection.ConfigNames.Contains(c.Name)))
                {
                    var source = paths.ConfigPath(cfg.Name);
                    if (cfg.IsDirectory ? !Directory.Exists(source) : !File.Exists(source))
                    {
                        continue;
                    }
                    log?.Report($"Configuration : {cfg.Name}...");
                    var count = cfg.IsDirectory
                        ? AddDirectory(zip, manifest, result, source, $"config/{cfg.Name}")
                        : AddFile(zip, manifest, result, source, $"config/{cfg.Name}");
                    manifest.Configs.Add(cfg.Name);
                    result.Done.Add($"{cfg.Label} ({count} fichiers)");
                }

                var entry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
                using var writer = entry.Open();
                JsonSerializer.Serialize(writer, manifest, Json);
            }

            File.Move(temp, destination, overwrite: true);
            log?.Report($"Sauvegarde créée : {destination}");
        }
        catch (Exception ex)
        {
            result.Errors.Add(ex.Message);
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
        return result;
    }

    private static int AddDirectory(ZipArchive zip, BackupManifest manifest, OperationResult result, string sourceDir, string prefix)
    {
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
            count += AddFile(zip, manifest, result, file, $"{prefix}/{rel}");
        }
        return count;
    }

    /// <summary>Adds one file, hashing it while it is written. Returns 1 when added, 0 when it could not be read.</summary>
    private static int AddFile(ZipArchive zip, BackupManifest manifest, OperationResult result, string file, string entryName)
    {
        try
        {
            using var input = FileUtil.OpenShared(file);
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(File.GetLastWriteTime(file));
            using var output = entry.Open();
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha.AppendData(buffer, 0, read);
                output.Write(buffer, 0, read);
            }
            manifest.Files[entryName] = Convert.ToHexString(sha.GetHashAndReset());
            return 1;
        }
        catch (IOException ex)
        {
            // A file locked exclusively by another program: skipped and reported, the backup goes on.
            result.Warnings.Add($"Ignoré (verrouillé) : {file} - {ex.Message}");
            return 0;
        }
        catch (UnauthorizedAccessException ex)
        {
            result.Warnings.Add($"Ignoré (accès refusé) : {file} - {ex.Message}");
            return 0;
        }
    }
}
