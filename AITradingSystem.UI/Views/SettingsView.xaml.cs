using System.Windows.Controls;

namespace AITradingSystem.UI.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void LiveApiSecretBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is ViewModels.SettingsViewModel vm)
            vm.LiveApiSecretInput = ((PasswordBox)sender).Password;
    }
}