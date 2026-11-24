using System.Windows;

namespace LogiPluginBackup.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // With arguments the program works as a command line tool (scheduled backups, scripts).
        if (e.Args.Length > 0)
        {
            Shutdown(Cli.Run(e.Args));
            return;
        }

        new MainWindow().Show();
    }
}
