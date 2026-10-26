using System.IO;
using System.Windows;
using Sulap.App.Platform;
using Sulap.Core;

namespace Sulap.App.Views;

public partial class SettingsWindow : Window
{
    private readonly string _settingsPath;

    public SettingsWindow(string settingsPath)
    {
        InitializeComponent();
        _settingsPath = settingsPath;
        Title = $"{AppIdentity.Name} Settings";
        PathBox.Text = settingsPath;
    }

    private void OnShowFileClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Shell.ShowInExplorer(_settingsPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or System.Runtime.InteropServices.COMException)
        {
            MessageBox.Show(this, $"Couldn't open File Explorer: {ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
