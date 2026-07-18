using System;

namespace Mithril.Domain.Models;

public class BackupMetadata
{
    public Guid BackupId { get; set; } = Guid.NewGuid();
    public Guid OriginalVaultId { get; set; }
    public DateTime ExportedAt { get; set; } = DateTime.UtcNow;
    public string Checksum { get; set; } = string.Empty; // Hash SHA-256 do conteúdo criptografado para garantir integridade física
    public string AppVersion { get; set; } = "1.0.0";
}
