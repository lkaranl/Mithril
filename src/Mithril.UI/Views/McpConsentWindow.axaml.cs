using System;
using Avalonia.Controls;
using Mithril.UI.ViewModels;

namespace Mithril.UI.Views;

public partial class McpConsentWindow : Window
{
    public McpConsentWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is McpConsentViewModel vm)
        {
            // Quando o consentimento for definido (aprovado ou recusado), fecha a janela retornando o resultado
            vm.ConsentTask.ContinueWith(t =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    this.Close(t.Result);
                });
            });
        }
    }
}
