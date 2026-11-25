using System.Windows;
using AITradingSystem.UI.ViewModels;

namespace AITradingSystem.UI;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        // Явно устанавливаем DataContext из DI-контейнера
        this.DataContext = viewModel;
    }
}