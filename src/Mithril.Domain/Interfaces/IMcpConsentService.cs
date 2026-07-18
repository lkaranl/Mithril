using System.Threading.Tasks;
using Mithril.Domain.Models;

namespace Mithril.Domain.Interfaces;

public interface IMcpConsentService
{
    Task<ConsentResponse> RequestConsentAsync(string requester, string domain);
}
