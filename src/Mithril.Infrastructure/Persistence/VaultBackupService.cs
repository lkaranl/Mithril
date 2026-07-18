using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Mithril.Domain.Exceptions;
using Mithril.Domain.Interfaces;
using Mithril.Domain.Models;

namespace Mithril.Infrastructure.Persistence;

public class VaultBackupService : IBackupService
{
    private const string BackupExtension = ".mithrilbak";
    private const string MetaExtension = ".meta";

    public async Task<string> CreateBackupAsync(string vaultFilePath, string backupDirectoryPath)
    {
        if (!File.Exists(vaultFilePath))
            throw new FileNotFoundException("O cofre original não foi encontrado para realizar o backup.", vaultFilePath);

        if (!Directory.Exists(backupDirectoryPath))
            Directory.CreateDirectory(backupDirectoryPath);

        try
        {
            // 1. Calcular o Checksum SHA-256 do arquivo original criptografado
            string checksum = await CalculateFileSha256Async(vaultFilePath);

            // 2. Definir caminhos do backup
            string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            string backupFileName = $"vault_{timestamp}{BackupExtension}";
            string backupFilePath = Path.Combine(backupDirectoryPath, backupFileName);
            string metaFilePath = backupFilePath + MetaExtension;

            // 3. Copiar o arquivo físico criptografado
            File.Copy(vaultFilePath, backupFilePath, overwrite: true);

            // 4. Ler metadados do cofre para o backup
            var vaultContent = await File.ReadAllTextAsync(vaultFilePath);
            using var doc = JsonDocument.Parse(vaultContent);
            Guid vaultId = Guid.Empty;
            if (doc.RootElement.TryGetProperty("VaultId", out var idProp) || doc.RootElement.TryGetProperty("vaultId", out idProp))
            {
                vaultId = idProp.GetGuid();
            }

            // 5. Salvar o arquivo de metadados
            var metadata = new BackupMetadata
            {
                BackupId = Guid.NewGuid(),
                OriginalVaultId = vaultId,
                ExportedAt = DateTime.UtcNow,
                Checksum = checksum,
                AppVersion = "1.0.0"
            };

            string metaJson = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(metaFilePath, metaJson);

            return backupFilePath;
        }
        catch (Exception ex)
        {
            throw new SecurityException("Erro ao criar o backup físico do cofre.", ex);
        }
    }

    public async Task<bool> VerifyBackupIntegrityAsync(string backupFilePath)
    {
        if (!File.Exists(backupFilePath))
            return false;

        string metaFilePath = backupFilePath + MetaExtension;
        if (!File.Exists(metaFilePath))
            return false;

        try
        {
            // 1. Ler o metadado
            string metaJson = await File.ReadAllTextAsync(metaFilePath);
            var metadata = JsonSerializer.Deserialize<BackupMetadata>(metaJson);

            if (metadata == null || string.IsNullOrEmpty(metadata.Checksum))
                return false;

            // 2. Calcular o checksum atual do arquivo de backup
            string currentChecksum = await CalculateFileSha256Async(backupFilePath);

            // 3. Comparar
            return string.Equals(metadata.Checksum, currentChecksum, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> RestoreBackupAsync(string backupFilePath, string targetVaultFilePath)
    {
        if (!File.Exists(backupFilePath))
            throw new FileNotFoundException("O arquivo de backup especificado não existe.", backupFilePath);

        // 1. Verificar a integridade física do backup antes de restaurar
        bool isIntegrityOk = await VerifyBackupIntegrityAsync(backupFilePath);
        if (!isIntegrityOk)
            throw new SecurityException("A verificação de integridade física do backup falhou. O arquivo de backup pode estar corrompido ou modificado.");

        try
        {
            // 2. Fazer um backup preventivo do cofre atual (se existir)
            if (File.Exists(targetVaultFilePath))
            {
                string backupDir = Path.GetDirectoryName(targetVaultFilePath) ?? "";
                string preRestoreBackup = Path.Combine(backupDir, $"vault_pre_restore_temp");
                File.Copy(targetVaultFilePath, preRestoreBackup, overwrite: true);
            }

            // 3. Restaurar copiando o backup sobre o alvo
            string? targetDir = Path.GetDirectoryName(targetVaultFilePath);
            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            File.Copy(backupFilePath, targetVaultFilePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            throw new SecurityException("Erro ao restaurar o cofre a partir do backup.", ex);
        }
    }

    private static async Task<string> CalculateFileSha256Async(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        byte[] hashBytes = await sha256.ComputeHashAsync(stream);
        return Convert.ToHexString(hashBytes);
    }
}
