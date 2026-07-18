using System.Threading.Tasks;
using Mithril.Domain.Models;

namespace Mithril.Domain.Interfaces;

public interface IVaultRepository
{
    Task<VaultData> LoadVaultAsync(string filePath, byte[] decryptionKey);
    Task SaveVaultAsync(string filePath, VaultData vault, byte[] encryptionKey);
}
