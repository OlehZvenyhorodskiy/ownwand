using System.Windows;
using OwnWand.App.ViewModels;
using Wpf.Ui.Controls;

namespace OwnWand.App;

public partial class MainWindow : FluentWindow
{
    public MainViewModel ViewModel { get; }

    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        
        // Initialize services after window loads
        Loaded += async (_, _) => await ViewModel.InitializeAsync();
    }
}
