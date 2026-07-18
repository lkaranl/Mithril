using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Mithril.UI.Views;

public partial class DeleteConfirmWindow : Window
{
    public DeleteConfirmWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
    }

    public DeleteConfirmWindow(string identifier) : this()
    {
        var textBlock = this.FindControl<TextBlock>("TxtIdentifier");
        if (textBlock != null)
        {
            textBlock.Text = identifier;
        }
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
