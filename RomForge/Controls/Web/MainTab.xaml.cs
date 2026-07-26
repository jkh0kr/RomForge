using RomForge.ViewModels.Web;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace RomForge.Controls.Web;

public partial class MainTab : UserControl
{
    public MainTab()
    {
        InitializeComponent();
    }

    private void Hyperlink_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not Hyperlink hyperlink)
            return;

        var url = hyperlink.Tag as string;

        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http"))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private void TxtKeyword_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        if (DataContext is PatchSearchMainViewModel vm && vm.SearchCommand.CanExecute(null))
            vm.SearchCommand.Execute(null);
    }

    private void BtnPlatform_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        platformPopup.IsOpen = !platformPopup.IsOpen;
    }
}