using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Domain.Exceptions;
using Mithril.Domain.Interfaces;
using Mithril.Domain.Models;
using Mithril.UI.Services;
using Mithril.UI.Views;

namespace Mithril.UI.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly ISecurityService _securityService;
    private readonly IVaultRepository _vaultRepository;
    private readonly IBackupService _backupService;
    private readonly McpConsentService _mcpConsentService;

    private byte[]? _currentVaultKey;
    private VaultData? _currentVault;
    private readonly string _defaultVaultPath;
    private readonly string _defaultBackupDirectory;

    [ObservableProperty]
    private string _statusMessage = "Cofre Fechado. Crie ou abra seu cofre.";

    [ObservableProperty]
    private bool _isVaultOpen;

    [ObservableProperty]
    private string _masterPassword = string.Empty;

    [ObservableProperty]
    private ObservableCollection<Credential> _credentials = new();

    public MainViewModel(
        ISecurityService securityService,
        IVaultRepository vaultRepository,
        IBackupService backupService,
        McpConsentService mcpConsentService)
    {
        _securityService = securityService;
        _vaultRepository = vaultRepository;
        _backupService = backupService;
        _mcpConsentService = mcpConsentService;

        // Locais padrão para o cofre e backups dentro da pasta do usuário ou app
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string mithrilDir = Path.Combine(appData, "Mithril");
        _defaultVaultPath = Path.Combine(mithrilDir, "vault.json");
        _defaultBackupDirectory = Path.Combine(mithrilDir, "backups");

        // Registrar o callback de consentimento que conecta o Servidor MCP com a UI
        _mcpConsentService.OnConsentRequested = HandleMcpConsentRequestAsync;
    }

    [RelayCommand]
    private async Task OpenOrCreateVaultAsync()
    {
        if (string.IsNullOrWhiteSpace(MasterPassword))
        {
            StatusMessage = "A Senha Mestre é obrigatória.";
            return;
        }

        try
        {
            if (File.Exists(_defaultVaultPath))
            {
                // Carregar cofre existente
                StatusMessage = "Carregando cofre...";
                
                // Lemos o salt do arquivo antes para derivar a chave
                var tempVault = await _vaultRepository.LoadVaultAsync(_defaultVaultPath, new byte[32]); // Dummy key apenas para disparar leitura estrutural
                byte[] salt = Convert.FromBase64String(tempVault.KeyDerivationSalt);
                
                // Derivar a chave real
                byte[] derivedKey = _securityService.DeriveKey(MasterPassword, salt, tempVault.KeyDerivationIterations);

                // Agora carrega descriptografando o payload real
                _currentVault = await _vaultRepository.LoadVaultAsync(_defaultVaultPath, derivedKey);
                _currentVaultKey = derivedKey;
                
                IsVaultOpen = true;
                StatusMessage = $"Cofre aberto. { _currentVault.Credentials.Count } credenciais carregadas.";
                LoadCredentialsList();
            }
            else
            {
                // Criar um novo cofre de demonstração
                StatusMessage = "Criando novo cofre...";
                byte[] salt = _securityService.GenerateSalt();

                var newVault = new VaultData
                {
                    VaultId = Guid.NewGuid(),
                    KeyDerivationSalt = Convert.ToBase64String(salt),
                    KeyDerivationIterations = 600000,
                    KeyDerivationAlgorithm = "PBKDF2-SHA256"
                };

                byte[] derivedKey = _securityService.DeriveKey(MasterPassword, salt, newVault.KeyDerivationIterations);

                // Adicionar credenciais iniciais de demonstração
                string plainPassword = "super_secret_mcp_password_2026";
                byte[] encryptedPassBytes = _securityService.Encrypt(System.Text.Encoding.UTF8.GetBytes(plainPassword), derivedKey);
                string encryptedPassBase64 = Convert.ToBase64String(encryptedPassBytes);

                newVault.Credentials.Add(new Credential
                {
                    Domain = "github.com",
                    Username = "dev_ai",
                    EncryptedPassword = encryptedPassBase64
                });

                await _vaultRepository.SaveVaultAsync(_defaultVaultPath, newVault, derivedKey);

                _currentVault = newVault;
                _currentVaultKey = derivedKey;
                IsVaultOpen = true;
                StatusMessage = "Novo cofre criado com credencial de teste para 'github.com'.";
                LoadCredentialsList();
            }
        }
        catch (SecurityException ex)
        {
            StatusMessage = $"Erro de Segurança: {ex.Message}";
            IsVaultOpen = false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erro: {ex.Message}";
            IsVaultOpen = false;
        }
    }

    [RelayCommand]
    private async Task CreateBackupAsync()
    {
        if (!IsVaultOpen)
        {
            StatusMessage = "Abra o cofre primeiro antes de criar backup.";
            return;
        }

        try
        {
            StatusMessage = "Gerando backup físico criptografado...";
            string backupPath = await _backupService.CreateBackupAsync(_defaultVaultPath, _defaultBackupDirectory);
            StatusMessage = $"Backup gerado e assinado com SHA-256 em: {Path.GetFileName(backupPath)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Falha no backup: {ex.Message}";
        }
    }

    private void LoadCredentialsList()
    {
        Credentials.Clear();
        if (_currentVault != null)
        {
            foreach (var cred in _currentVault.Credentials)
            {
                Credentials.Add(cred);
            }
        }
    }

    // Método assíncrono que exibe o Modal de Consentimento na thread da UI
    private async Task<ConsentResponse> HandleMcpConsentRequestAsync(string requester, string domain)
    {
        // Se o cofre estiver fechado na UI, não podemos fornecer as senhas.
        // O usuário precisará destravar o cofre.
        if (!IsVaultOpen || _currentVault == null || _currentVaultKey == null)
        {
            StatusMessage = $"MCP solicitou '{domain}', mas o cofre está trancado.";
            return new ConsentResponse { Approved = false };
        }

        // Buscar a credencial correspondente ao domínio solicitado
        Credential? targetCredential = null;
        foreach (var cred in _currentVault.Credentials)
        {
            if (string.Equals(cred.Domain, domain, StringComparison.OrdinalIgnoreCase))
            {
                targetCredential = cred;
                break;
            }
        }

        if (targetCredential == null)
        {
            StatusMessage = $"Solicitação MCP para '{domain}' negada: Domínio não encontrado.";
            return new ConsentResponse { Approved = false };
        }

        // Abrir o diálogo na UI do Avalonia
        var tcs = new TaskCompletionSource<ConsentResponse>();

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var consentVm = new McpConsentViewModel
            {
                Requester = requester,
                Domain = domain,
                Username = targetCredential.Username,
                IsMasterPasswordRequired = false // Poderia ser setado como true se desejássemos re-verificar a senha mestre
            };

            var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            if (desktop?.MainWindow != null)
            {
                var dialog = new McpConsentWindow
                {
                    DataContext = consentVm
                };

                // Exibe como janela modal bloqueando a interação apenas com a MainWindow
                var approved = await dialog.ShowDialog<bool>(desktop.MainWindow);

                if (approved)
                {
                    try
                    {
                        // Descriptografar a senha
                        byte[] encryptedBytes = Convert.FromBase64String(targetCredential.EncryptedPassword);
                        byte[] decryptedBytes = _securityService.Decrypt(encryptedBytes, _currentVaultKey);
                        string plainPassword = System.Text.Encoding.UTF8.GetString(decryptedBytes);

                        tcs.SetResult(new ConsentResponse
                        {
                            Approved = true,
                            Username = targetCredential.Username,
                            Password = plainPassword
                        });
                    }
                    catch (Exception ex)
                    {
                        StatusMessage = $"Falha ao descriptografar credencial via MCP: {ex.Message}";
                        tcs.SetResult(new ConsentResponse { Approved = false });
                    }
                }
                else
                {
                    tcs.SetResult(new ConsentResponse { Approved = false });
                }
            }
            else
            {
                tcs.SetResult(new ConsentResponse { Approved = false });
            }
        });

        return await tcs.Task;
    }
}
