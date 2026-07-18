using System;
using System.IO;
using System.Security.Cryptography;
using Mithril.Domain.Exceptions;
using Mithril.Domain.Interfaces;

namespace Mithril.Infrastructure.Security;

public class AesGcmSecurityService : ISecurityService
{
    private const int NonceSize = 12; // IV recomendado para AES-GCM
    private const int TagSize = 16;   // Tag de integridade padrão

    public byte[] Encrypt(byte[] plaintext, byte[] key)
    {
        if (key == null || key.Length != 32)
            throw new ArgumentException("A chave do AES-256 deve conter exatamente 32 bytes (256 bits).", nameof(key));
        if (plaintext == null)
            throw new ArgumentNullException(nameof(plaintext));

        byte[] nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        byte[] tag = new byte[TagSize];
        byte[] ciphertext = new byte[plaintext.Length];

        try
        {
            using var aesGcm = new AesGcm(key, TagSize);
            aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);
        }
        catch (CryptographicException ex)
        {
            throw new SecurityException("Erro criptográfico ao encriptar os dados.", ex);
        }

        // Empacota: [Nonce (12B)] + [Tag (16B)] + [Ciphertext]
        byte[] result = new byte[NonceSize + TagSize + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, result, NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, result, NonceSize + TagSize, ciphertext.Length);

        return result;
    }

    public byte[] Decrypt(byte[] encryptedData, byte[] key)
    {
        if (key == null || key.Length != 32)
            throw new ArgumentException("A chave do AES-256 deve conter exatamente 32 bytes (256 bits).", nameof(key));
        if (encryptedData == null || encryptedData.Length < NonceSize + TagSize)
            throw new ArgumentException("Dados criptografados inválidos ou corrompidos.", nameof(encryptedData));

        byte[] nonce = new byte[NonceSize];
        byte[] tag = new byte[TagSize];
        byte[] ciphertext = new byte[encryptedData.Length - NonceSize - TagSize];

        Buffer.BlockCopy(encryptedData, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(encryptedData, NonceSize, tag, 0, TagSize);
        Buffer.BlockCopy(encryptedData, NonceSize + TagSize, ciphertext, 0, ciphertext.Length);

        byte[] plaintext = new byte[ciphertext.Length];

        try
        {
            using var aesGcm = new AesGcm(key, TagSize);
            aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (CryptographicException ex)
        {
            throw new SecurityException("Falha na decriptação. Senha mestre incorreta ou dados do cofre corrompidos.", ex);
        }

        return plaintext;
    }

    public byte[] DeriveKey(string password, byte[] salt, int iterations)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("A senha não pode ser vazia.", nameof(password));
        if (salt == null || salt.Length < 16)
            throw new ArgumentException("O salt deve ter pelo menos 16 bytes.", nameof(salt));

        // Deriva uma chave de 32 bytes (256 bits) usando PBKDF2-SHA256
        return Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            32
        );
    }

    public byte[] GenerateSalt(int size = 32)
    {
        if (size < 16)
            throw new ArgumentException("O tamanho mínimo de salt deve ser 16 bytes.", nameof(size));

        byte[] salt = new byte[size];
        RandomNumberGenerator.Fill(salt);
        return salt;
    }
}
