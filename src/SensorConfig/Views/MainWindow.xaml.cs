using System.ComponentModel;
using System.Windows;
using SensorConfig.ViewModels;

namespace SensorConfig.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Closing += OnClosing;
        }

        private void OnClosing(object? sender, CancelEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
                vm.Stop();
        }
    }
}
