using System.Threading.Tasks;

namespace Mithril.Domain.Interfaces;

public interface ITokenExchangeService
{
    Task<string> GetAccessTokenAsync(string tokenUrl, string clientId, string clientSecret);
}
