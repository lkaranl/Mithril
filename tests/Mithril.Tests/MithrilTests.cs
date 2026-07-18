using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Mithril.Domain.Exceptions;
using Mithril.Domain.Interfaces;
using Mithril.Domain.Models;
using Mithril.Infrastructure.Persistence;
using Mithril.Infrastructure.Security;
using Mithril.Infrastructure.Services;

namespace Mithril.Tests;

public class MithrilTests
{
    private readonly ISecurityService _securityService;

    public MithrilTests()
    {
        _securityService = new AesGcmSecurityService();
    }

    [Fact]
    public void AesGcm_Encrypt_And_Decrypt_Should_Return_Original_Data()
    {
        // Arrange
        byte[] key = _securityService.GenerateSalt(32); // Chave de 256 bits
        string originalText = "MinhaSenhaSuperSecreta123!";
        byte[] plaintext = Encoding.UTF8.GetBytes(originalText);

        // Act
        byte[] ciphertext = _securityService.Encrypt(plaintext, key);
        byte[] decryptedBytes = _securityService.Decrypt(ciphertext, key);
        string decryptedText = Encoding.UTF8.GetString(decryptedBytes);

        // Assert
        Assert.NotEqual(plaintext, ciphertext);
        Assert.Equal(originalText, decryptedText);
    }

    [Fact]
    public void AesGcm_Decrypt_With_Wrong_Key_Should_Throw_SecurityException()
    {
        // Arrange
        byte[] correctKey = _securityService.GenerateSalt(32);
        byte[] wrongKey = _securityService.GenerateSalt(32);
        byte[] plaintext = Encoding.UTF8.GetBytes("Dados confidenciais");

        byte[] ciphertext = _securityService.Encrypt(plaintext, correctKey);

        // Act & Assert
        Assert.Throws<SecurityException>(() => _securityService.Decrypt(ciphertext, wrongKey));
    }

    [Fact]
    public void KeyDerivation_Should_Be_Consistent_And_Strong()
    {
        // Arrange
        string password = "MinhaMasterPasswordForte";
        byte[] salt = _securityService.GenerateSalt(16);
        int iterations = 10000; // menor número para acelerar o teste unitário

        // Act
        byte[] key1 = _securityService.DeriveKey(password, salt, iterations);
        byte[] key2 = _securityService.DeriveKey(password, salt, iterations);
        byte[] keyWithDifferentPassword = _securityService.DeriveKey("OutraSenha", salt, iterations);

        // Assert
        Assert.Equal(32, key1.Length);
        Assert.Equal(key1, key2); // Deve ser consistente
        Assert.NotEqual(key1, keyWithDifferentPassword); // Senhas diferentes devem gerar chaves diferentes
    }

    [Fact]
    public async Task BackupService_Should_Detect_Physical_Corruption()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        string vaultFile = Path.Combine(tempDir, "original_vault.json");
        string backupDir = Path.Combine(tempDir, "backups");

        // Criar um conteúdo inicial simulado para o cofre
        string simulatedVaultContent = "{\"VaultId\":\"" + Guid.NewGuid() + "\", \"EncryptedPayload\":\"AAAA\"}";
        await File.WriteAllTextAsync(vaultFile, simulatedVaultContent);

        IBackupService backupService = new VaultBackupService();

        try
        {
            // Act: Criar backup
            string backupPath = await backupService.CreateBackupAsync(vaultFile, backupDir);

            // Assert: Verificar integridade inicial
            bool isIntegrityOk = await backupService.VerifyBackupIntegrityAsync(backupPath);
            Assert.True(isIntegrityOk);

            // Simular corrupção física alterando 1 byte do arquivo de backup
            byte[] backupBytes = await File.ReadAllBytesAsync(backupPath);
            backupBytes[backupBytes.Length - 1] = (byte)(backupBytes[backupBytes.Length - 1] ^ 0xFF); // inverte o último byte
            await File.WriteAllBytesAsync(backupPath, backupBytes);

            // Assert: Verificar integridade após corrupção (deve falhar)
            bool isIntegrityCorrupted = await backupService.VerifyBackupIntegrityAsync(backupPath);
            Assert.False(isIntegrityCorrupted);

            // Assert: Restauração deve lançar SecurityException pela falha de integridade
            string restoreTarget = Path.Combine(tempDir, "restored_vault.json");
            await Assert.ThrowsAsync<SecurityException>(() => backupService.RestoreBackupAsync(backupPath, restoreTarget));
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public MockHttpMessageHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_response);
        }
    }

    [Fact]
    public async Task GetAccessTokenAsync_With_ValidResponse_Should_Return_Token()
    {
        // Arrange
        var mockResponse = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"access_token\":\"mocked-jwt-token-value\"}", Encoding.UTF8, "application/json")
        };
        var handler = new MockHttpMessageHandler(mockResponse);
        using var client = new HttpClient(handler);
        var service = new HttpTokenExchangeService(client);

        // Act
        string token = await service.GetAccessTokenAsync("https://dummy.api/token", "client_id", "client_secret");

        // Assert
        Assert.Equal("mocked-jwt-token-value", token);
    }

    [Fact]
    public async Task GetAccessTokenAsync_With_HttpError_Should_Throw_SecurityException()
    {
        // Arrange
        var mockResponse = new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
        {
            Content = new StringContent("Erro de autenticação fake", Encoding.UTF8, "text/plain")
        };
        var handler = new MockHttpMessageHandler(mockResponse);
        using var client = new HttpClient(handler);
        var service = new HttpTokenExchangeService(client);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<SecurityException>(() => 
            service.GetAccessTokenAsync("https://dummy.api/token", "client_id", "client_secret")
        );
        Assert.Contains("Erro de autenticação fake", ex.Message);
    }
}
