using System.Windows;
using System.Windows.Threading;
using System.IO;
namespace PistolLauncher;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += HandleDispatcherException;
        base.OnStartup(e);
    }

    private static void HandleDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var logPath = Path.Combine(AppContext.BaseDirectory, "PistolLauncher-crash.log");
        try { File.AppendAllText(logPath, $"[{DateTimeOffset.Now:O}] {e.Exception}\r\n\r\n"); } catch { }
        MessageBox.Show($"Произошла ошибка. Лаунчер сохранил подробности в PistolLauncher-crash.log.\n\n{e.Exception.Message}", "Ошибка PistolLauncher", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        Current.Shutdown(-1);
    }
}
