using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using LogiPluginBackup.Core;
using Microsoft.Win32;

namespace LogiPluginBackup.App;

public sealed class PluginRow
{
    public bool Selected { get; set; } = true;
    public string Key { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Version { get; init; } = "";
    public string Author { get; init; } = "";
    public string Kind { get; init; } = "";
    public string SizeText { get; init; } = "";
}

public sealed class ConfigRow
{
    public bool Selected { get; set; }
    public string Name { get; init; } = "";
    public string Label { get; init; } = "";
}

public sealed class RestoreRow
{
    public bool Selected { get; set; }
    public string Key { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Version { get; init; } = "";
    public string Installed { get; init; } = "";
    public string Status { get; init; } = "";
    public bool Differs { get; init; }
}

public partial class MainWindow : Window
{
    private readonly LogiPaths _paths = new();
    private readonly ObservableCollection<PluginRow> _backupRows = [];
    private readonly ObservableCollection<ConfigRow> _backupConfig = [];
    private readonly ObservableCollection<RestoreRow> _restoreRows = [];
    private readonly ObservableCollection<ConfigRow> _restoreConfig = [];
    private BackupArchive? _archive;

    public MainWindow()
    {
        InitializeComponent();
        Log = new Progress<string>(s =>
        {
            LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {s}{Environment.NewLine}");
            LogBox.ScrollToEnd();
        });
        BackupGrid.ItemsSource = _backupRows;
        BackupConfigList.ItemsSource = _backupConfig;
        RestoreGrid.ItemsSource = _restoreRows;
        RestoreConfigList.ItemsSource = _restoreConfig;
        BackupPath.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), $"LogiPlugins_{DateTime.Now:yyyy-MM-dd}.lpbak");
        Loaded += (_, _) => LoadBackupLists();
        Closed += (_, _) => _archive?.Dispose();
    }

    // Created ONCE on the UI thread: Progress<T> posts back to the thread that created it, so the log can be
    // fed safely from the background tasks.
    private IProgress<string> Log { get; }

    private static string Size(long bytes) => bytes >= 1 << 20 ? $"{bytes / 1048576.0:0.0} Mo" : $"{Math.Max(1, bytes / 1024)} Ko";

    // ---------------------------------------------------------------- backup tab

    private void LoadBackupLists()
    {
        _backupRows.Clear();
        _backupConfig.Clear();
        if (!Directory.Exists(_paths.PluginsDir))
        {
            Log.Report($"Dossier des plugins introuvable : {_paths.PluginsDir}. Logi Options+ est-il installé ?");
            return;
        }
        foreach (var p in Inventory.ScanPlugins(_paths))
        {
            _backupRows.Add(new PluginRow
            {
                Key = p.Key, DisplayName = p.DisplayName, Version = p.Version, Author = p.Author,
                Kind = p.Kind, SizeText = Size(p.SizeBytes),
            });
        }
        foreach (var c in Inventory.ScanConfig(_paths))
        {
            _backupConfig.Add(new ConfigRow { Name = c.Name, Label = c.Label, Selected = c.DefaultSelected });
        }
        BackupSummary.Text = $"{_backupRows.Count} plugins trouvés";
        Log.Report($"{_backupRows.Count} plugins et {_backupConfig.Count} jeux de configuration trouvés dans {_paths.Root}");
    }

    private void BackupAll_Click(object s, RoutedEventArgs e) => SetAll(_backupRows, true);

    private void BackupNone_Click(object s, RoutedEventArgs e) => SetAll(_backupRows, false);

    private void SetAll(IEnumerable<PluginRow> rows, bool value)
    {
        foreach (var r in rows) r.Selected = value;
        BackupGrid.Items.Refresh();
    }

    private void BackupBrowse_Click(object s, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Sauvegarde Logi Plugin Backup (*.lpbak)|*.lpbak", DefaultExt = ".lpbak",
            FileName = Path.GetFileName(BackupPath.Text), InitialDirectory = Path.GetDirectoryName(BackupPath.Text),
        };
        if (dlg.ShowDialog(this) == true) BackupPath.Text = dlg.FileName;
    }

    private async void Backup_Click(object s, RoutedEventArgs e)
    {
        var keys = _backupRows.Where(r => r.Selected).Select(r => r.Key).ToList();
        var cfg = _backupConfig.Where(r => r.Selected).Select(r => r.Name).ToList();
        if (keys.Count == 0 && cfg.Count == 0)
        {
            MessageBox.Show(this, "Cochez au moins un plugin ou un jeu de configuration.", Title);
            return;
        }
        var dest = BackupPath.Text.Trim();
        if (dest.Length == 0) return;

        var result = await RunBusy(() => BackupService.Create(_paths, dest, new BackupSelection(keys, cfg), Log));
        ShowResult(result, "Sauvegarde terminée");
    }

    // ---------------------------------------------------------------- restore tab

    private void RestoreBrowse_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Sauvegarde Logi Plugin Backup (*.lpbak)|*.lpbak|Tous les fichiers|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        OpenArchive(dlg.FileName);
    }

    private void OpenArchive(string file)
    {
        _archive?.Dispose();
        _archive = null;
        _restoreRows.Clear();
        _restoreConfig.Clear();
        RestoreButton.IsEnabled = VerifyButton.IsEnabled = false;
        try
        {
            _archive = BackupArchive.Open(file);
        }
        catch (Exception ex)
        {
            RestoreInfo.Text = "Fichier refusé : " + ex.Message;
            Log.Report("Fichier refusé : " + ex.Message);
            return;
        }

        var m = _archive.Manifest;
        RestorePath.Text = file;
        RestoreInfo.Text = $"Sauvegarde du {m.CreatedUtc.ToLocalTime():dd/MM/yyyy HH:mm} - PC {m.Machine}, utilisateur {m.User}" +
                           (m.PluginServiceVersion.Length > 0 ? $", Plugin Service {m.PluginServiceVersion}" : "") +
                           $" - {m.Plugins.Count} plugins.";

        var installed = Inventory.ScanPlugins(_paths).ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var p in m.Plugins.OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            installed.TryGetValue(p.Key, out var here);
            string status;
            bool differs;
            if (here is null) { status = "Absent de ce PC"; differs = true; }
            else if (here.IsDevLink) { status = "Lien de développement ici (ignoré)"; differs = false; }
            else if (here.Version == p.Version) { status = "Même version"; differs = false; }
            else { status = "Version différente"; differs = true; }
            _restoreRows.Add(new RestoreRow
            {
                Key = p.Key, DisplayName = p.DisplayName, Version = p.Version, Installed = here?.Version ?? "-",
                Status = status, Differs = differs, Selected = differs,
            });
        }
        foreach (var c in LogiPaths.ConfigItems.Where(c => m.Configs.Contains(c.Name)))
        {
            _restoreConfig.Add(new ConfigRow { Name = c.Name, Label = c.Label, Selected = c.DefaultSelected });
        }
        RestoreButton.IsEnabled = VerifyButton.IsEnabled = true;
        Log.Report($"Sauvegarde ouverte : {file}");
    }

    private void RestoreAll_Click(object s, RoutedEventArgs e) => SetRestore(r => true);

    private void RestoreNone_Click(object s, RoutedEventArgs e) => SetRestore(r => false);

    private void RestoreDiff_Click(object s, RoutedEventArgs e) => SetRestore(r => r.Differs);

    private void SetRestore(Func<RestoreRow, bool> pick)
    {
        foreach (var r in _restoreRows) r.Selected = pick(r);
        RestoreGrid.Items.Refresh();
    }

    private async void Verify_Click(object s, RoutedEventArgs e)
    {
        if (_archive is null) return;
        var archive = _archive;
        var problems = await RunBusy(() => archive.Verify(Log));
        MessageBox.Show(this, problems.Count == 0
            ? "Archive intacte : tous les fichiers correspondent à leur empreinte SHA-256."
            : "Problèmes détectés :\n\n" + string.Join("\n", problems.Take(15)), Title);
    }

    private async void Restore_Click(object s, RoutedEventArgs e)
    {
        if (_archive is null) return;
        var keys = _restoreRows.Where(r => r.Selected).Select(r => r.Key).ToList();
        var cfg = _restoreConfig.Where(r => r.Selected).Select(r => r.Name).ToList();
        if (keys.Count == 0 && cfg.Count == 0)
        {
            MessageBox.Show(this, "Cochez au moins un plugin ou un jeu de configuration.", Title);
            return;
        }
        var stop = StopServiceBox.IsChecked == true;
        var ask = $"Restaurer {keys.Count} plugin(s) et {cfg.Count} jeu(x) de configuration ?\n\n" +
                  "Les dossiers concernés sont remplacés. Leur état actuel est copié avant dans :\n" +
                  LogiPaths.SafetyRoot + "\n\n" +
                  (stop ? "Le service de plugins Logi va être arrêté puis relancé par Options+ (quelques secondes)." : "Le service n'est pas arrêté : des fichiers verrouillés peuvent bloquer la restauration.");
        if (MessageBox.Show(this, ask, Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        var archive = _archive;
        var result = await RunBusy(() => RestoreService.Restore(
            _paths, archive, new BackupSelection(keys, cfg), new RestoreOptions(StopService: stop), new PluginServiceControl(), Log));
        ShowResult(result, "Restauration terminée");
        LoadBackupLists();
        OpenArchive(archive.Path);
    }

    // ---------------------------------------------------------------- common

    private async Task<T> RunBusy<T>(Func<T> work)
    {
        Tabs.IsEnabled = false;
        Busy.Visibility = Visibility.Visible;
        try
        {
            return await Task.Run(work);
        }
        finally
        {
            Busy.Visibility = Visibility.Hidden;
            Tabs.IsEnabled = true;
        }
    }

    private void ShowResult(OperationResult r, string title)
    {
        foreach (var d in r.Done) Log.Report("  OK  " + d);
        foreach (var w in r.Warnings) Log.Report("  !   " + w);
        foreach (var x in r.Errors) Log.Report("  ERREUR  " + x);
        if (r.SafetyCopy is not null) Log.Report("  Copie de sécurité : " + r.SafetyCopy);

        var text = r.Success
            ? $"{title}.\n\n{string.Join("\n", r.Done.Take(12))}" + (r.Done.Count > 12 ? $"\n... (+{r.Done.Count - 12})" : "")
            : "Échec :\n\n" + string.Join("\n", r.Errors);
        if (r.Warnings.Count > 0) text += "\n\nAvertissements :\n" + string.Join("\n", r.Warnings.Take(6));
        MessageBox.Show(this, text, Title, MessageBoxButton.OK, r.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
        if (r.Success && r.File is not null && File.Exists(r.File) && Tabs.SelectedIndex == 0)
        {
            Process.Start("explorer.exe", $"/select,\"{r.File}\"")?.Dispose();
        }
    }
}
