using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Mithril.UI.Views;

public partial class SseConfirmWindow : Window
{
    public SseConfirmWindow()
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
