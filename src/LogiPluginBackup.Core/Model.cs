using System.Text.Json.Serialization;

namespace LogiPluginBackup.Core;

/// <summary>One thing that can live in the plugin folder: an installed plugin or a development link.</summary>
/// <param name="Key">Stable identifier inside a backup (the plugin folder name).</param>
public sealed record PluginInfo(
    string Key,
    string Name,
    string DisplayName,
    string Version,
    string Author,
    long SizeBytes,
    bool IsDevLink,
    string LinkTarget)
{
    /// <summary>"Installé" for a package installed by Options+, "Lien dev" for a .link to a build folder.</summary>
    public string Kind => IsDevLink ? "Lien dev" : "Installé";
}

/// <summary>A configuration set of the Logi Plugin Service (profiles, plugin data...).</summary>
public sealed record ConfigItem(string Name, string Label, bool IsDirectory, bool DefaultSelected = true);

/// <summary>What the user chose to put in a backup.</summary>
public sealed record BackupSelection(IReadOnlyCollection<string> PluginKeys, IReadOnlyCollection<string> ConfigNames);

/// <summary>manifest.json at the root of a backup (.lpbak, a plain zip file).</summary>
public sealed class BackupManifest
{
    public const string CurrentFormat = "lpbak/1";

    [JsonPropertyName("format")] public string Format { get; set; } = CurrentFormat;
    [JsonPropertyName("createdUtc")] public DateTime CreatedUtc { get; set; }
    [JsonPropertyName("machine")] public string Machine { get; set; } = "";
    [JsonPropertyName("user")] public string User { get; set; } = "";
    [JsonPropertyName("pluginServiceVersion")] public string PluginServiceVersion { get; set; } = "";
    [JsonPropertyName("plugins")] public List<ManifestPlugin> Plugins { get; set; } = [];
    [JsonPropertyName("configs")] public List<string> Configs { get; set; } = [];

    /// <summary>SHA-256 (hex) of every file of the archive except this manifest.</summary>
    [JsonPropertyName("files")] public Dictionary<string, string> Files { get; set; } = [];
}

public sealed class ManifestPlugin
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("sizeBytes")] public long SizeBytes { get; set; }
    /// <summary>True when the plugin was loaded through a .link: its build folder is stored as a normal plugin.</summary>
    [JsonPropertyName("fromDevLink")] public bool FromDevLink { get; set; }
    [JsonPropertyName("linkTarget")] public string LinkTarget { get; set; } = "";
}

/// <summary>Result of a backup or restore, shown to the user.</summary>
public sealed class OperationResult
{
    public List<string> Done { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<string> Errors { get; } = [];
    public string? File { get; set; }
    public string? SafetyCopy { get; set; }
    public bool Success => Errors.Count == 0;
}
