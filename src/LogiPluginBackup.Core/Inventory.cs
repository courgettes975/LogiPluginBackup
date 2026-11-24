namespace LogiPluginBackup.Core;

/// <summary>Lists the plugins installed in the Logi Plugin Service folder (read-only).</summary>
public static class Inventory
{
    public static List<PluginInfo> ScanPlugins(LogiPaths paths)
    {
        var list = new List<PluginInfo>();
        if (!Directory.Exists(paths.PluginsDir))
        {
            return list;
        }

        // Plugins installed from the Marketplace or from a .lplug4: one folder each.
        foreach (var dir in Directory.EnumerateDirectories(paths.PluginsDir))
        {
            var folder = Path.GetFileName(dir);
            var meta = ReadMetadata(Path.Combine(dir, "metadata", "LoupedeckPackage.yaml"));
            list.Add(new PluginInfo(folder, meta.GetValueOrDefault("name", folder),
                meta.GetValueOrDefault("displayName", folder), meta.GetValueOrDefault("version", ""),
                meta.GetValueOrDefault("author", ""), FileUtil.DirectorySize(dir), false, ""));
        }

        // Development links: a .link file holds the path of a build folder (bin\Release\...).
        // The build folder has the same layout as an installed plugin, so it is stored as a plugin.
        foreach (var link in Directory.EnumerateFiles(paths.PluginsDir, "*.link"))
        {
            var target = SafeRead(link).Trim().TrimEnd('\\', '/');
            var linkName = Path.GetFileNameWithoutExtension(link);
            if (target.Length == 0 || !Directory.Exists(target))
            {
                continue; // dangling link: nothing to save
            }
            var meta = ReadMetadata(Path.Combine(target, "metadata", "LoupedeckPackage.yaml"));
            var name = meta.GetValueOrDefault("name", linkName);
            if (list.Any(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                continue; // an installed copy with the same name is already listed
            }
            list.Add(new PluginInfo(name, name, meta.GetValueOrDefault("displayName", name),
                meta.GetValueOrDefault("version", ""), meta.GetValueOrDefault("author", ""),
                FileUtil.DirectorySize(target), true, target));
        }

        return list.OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static List<ConfigItem> ScanConfig(LogiPaths paths) =>
        LogiPaths.ConfigItems.Where(c => c.IsDirectory ? Directory.Exists(paths.ConfigPath(c.Name)) : File.Exists(paths.ConfigPath(c.Name))).ToList();

    /// <summary>Reads the top-level "key: value" lines of a LoupedeckPackage.yaml (no YAML library needed).</summary>
    public static Dictionary<string, string> ReadMetadata(string yamlPath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(yamlPath))
        {
            return result;
        }
        foreach (var raw in SafeRead(yamlPath).Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] == '#' || line[0] == '-')
            {
                continue;
            }
            var i = line.IndexOf(':');
            if (i <= 0)
            {
                continue;
            }
            var key = line[..i].Trim();
            var value = line[(i + 1)..].Trim().Trim('\'', '"');
            if (value.Length > 0 && !result.ContainsKey(key))
            {
                result[key] = value;
            }
        }
        return result;
    }

    private static string SafeRead(string path)
    {
        try
        {
            using var s = FileUtil.OpenShared(path);
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }
        catch (IOException)
        {
            return "";
        }
    }
}
