using System.Configuration;
using System.Windows;
using SensorConfig.ViewModels;
using SensorConfig.Views;

namespace SensorConfig;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var viewModel = new MainWindowViewModel();
        var mainWindow = new MainWindow { DataContext = viewModel };
        mainWindow.Show();
        base.OnStartup(e);
    }
}
