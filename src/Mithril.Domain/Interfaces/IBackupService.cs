using System.Threading.Tasks;

namespace Mithril.Domain.Interfaces;

public interface IBackupService
{
    Task<string> CreateBackupAsync(string vaultFilePath, string backupDirectoryPath);
    Task<bool> RestoreBackupAsync(string backupFilePath, string targetVaultFilePath);
    Task<bool> VerifyBackupIntegrityAsync(string backupFilePath);
}
