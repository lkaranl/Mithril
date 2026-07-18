using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Mithril.Domain.Exceptions;
using Mithril.Domain.Interfaces;
using Mithril.Domain.Models;

namespace Mithril.Infrastructure.Persistence;

public class LocalFileVaultRepository : IVaultRepository
{
    private readonly ISecurityService _securityService;

    public LocalFileVaultRepository(ISecurityService securityService)
    {
        _securityService = securityService;
    }

    // Classe auxiliar interna para representar a estrutura persistida fisicamente
    private class PhysicalVaultFile
    {
        public Guid VaultId { get; set; }
        public string KeyDerivationSalt { get; set; } = string.Empty;
        public int KeyDerivationIterations { get; set; }
        public string KeyDerivationAlgorithm { get; set; } = string.Empty;
        public string EncryptedPayload { get; set; } = string.Empty; // Base64 dos dados criptografados
        public DateTime LastModifiedAt { get; set; }
    }

    public async Task<VaultData> LoadVaultAsync(string filePath, byte[] decryptionKey)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("O arquivo do cofre não foi encontrado.", filePath);

        try
        {
            // Lendo o arquivo físico JSON
            var jsonContent = await File.ReadAllTextAsync(filePath);
            var physicalFile = JsonSerializer.Deserialize<PhysicalVaultFile>(jsonContent);

            if (physicalFile == null || string.IsNullOrEmpty(physicalFile.EncryptedPayload))
                throw new SecurityException("O formato do arquivo do cofre é inválido.");

            // Descriptografando o payload (lista de credenciais em formato JSON)
            byte[] encryptedBytes = Convert.FromBase64String(physicalFile.EncryptedPayload);
            byte[] decryptedBytes = _securityService.Decrypt(encryptedBytes, decryptionKey);

            // Deserializando o payload descriptografado
            var credentialsJson = System.Text.Encoding.UTF8.GetString(decryptedBytes);
            var credentials = JsonSerializer.Deserialize<System.Collections.Generic.List<Credential>>(credentialsJson);

            // Montando o modelo de domínio
            return new VaultData
            {
                VaultId = physicalFile.VaultId,
                KeyDerivationSalt = physicalFile.KeyDerivationSalt,
                KeyDerivationIterations = physicalFile.KeyDerivationIterations,
                KeyDerivationAlgorithm = physicalFile.KeyDerivationAlgorithm,
                LastModifiedAt = physicalFile.LastModifiedAt,
                Credentials = credentials ?? new()
            };
        }
        catch (JsonException ex)
        {
            throw new SecurityException("Erro ao processar a estrutura de dados do cofre.", ex);
        }
        catch (SecurityException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SecurityException("Erro desconhecido ao carregar o cofre.", ex);
        }
    }

    public async Task SaveVaultAsync(string filePath, VaultData vault, byte[] encryptionKey)
    {
        try
        {
            // 1. Serializar a lista de credenciais
            var credentialsJson = JsonSerializer.Serialize(vault.Credentials);
            byte[] plaintextBytes = System.Text.Encoding.UTF8.GetBytes(credentialsJson);

            // 2. Criptografar a lista de credenciais
            byte[] encryptedBytes = _securityService.Encrypt(plaintextBytes, encryptionKey);
            string encryptedBase64 = Convert.FromBase64String(Convert.ToBase64String(encryptedBytes)) == null ? "" : Convert.ToBase64String(encryptedBytes);

            // 3. Montar a estrutura física do arquivo
            var physicalFile = new PhysicalVaultFile
            {
                VaultId = vault.VaultId,
                KeyDerivationSalt = vault.KeyDerivationSalt,
                KeyDerivationIterations = vault.KeyDerivationIterations,
                KeyDerivationAlgorithm = vault.KeyDerivationAlgorithm,
                EncryptedPayload = encryptedBase64,
                LastModifiedAt = DateTime.UtcNow
            };

            // 4. Salvar em disco
            var jsonContent = JsonSerializer.Serialize(physicalFile, new JsonSerializerOptions { WriteIndented = true });
            
            // Garantir que o diretório pai existe
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(filePath, jsonContent);
        }
        catch (Exception ex)
        {
            throw new SecurityException("Falha ao gravar os dados criptografados do cofre em disco.", ex);
        }
    }
}
