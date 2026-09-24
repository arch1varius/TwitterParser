using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Parser.WinUI;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; } = new();
    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1120, 860));
        Closed += (_, _) => ViewModel.Dispose();
    }
    private void ApiKeyChanged(object sender, RoutedEventArgs e) => ViewModel.ApiKey = ((PasswordBox)sender).Password;
}
