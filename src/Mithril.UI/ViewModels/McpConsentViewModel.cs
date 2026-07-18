using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Mithril.UI.ViewModels;

public partial class McpConsentViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _requester = string.Empty;

    [ObservableProperty]
    private string _domain = string.Empty;

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private bool _isMasterPasswordRequired;

    [ObservableProperty]
    private string _masterPasswordInput = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    private readonly TaskCompletionSource<bool> _tcs = new();

    public Task<bool> ConsentTask => _tcs.Task;

    [RelayCommand]
    private void Approve()
    {
        if (IsMasterPasswordRequired && string.IsNullOrWhiteSpace(MasterPasswordInput))
        {
            ErrorMessage = "A Senha Mestre é obrigatória para autorizar.";
            return;
        }

        ErrorMessage = null;
        _tcs.TrySetResult(true);
    }

    [RelayCommand]
    private void Deny()
    {
        ErrorMessage = null;
        _tcs.TrySetResult(false);
    }
}
