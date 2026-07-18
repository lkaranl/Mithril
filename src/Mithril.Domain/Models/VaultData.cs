using System;
using System.Collections.Generic;

namespace Mithril.Domain.Models;

public class VaultData
{
    public Guid VaultId { get; set; } = Guid.NewGuid();
    public List<Credential> Credentials { get; set; } = new();
    public DateTime LastBackupAt { get; set; }
    public string KeyDerivationSalt { get; set; } = string.Empty; // Base64 do salt usado na derivação de chave (PBKDF2)
    public int KeyDerivationIterations { get; set; } = 600000; // Número de iterações recomendadas pelo OWASP
    public string KeyDerivationAlgorithm { get; set; } = "PBKDF2-SHA256";
    public DateTime LastModifiedAt { get; set; } = DateTime.UtcNow;
}
