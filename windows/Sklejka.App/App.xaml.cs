using System.Windows;

namespace Sklejka.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Settings.Load();
#pragma warning disable WPF0001
        ThemeMode = Settings.Theme switch { "Light" => ThemeMode.Light, "Dark" => ThemeMode.Dark, _ => ThemeMode.System };
#pragma warning restore WPF0001
        DispatcherUnhandledException += (_, ex) => { MessageBox.Show(ex.Exception.Message, "Склейка файлов", MessageBoxButton.OK, MessageBoxImage.Error); ex.Handled = true; };
        var w = new MainWindow(e.Args);
        w.Show();
    }
}
