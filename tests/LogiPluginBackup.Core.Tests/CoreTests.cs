using System.IO.Compression;
using LogiPluginBackup.Core;

namespace LogiPluginBackup.Core.Tests;

/// <summary>Never touches the real Logi install: everything happens in temporary folders.</summary>
public sealed class CoreTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "lpb-tests-" + Guid.NewGuid().ToString("N")[..8]);

    private sealed class FakeService : IServiceControl
    {
        public int Stops, Waits;
        public bool CanStop = true;
        public bool Stop(IProgress<string>? log) { Stops++; return CanStop; }
        public bool WaitUntilBack(TimeSpan timeout, IProgress<string>? log) { Waits++; return true; }
    }

    private LogiPaths MakeInstall(string name)
    {
        var root = Path.Combine(_tmp, name);
        var paths = new LogiPaths(root);
        Directory.CreateDirectory(Path.Combine(paths.PluginsDir, "Vlc", "metadata"));
        File.WriteAllText(Path.Combine(paths.PluginsDir, "Vlc", "metadata", "LoupedeckPackage.yaml"),
            "type: plugin4\nname: Vlc\ndisplayName: VLC media player\nversion: 1.4.0\nauthor: Someone\n");
        File.WriteAllText(Path.Combine(paths.PluginsDir, "Vlc", "VlcPlugin.dll"), "dll-bytes-v1");
        Directory.CreateDirectory(Path.Combine(root, "Applications", "Loupedeck71", "@_excel"));
        File.WriteAllText(Path.Combine(root, "Applications", "Loupedeck71", "@_excel", "profile.json"), "{\"keys\":1}");
        File.WriteAllText(Path.Combine(root, "LoupedeckSettings.ini"), "Language=fr-FR");
        return paths;
    }

    private static BackupSelection All(LogiPaths p) =>
        new(Inventory.ScanPlugins(p).Select(x => x.Key).ToList(), Inventory.ScanConfig(p).Select(c => c.Name).ToList());

    [Fact]
    public void Scan_reads_plugin_metadata()
    {
        var p = MakeInstall("scan");
        var plugin = Assert.Single(Inventory.ScanPlugins(p));
        Assert.Equal("Vlc", plugin.Key);
        Assert.Equal("VLC media player", plugin.DisplayName);
        Assert.Equal("1.4.0", plugin.Version);
        Assert.False(plugin.IsDevLink);
        Assert.Contains(Inventory.ScanConfig(p), c => c.Name == "Applications");
    }

    [Fact]
    public void Dev_link_is_stored_as_a_plugin_from_its_build_folder()
    {
        var p = MakeInstall("devlink");
        var build = Path.Combine(_tmp, "build", "Release");
        Directory.CreateDirectory(Path.Combine(build, "metadata"));
        Directory.CreateDirectory(Path.Combine(build, "bin"));
        File.WriteAllText(Path.Combine(build, "metadata", "LoupedeckPackage.yaml"), "name: MyDev\ndisplayName: My Dev\nversion: 2.0\nauthor: Me\n");
        File.WriteAllText(Path.Combine(build, "bin", "MyDev.dll"), "dev");
        File.WriteAllText(Path.Combine(p.PluginsDir, "MyDevPlugin.link"), build + "\\ \r\n");

        var dev = Inventory.ScanPlugins(p).Single(x => x.IsDevLink);
        Assert.Equal("MyDev", dev.Key);
        Assert.Equal(build, dev.LinkTarget);

        var file = Path.Combine(_tmp, "dev.lpbak");
        var r = BackupService.Create(p, file, All(p));
        Assert.True(r.Success);
        using var a = BackupArchive.Open(file);
        Assert.Contains(a.Manifest.Plugins, x => x.Key == "MyDev" && x.FromDevLink);
        Assert.True(a.Manifest.Files.ContainsKey("plugins/MyDev/bin/MyDev.dll"));
    }

    [Fact]
    public void Backup_then_restore_on_another_install_round_trips()
    {
        var source = MakeInstall("src");
        var file = Path.Combine(_tmp, "b.lpbak");
        Assert.True(BackupService.Create(source, file, All(source)).Success);

        var target = new LogiPaths(Path.Combine(_tmp, "dst"));
        var service = new FakeService();
        using var archive = BackupArchive.Open(file);
        var result = RestoreService.Restore(target, archive, All(source), new RestoreOptions(), service);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal("dll-bytes-v1", File.ReadAllText(Path.Combine(target.PluginsDir, "Vlc", "VlcPlugin.dll")));
        Assert.Equal("{\"keys\":1}", File.ReadAllText(Path.Combine(target.Root, "Applications", "Loupedeck71", "@_excel", "profile.json")));
        Assert.Equal("Language=fr-FR", File.ReadAllText(Path.Combine(target.Root, "LoupedeckSettings.ini")));
        Assert.Equal(1, service.Stops);
        Assert.Equal(1, service.Waits);
    }

    [Fact]
    public void Restore_replaces_existing_files_and_keeps_a_safety_copy()
    {
        var source = MakeInstall("src2");
        var file = Path.Combine(_tmp, "c.lpbak");
        Assert.True(BackupService.Create(source, file, All(source)).Success);

        // Same install changed after the backup: a stray file and a modified dll.
        File.WriteAllText(Path.Combine(source.PluginsDir, "Vlc", "VlcPlugin.dll"), "changed");
        File.WriteAllText(Path.Combine(source.PluginsDir, "Vlc", "stray.txt"), "x");

        using var archive = BackupArchive.Open(file);
        var result = RestoreService.Restore(source, archive, All(source), new RestoreOptions(), new FakeService());

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal("dll-bytes-v1", File.ReadAllText(Path.Combine(source.PluginsDir, "Vlc", "VlcPlugin.dll")));
        Assert.False(File.Exists(Path.Combine(source.PluginsDir, "Vlc", "stray.txt")));
        Assert.NotNull(result.SafetyCopy);
        Assert.Equal("changed", File.ReadAllText(Path.Combine(result.SafetyCopy!, "plugins", "Vlc", "VlcPlugin.dll")));
    }

    [Fact]
    public void Tampered_archive_is_refused_and_nothing_is_touched()
    {
        var source = MakeInstall("src3");
        var file = Path.Combine(_tmp, "d.lpbak");
        Assert.True(BackupService.Create(source, file, All(source)).Success);

        using (var zip = ZipFile.Open(file, ZipArchiveMode.Update))
        {
            zip.GetEntry("plugins/Vlc/VlcPlugin.dll")!.Delete();
            var e = zip.CreateEntry("plugins/Vlc/VlcPlugin.dll");
            using var w = new StreamWriter(e.Open());
            w.Write("evil");
        }

        var target = new LogiPaths(Path.Combine(_tmp, "dst3"));
        var service = new FakeService();
        using var archive = BackupArchive.Open(file);
        var result = RestoreService.Restore(target, archive, All(source), new RestoreOptions(), service);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("VlcPlugin.dll"));
        Assert.Equal(0, service.Stops);
        Assert.False(Directory.Exists(target.PluginsDir));
    }

    [Fact]
    public void Zip_slip_entry_is_rejected()
    {
        var file = Path.Combine(_tmp, "evil.lpbak");
        Directory.CreateDirectory(_tmp);
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
        {
            using (var m = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
                m.Write("{\"format\":\"lpbak/1\"}");
            using var w = new StreamWriter(zip.CreateEntry("plugins/x/../../../evil.txt").Open());
            w.Write("x");
        }
        Assert.Throws<InvalidDataException>(() => BackupArchive.Open(file));
    }

    [Fact]
    public void Unknown_format_is_rejected()
    {
        var file = Path.Combine(_tmp, "other.lpbak");
        Directory.CreateDirectory(_tmp);
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
        using (var m = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
            m.Write("{\"format\":\"something/9\"}");
        Assert.Throws<InvalidDataException>(() => BackupArchive.Open(file));
    }

    [Fact]
    public void Plugin_with_same_name_dev_link_is_skipped_on_restore()
    {
        var source = MakeInstall("src4");
        var file = Path.Combine(_tmp, "e.lpbak");
        Assert.True(BackupService.Create(source, file, All(source)).Success);

        // Target PC loads "Vlc" through a development link.
        var target = new LogiPaths(Path.Combine(_tmp, "dst4"));
        var build = Path.Combine(_tmp, "vlcbuild");
        Directory.CreateDirectory(Path.Combine(build, "metadata"));
        File.WriteAllText(Path.Combine(build, "metadata", "LoupedeckPackage.yaml"), "name: Vlc\nversion: 9\n");
        Directory.CreateDirectory(target.PluginsDir);
        File.WriteAllText(Path.Combine(target.PluginsDir, "Vlc.link"), build);

        using var archive = BackupArchive.Open(file);
        var result = RestoreService.Restore(target, archive, All(source), new RestoreOptions(), new FakeService());

        Assert.True(result.Success);
        Assert.Contains(result.Warnings, w => w.Contains("lien de développement"));
        Assert.False(Directory.Exists(Path.Combine(target.PluginsDir, "Vlc")));
    }

    [Fact]
    public void Service_that_cannot_be_stopped_aborts_before_any_change()
    {
        var source = MakeInstall("src5");
        var file = Path.Combine(_tmp, "f.lpbak");
        Assert.True(BackupService.Create(source, file, All(source)).Success);
        File.WriteAllText(Path.Combine(source.PluginsDir, "Vlc", "VlcPlugin.dll"), "changed");

        using var archive = BackupArchive.Open(file);
        var result = RestoreService.Restore(source, archive, All(source), new RestoreOptions(), new FakeService { CanStop = false });

        Assert.False(result.Success);
        Assert.Equal("changed", File.ReadAllText(Path.Combine(source.PluginsDir, "Vlc", "VlcPlugin.dll")));
    }

    [Fact]
    public void Backup_reads_files_that_another_process_keeps_open()
    {
        var source = MakeInstall("src6");
        var dll = Path.Combine(source.PluginsDir, "Vlc", "VlcPlugin.dll");
        using var holder = new FileStream(dll, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        var r = BackupService.Create(source, Path.Combine(_tmp, "g.lpbak"), All(source));
        Assert.True(r.Success);
        Assert.Empty(r.Warnings);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { /* best effort */ }
    }
}
