using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Mithril.UI.Views;

public partial class SseDisconnectWindow : Window
{
    public SseDisconnectWindow()
    {
        InitializeComponent();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        Close(true);
    }
}
