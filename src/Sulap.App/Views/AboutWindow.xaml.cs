using System.Windows;
using Sulap.Core;

namespace Sulap.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        Title = $"About {AppIdentity.Name}";
        NameText.Text = AppIdentity.Name;
        VersionText.Text = $"Version {AppIdentity.Version}";
        SummaryText.Text = $"Converts files with a drag gesture: drag a file, press Shift, and drop it on the format you want. "
            + $"{AppIdentity.Name} saves a converted copy next to the original.";
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
