using System;

namespace Mithril.Domain.Models;

public enum CredentialType
{
    Web,
    ApiToken
}

public class Credential
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public CredentialType Type { get; set; } = CredentialType.Web;
    public string Domain { get; set; } = string.Empty; // Serve como nome do site ou nome da API (ex: "reciprocidade")
    public string Username { get; set; } = string.Empty; // Serve como usuário web ou Client ID da API
    public string EncryptedPassword { get; set; } = string.Empty; // Serve como senha web ou Client Secret criptografado
    public string TokenUrl { get; set; } = string.Empty; // Específico para tipo ApiToken
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastModifiedAt { get; set; } = DateTime.UtcNow;
}
