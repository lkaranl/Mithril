namespace Mithril.Domain.Interfaces;

public interface ISecurityService
{
    byte[] Encrypt(byte[] plaintext, byte[] key);
    byte[] Decrypt(byte[] encryptedData, byte[] key);
    byte[] DeriveKey(string password, byte[] salt, int iterations);
    byte[] GenerateSalt(int size = 32);
}
