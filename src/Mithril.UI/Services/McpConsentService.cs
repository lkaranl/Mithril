using System;
using System.Threading.Tasks;
using Mithril.Domain.Interfaces;
using Mithril.Domain.Models;

namespace Mithril.UI.Services;

public class McpConsentService : IMcpConsentService
{
    // Delegate que a UI (MainWindow/MainWindowViewModel) irá assinar para exibir o modal de consentimento
    public Func<string, string, Task<ConsentResponse>>? OnConsentRequested { get; set; }

    public async Task<ConsentResponse> RequestConsentAsync(string requester, string domain)
    {
        if (OnConsentRequested == null)
        {
            // Por segurança, se não houver interface gráfica associada para pedir consentimento, rejeita o acesso
            return new ConsentResponse
            {
                Approved = false,
                Username = null,
                Password = null
            };
        }

        // Encaminha a solicitação para o handler registrado na UI
        return await OnConsentRequested(requester, domain);
    }
}
