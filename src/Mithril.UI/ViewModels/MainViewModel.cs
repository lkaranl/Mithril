using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
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
                
                // Ler metadados de derivação diretamente do arquivo físico (campos públicos)
                string vaultContent = await File.ReadAllTextAsync(_defaultVaultPath);
                using var doc = JsonDocument.Parse(vaultContent);
                var root = doc.RootElement;

                // Tenta extrair propriedades públicas (com suporte a variações de case JSON)
                string saltBase64 = string.Empty;
                if (root.TryGetProperty("KeyDerivationSalt", out var saltProp) || root.TryGetProperty("keyDerivationSalt", out saltProp))
                {
                    saltBase64 = saltProp.GetString() ?? string.Empty;
                }

                int iterations = 600000;
                if (root.TryGetProperty("KeyDerivationIterations", out var iterProp) || root.TryGetProperty("keyDerivationIterations", out iterProp))
                {
                    iterations = iterProp.GetInt32();
                }

                byte[] salt = Convert.FromBase64String(saltBase64);
                
                // Derivar a chave real usando o salt e as iterações reais do arquivo
                byte[] derivedKey = _securityService.DeriveKey(MasterPassword, salt, iterations);

                // Agora carrega descriptografando o payload real com a chave correta
                _currentVault = await _vaultRepository.LoadVaultAsync(_defaultVaultPath, derivedKey);
                _currentVaultKey = derivedKey;
                
                IsVaultOpen = true;
                StatusMessage = $"Cofre aberto. {_currentVault.Credentials.Count} credenciais carregadas.";
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

                // Adicionar credenciais iniciais de demonstração (Web)
                string plainPassword = "super_secret_mcp_password_2026";
                byte[] encryptedPassBytes = _securityService.Encrypt(System.Text.Encoding.UTF8.GetBytes(plainPassword), derivedKey);
                string encryptedPassBase64 = Convert.ToBase64String(encryptedPassBytes);

                newVault.Credentials.Add(new Credential
                {
                    Type = CredentialType.Web,
                    Domain = "github.com",
                    Username = "dev_ai",
                    EncryptedPassword = encryptedPassBase64
                });

                // Adicionar credenciais iniciais de demonstração (API de Reciprocidade)
                string mockSecret = "client_secret_super_secret_reciprocidade_key";
                byte[] encryptedSecretBytes = _securityService.Encrypt(System.Text.Encoding.UTF8.GetBytes(mockSecret), derivedKey);
                string encryptedSecretBase64 = Convert.ToBase64String(encryptedSecretBytes);

                newVault.Credentials.Add(new Credential
                {
                    Type = CredentialType.ApiToken,
                    Domain = "reciprocidade",
                    Username = "client_id_reciprocidade",
                    EncryptedPassword = encryptedSecretBase64,
                    TokenUrl = "https://api.reciprocidade.com.br/v1/auth/token"
                });

                await _vaultRepository.SaveVaultAsync(_defaultVaultPath, newVault, derivedKey);

                _currentVault = newVault;
                _currentVaultKey = derivedKey;
                IsVaultOpen = true;
                StatusMessage = "Novo cofre criado com credenciais de teste para 'github.com' (Web) e 'reciprocidade' (API).";
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
                            Password = plainPassword,
                            TokenUrl = targetCredential.TokenUrl
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

    // --- PROPRIEDADES E COMANDOS DE CADASTRO E EDIÇÃO DE CREDENCIAIS ---

    private Guid? _editingCredentialId;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private bool _isAddFormOpen;

    [ObservableProperty]
    private CredentialType _newCredentialType = CredentialType.Web;

    [ObservableProperty]
    private string _newDomain = string.Empty;

    [ObservableProperty]
    private string _newUsername = string.Empty;

    [ObservableProperty]
    private string _newPassword = string.Empty;

    [ObservableProperty]
    private string _newTokenUrl = string.Empty;

    [RelayCommand]
    private void ToggleAddForm()
    {
        IsAddFormOpen = !IsAddFormOpen;
        if (IsAddFormOpen)
        {
            // Limpar formulário para novo cadastro
            NewDomain = string.Empty;
            NewUsername = string.Empty;
            NewPassword = string.Empty;
            NewTokenUrl = string.Empty;
            NewCredentialType = CredentialType.Web;
            IsEditing = false;
            _editingCredentialId = null;
        }
    }

    [RelayCommand]
    private void EditCredential(Credential credential)
    {
        if (credential == null || _currentVaultKey == null) return;

        try
        {
            // Descriptografar a senha/secret para preencher no formulário
            byte[] encryptedBytes = Convert.FromBase64String(credential.EncryptedPassword);
            byte[] decryptedBytes = _securityService.Decrypt(encryptedBytes, _currentVaultKey);
            string plainPassword = System.Text.Encoding.UTF8.GetString(decryptedBytes);

            // Carrega no formulário
            NewDomain = credential.Domain;
            NewUsername = credential.Username;
            NewTokenUrl = credential.TokenUrl;
            NewCredentialType = credential.Type;
            NewPassword = plainPassword;

            _editingCredentialId = credential.Id;
            IsEditing = true;
            IsAddFormOpen = true; // Abre o painel/formulário
            StatusMessage = $"Editando credencial para '{credential.Domain}'...";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Falha ao descriptografar credencial para edição: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveNewCredentialAsync()
    {
        if (string.IsNullOrWhiteSpace(NewDomain) || string.IsNullOrWhiteSpace(NewUsername) || string.IsNullOrWhiteSpace(NewPassword))
        {
            StatusMessage = "Os campos Domínio/API, Usuário/Client ID e Senha/Client Secret são obrigatórios.";
            return;
        }

        if (NewCredentialType == CredentialType.ApiToken && string.IsNullOrWhiteSpace(NewTokenUrl))
        {
            StatusMessage = "A URL de Token da API é obrigatória para credenciais de API.";
            return;
        }

        if (_currentVault == null || _currentVaultKey == null)
        {
            StatusMessage = "O cofre precisa estar aberto para salvar uma credencial.";
            return;
        }

        try
        {
            StatusMessage = "Criptografando e salvando alterações...";

            // Criptografar a nova senha/secret
            byte[] encryptedBytes = _securityService.Encrypt(System.Text.Encoding.UTF8.GetBytes(NewPassword), _currentVaultKey);
            string encryptedBase64 = Convert.ToBase64String(encryptedBytes);

            if (IsEditing && _editingCredentialId.HasValue)
            {
                // Localizar e atualizar a credencial existente
                Credential? target = null;
                foreach (var c in _currentVault.Credentials)
                {
                    if (c.Id == _editingCredentialId.Value)
                    {
                        target = c;
                        break;
                    }
                }

                if (target != null)
                {
                    target.Type = NewCredentialType;
                    target.Domain = NewDomain.Trim();
                    target.Username = NewUsername.Trim();
                    target.EncryptedPassword = encryptedBase64;
                    target.TokenUrl = NewCredentialType == CredentialType.ApiToken ? NewTokenUrl.Trim() : string.Empty;
                    target.LastModifiedAt = DateTime.UtcNow;

                    StatusMessage = $"Credencial para '{target.Domain}' editada com sucesso!";
                }
                else
                {
                    StatusMessage = "Credencial original não encontrada no cofre.";
                }
            }
            else
            {
                // Criar nova credencial
                var newCred = new Credential
                {
                    Type = NewCredentialType,
                    Domain = NewDomain.Trim(),
                    Username = NewUsername.Trim(),
                    EncryptedPassword = encryptedBase64,
                    TokenUrl = NewCredentialType == CredentialType.ApiToken ? NewTokenUrl.Trim() : string.Empty
                };

                _currentVault.Credentials.Add(newCred);
                StatusMessage = $"Nova credencial para '{newCred.Domain}' adicionada com sucesso!";
            }

            // Persistir no arquivo físico
            await _vaultRepository.SaveVaultAsync(_defaultVaultPath, _currentVault, _currentVaultKey);

            IsAddFormOpen = false;
            IsEditing = false;
            _editingCredentialId = null;

            // Recarregar a lista
            LoadCredentialsList();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erro ao salvar alterações no cofre: {ex.Message}";
        }
    }
}
