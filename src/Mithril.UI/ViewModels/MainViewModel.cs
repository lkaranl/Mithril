using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Domain.Exceptions;
using Mithril.Domain.Interfaces;
using Mithril.Domain.Models;
using Mithril.Infrastructure.Mcp;
using Mithril.UI.Services;
using Mithril.UI.Views;

namespace Mithril.UI.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly ISecurityService _securityService;
    private readonly IVaultRepository _vaultRepository;
    private readonly IBackupService _backupService;
    private readonly McpConsentService _mcpConsentService;
    private readonly McpSseServerService _sseServer;
    private readonly ITokenExchangeService _tokenExchangeService;

    private byte[]? _currentVaultKey;
    private VaultData? _currentVault;
    private readonly string _defaultVaultPath;
    private readonly string _defaultBackupDirectory;

    [ObservableProperty]
    private string _statusMessage = "Cofre Fechado. Crie ou abra seu cofre.";

    [ObservableProperty]
    private string _notificationMessage = string.Empty;

    [ObservableProperty]
    private string _notificationType = "Info"; // Info, Success, Error

    [ObservableProperty]
    private bool _isNotificationOpen;

    [ObservableProperty]
    private bool _isVaultOpen;

    [ObservableProperty]
    private string _masterPassword = string.Empty;

    [ObservableProperty]
    private ObservableCollection<Credential> _credentials = new();

    [ObservableProperty]
    private ObservableCollection<Credential> _filteredCredentials = new();

    [ObservableProperty]
    private ObservableCollection<string> _categories = new() { "Geral", "Trabalho", "Pessoal", "Produção", "Homologação", "Teste" };

    [ObservableProperty]
    private ObservableCollection<string> _filterCategories = new() { "Todos", "Geral", "Trabalho", "Pessoal", "Produção", "Homologação", "Teste" };

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedCategoryFilter = "Todos";

    [ObservableProperty]
    private bool _isSseServerActive;

    /// <summary>
    /// Quando verdadeiro (padrão), exibe o modal de consentimento a cada solicitação MCP.
    /// Quando falso, aprova automaticamente sem interação do usuário.
    /// </summary>
    [ObservableProperty]
    private bool _isConsentRequired = true;

    [ObservableProperty]
    private Credential? _selectedCredentialForDetails;

    [ObservableProperty]
    private bool _isDetailsPanelOpen;

    [ObservableProperty]
    private string _decryptedPasswordForDetails = string.Empty;

    [ObservableProperty]
    private bool _isPasswordDetailsVisible;

    public MainViewModel(
        ISecurityService securityService,
        IVaultRepository vaultRepository,
        IBackupService backupService,
        McpConsentService mcpConsentService,
        McpSseServerService sseServer,
        ITokenExchangeService tokenExchangeService)
    {
        _securityService = securityService;
        _vaultRepository = vaultRepository;
        _backupService = backupService;
        _mcpConsentService = mcpConsentService;
        _sseServer = sseServer;
        _tokenExchangeService = tokenExchangeService;

        // Locais padrão para o cofre e backups dentro da pasta do usuário ou app
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string mithrilDir = Path.Combine(appData, "Mithril");
        _defaultVaultPath = Path.Combine(mithrilDir, "vault.json");
        _defaultBackupDirectory = Path.Combine(mithrilDir, "backups");

        // Registrar o callback de consentimento que conecta o Servidor MCP com a UI
        _mcpConsentService.OnConsentRequested = HandleMcpConsentRequestAsync;

        // O servidor de rede inicia desativado por padrão por motivos de segurança (Secure by Default)
        _isSseServerActive = false;
    }

    partial void OnSearchTextChanged(string value) => ApplyFilters();
    partial void OnSelectedCategoryFilterChanged(string value) => ApplyFilters();

    private void ApplyFilters()
    {
        FilteredCredentials.Clear();
        foreach (var cred in Credentials)
        {
            if (string.IsNullOrEmpty(cred.Category))
            {
                cred.Category = "Geral";
            }

            bool matchCategory = SelectedCategoryFilter == "Todos" || string.Equals(cred.Category, SelectedCategoryFilter, StringComparison.OrdinalIgnoreCase);
            bool matchSearch = string.IsNullOrWhiteSpace(SearchText) ||
                               cred.Domain.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                               cred.Username.Contains(SearchText, StringComparison.OrdinalIgnoreCase);

            if (matchCategory && matchSearch)
            {
                FilteredCredentials.Add(cred);
            }
        }
    }

    private bool _isChangingSseState;

    partial void OnIsSseServerActiveChanged(bool value)
    {
        if (_isChangingSseState) return;

        if (value)
        {
            // O usuário tentou ativar o servidor de rede.
            // Abrimos o diálogo modal de confirmação na thread da UI.
            _ = Task.Run(async () =>
            {
                bool confirmed = false;

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
                    if (desktop?.MainWindow != null)
                    {
                        var dialog = new SseConfirmWindow();
                        confirmed = await dialog.ShowDialog<bool>(desktop.MainWindow);
                    }
                });

                if (confirmed)
                {
                    _sseServer.Start();
                    StatusMessage = "⚠️ AVISO DE SEGURANÇA: Servidor MCP de rede local ativado na porta 12121! Portas expostas podem ser acessadas por outros hosts da LAN.";
                }
                else
                {
                    // Reverte o switch na UI sem disparar recursão
                    _isChangingSseState = true;
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        IsSseServerActive = false;
                    });
                    _isChangingSseState = false;

                    _sseServer.Stop();
                    StatusMessage = "🔒 Ativação cancelada pelo usuário. Servidor de rede permanece inativo.";
                }
            });
        }
        else
        {
            // O usuário tentou desativar o servidor de rede.
            // Abrimos o diálogo modal de confirmação de desconexão na thread da UI.
            _ = Task.Run(async () =>
            {
                bool confirmed = false;

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
                    if (desktop?.MainWindow != null)
                    {
                        var dialog = new SseDisconnectWindow();
                        confirmed = await dialog.ShowDialog<bool>(desktop.MainWindow);
                    }
                });

                if (confirmed)
                {
                    _sseServer.Stop();
                    StatusMessage = "🔒 Servidor MCP de rede local desativado com segurança.";
                }
                else
                {
                    // Reverte o switch na UI de volta para true (mantém ligado) sem disparar recursão
                    _isChangingSseState = true;
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        IsSseServerActive = true;
                    });
                    _isChangingSseState = false;

                    StatusMessage = "⚠️ Desativação cancelada pelo usuário. Servidor de rede permanece ativo na porta 12121.";
                }
            });
        }
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
                    EncryptedPassword = encryptedPassBase64,
                    Category = "Pessoal"
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
                    TokenUrl = "https://api.reciprocidade.com.br/v1/auth/token",
                    Category = "Trabalho"
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
            ShowNotification("Erro de Segurança: Senha Mestre incorreta ou dados corrompidos.", "Error");
            IsVaultOpen = false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erro: {ex.Message}";
            ShowNotification($"Erro ao abrir o cofre: {ex.Message}", "Error");
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
            string msg = $"Backup gerado e assinado com sucesso em:\n{backupPath}";
            StatusMessage = $"Backup gerado com sucesso em: {backupPath}";
            ShowNotification(msg, "Success");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Falha no backup: {ex.Message}";
            ShowNotification($"Erro físico ao gerar backup: {ex.Message}", "Error");
        }
    }

    [RelayCommand]
    private void OpenBackupFolder()
    {
        try
        {
            if (!Directory.Exists(_defaultBackupDirectory))
            {
                Directory.CreateDirectory(_defaultBackupDirectory);
            }

            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start("explorer.exe", _defaultBackupDirectory);
            }
            else if (OperatingSystem.IsMacOS())
            {
                System.Diagnostics.Process.Start("open", _defaultBackupDirectory);
            }
            else if (OperatingSystem.IsLinux())
            {
                System.Diagnostics.Process.Start("xdg-open", _defaultBackupDirectory);
            }
            else
            {
                StatusMessage = "Sistema operacional não suportado para abrir a pasta.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Falha ao abrir pasta de backup: {ex.Message}";
        }
    }


    [RelayCommand]
    private async Task RestoreBackupAsync()
    {
        if (!IsVaultOpen)
        {
            ShowNotification("Abra o cofre primeiro antes de restaurar um backup.", "Info");
            return;
        }

        try
        {
            var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            if (desktop?.MainWindow == null) return;

            var storageProvider = TopLevel.GetTopLevel(desktop.MainWindow)?.StorageProvider;
            if (storageProvider == null) return;

            var options = new FilePickerOpenOptions
            {
                Title = "Selecionar Arquivo de Backup do Mithril",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Backups do Mithril") { Patterns = new[] { "*.json", "*_backup*" } }
                }
            };

            var files = await storageProvider.OpenFilePickerAsync(options);
            if (files == null || files.Count == 0) return;

            string backupFilePath = files[0].Path.LocalPath;

            StatusMessage = "Restaurando backup e verificando integridade...";
            bool restored = await _backupService.RestoreBackupAsync(backupFilePath, _defaultVaultPath);

            if (restored)
            {
                try
                {
                    var reloadedVault = await _vaultRepository.LoadVaultAsync(_defaultVaultPath, _currentVaultKey!);
                    _currentVault = reloadedVault;
                    LoadCredentialsList();
                    StatusMessage = "Backup restaurado e cofre recarregado com sucesso!";
                    ShowNotification("Backup restaurado e cofre recarregado com sucesso!", "Success");
                }
                catch (SecurityException)
                {
                    _currentVault = null;
                    _currentVaultKey = null;
                    IsVaultOpen = false;
                    MasterPassword = string.Empty;
                    Credentials.Clear();
                    FilteredCredentials.Clear();
                    StatusMessage = "🔒 Backup restaurado com sucesso! Insira a Senha Mestre do backup para desbloquear.";
                    ShowNotification("Backup restaurado. Insira a Senha Mestre do backup para desbloquear o cofre.", "Info");
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Falha ao restaurar backup: {ex.Message}";
            ShowNotification($"Erro ao restaurar backup: {ex.Message}", "Error");
        }
    }

    private void LoadCredentialsList()
    {
        Credentials.Clear();
        if (_currentVault != null)
        {
            foreach (var cred in _currentVault.Credentials)
            {
                if (string.IsNullOrEmpty(cred.Category))
                {
                    cred.Category = "Geral";
                }
                
                // Clona para forçar atualização visual completa dos containers do Avalonia
                var displayCred = new Credential
                {
                    Id = cred.Id,
                    Type = cred.Type,
                    Domain = cred.Domain,
                    Username = cred.Username,
                    EncryptedPassword = cred.EncryptedPassword,
                    TokenUrl = cred.TokenUrl,
                    Category = cred.Category,
                    CreatedAt = cred.CreatedAt,
                    LastModifiedAt = cred.LastModifiedAt
                };
                Credentials.Add(displayCred);
            }
        }
        ApplyFilters();
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

        // Se o consentimento automático estiver ativo, aprovar sem abrir o modal
        if (!IsConsentRequired)
        {
            try
            {
                byte[] encryptedBytes = Convert.FromBase64String(targetCredential.EncryptedPassword);
                byte[] decryptedBytes = _securityService.Decrypt(encryptedBytes, _currentVaultKey);
                string plainPassword = System.Text.Encoding.UTF8.GetString(decryptedBytes);
                StatusMessage = $"MCP: acesso automático concedido para '{domain}' (consentimento desativado).";
                return new ConsentResponse
                {
                    Approved = true,
                    Username = targetCredential.Username,
                    Password = plainPassword,
                    TokenUrl = targetCredential.TokenUrl
                };
            }
            catch (Exception ex)
            {
                StatusMessage = $"Falha ao descriptografar credencial via MCP: {ex.Message}";
                return new ConsentResponse { Approved = false };
            }
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

    public bool IsNewCredentialTypeWeb
    {
        get => NewCredentialType == CredentialType.Web;
        set
        {
            if (value && NewCredentialType != CredentialType.Web)
            {
                NewCredentialType = CredentialType.Web;
            }
        }
    }

    public bool IsNewCredentialTypeApi
    {
        get => NewCredentialType == CredentialType.ApiToken;
        set
        {
            if (value && NewCredentialType != CredentialType.ApiToken)
            {
                NewCredentialType = CredentialType.ApiToken;
            }
        }
    }

    partial void OnNewCredentialTypeChanged(CredentialType value)
    {
        OnPropertyChanged(nameof(IsNewCredentialTypeWeb));
        OnPropertyChanged(nameof(IsNewCredentialTypeApi));
    }

    [ObservableProperty]
    private string _newDomain = string.Empty;

    [ObservableProperty]
    private string _newUsername = string.Empty;

    [ObservableProperty]
    private string _newPassword = string.Empty;

    [ObservableProperty]
    private string _newTokenUrl = string.Empty;

    [ObservableProperty]
    private string _newCategory = "Geral";

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
            NewCategory = "Geral";
            IsEditing = false;
            _editingCredentialId = null;
        }
    }

    [RelayCommand]
    private void EditCredential(Credential credential)
    {
        if (credential == null || _currentVaultKey == null) return;
        IsDetailsPanelOpen = false; // Fechar detalhes se aberto

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
            NewCategory = string.IsNullOrEmpty(credential.Category) ? "Geral" : credential.Category;

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
    private void CloneCredential(Credential credential)
    {
        if (credential == null || _currentVaultKey == null) return;

        try
        {
            byte[] encryptedBytes = Convert.FromBase64String(credential.EncryptedPassword);
            byte[] decryptedBytes = _securityService.Decrypt(encryptedBytes, _currentVaultKey);
            string plainPassword = System.Text.Encoding.UTF8.GetString(decryptedBytes);

            // Carrega no formulário
            NewDomain = credential.Domain;
            NewUsername = credential.Username;
            NewTokenUrl = credential.TokenUrl;
            NewCredentialType = credential.Type;
            NewPassword = plainPassword;
            NewCategory = string.IsNullOrEmpty(credential.Category) ? "Geral" : credential.Category;

            IsEditing = false;
            _editingCredentialId = null;
            IsAddFormOpen = true; // Abre o painel/formulário
            StatusMessage = $"Clonando credencial de '{credential.Domain}'...";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Falha ao clonar credencial: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ShowDetails(Credential credential)
    {
        if (credential == null) return;
        SelectedCredentialForDetails = credential;
        DecryptedPasswordForDetails = string.Empty;
        IsPasswordDetailsVisible = false;
        IsDetailsPanelOpen = true;
    }

    [RelayCommand]
    private void CloseDetails()
    {
        IsDetailsPanelOpen = false;
        SelectedCredentialForDetails = null;
        DecryptedPasswordForDetails = string.Empty;
        IsPasswordDetailsVisible = false;
    }

    [RelayCommand]
    private void TogglePasswordDetailsVisibility()
    {
        if (SelectedCredentialForDetails == null || _currentVaultKey == null) return;
        
        if (IsPasswordDetailsVisible)
        {
            DecryptedPasswordForDetails = string.Empty;
            IsPasswordDetailsVisible = false;
        }
        else
        {
            try
            {
                byte[] encryptedBytes = Convert.FromBase64String(SelectedCredentialForDetails.EncryptedPassword);
                byte[] decryptedBytes = _securityService.Decrypt(encryptedBytes, _currentVaultKey);
                DecryptedPasswordForDetails = System.Text.Encoding.UTF8.GetString(decryptedBytes);
                IsPasswordDetailsVisible = true;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Erro ao descriptografar: {ex.Message}";
            }
        }
    }


    [RelayCommand]
    private async Task SaveNewCredentialAsync()
    {
        if (string.IsNullOrWhiteSpace(NewDomain) || string.IsNullOrWhiteSpace(NewUsername) || string.IsNullOrWhiteSpace(NewPassword))
        {
            StatusMessage = "Os campos Domínio/API, Usuário/Client ID e Senha/Client Secret são obrigatórios.";
            ShowNotification("Por favor, preencha todos os campos obrigatórios.", "Info");
            return;
        }

        if (NewCredentialType == CredentialType.ApiToken && string.IsNullOrWhiteSpace(NewTokenUrl))
        {
            StatusMessage = "A URL de Token da API é obrigatória para credenciais de API.";
            ShowNotification("Por favor, informe a URL de Token da API.", "Info");
            return;
        }

        if (_currentVault == null || _currentVaultKey == null)
        {
            StatusMessage = "O cofre precisa estar aberto para salvar uma credencial.";
            ShowNotification("O cofre precisa estar aberto para salvar credenciais.", "Error");
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
                    target.Category = NewCategory;
                    target.LastModifiedAt = DateTime.UtcNow;

                    StatusMessage = $"Credencial para '{target.Domain}' editada com sucesso!";
                    ShowNotification($"Credencial para '{target.Domain}' editada com sucesso!", "Success");
                }
                else
                {
                    StatusMessage = "Credencial original não encontrada no cofre.";
                    ShowNotification("Erro: Credencial original não encontrada no cofre.", "Error");
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
                    TokenUrl = NewCredentialType == CredentialType.ApiToken ? NewTokenUrl.Trim() : string.Empty,
                    Category = NewCategory
                };

                _currentVault.Credentials.Add(newCred);
                StatusMessage = $"Nova credencial para '{newCred.Domain}' adicionada com sucesso!";
                ShowNotification($"Nova credencial para '{newCred.Domain}' adicionada com sucesso!", "Success");
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
            ShowNotification($"Erro ao salvar no cofre: {ex.Message}", "Error");
        }
    }

    [RelayCommand]
    private void GeneratePassword()
    {
        NewPassword = GenerateStrongPassword(16);
        StatusMessage = "Senha/Secret forte gerada com sucesso!";
    }

    [RelayCommand]
    private async Task CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        var clipboard = desktop?.MainWindow?.Clipboard;
        if (clipboard != null)
        {
            await clipboard.SetTextAsync(text);
            StatusMessage = "Copiado para a área de transferência!";
        }
    }

    [RelayCommand]
    private async Task CopyPasswordAsync(Credential credential)
    {
        if (credential == null || _currentVaultKey == null) return;
        try
        {
            byte[] encryptedBytes = Convert.FromBase64String(credential.EncryptedPassword);
            byte[] decryptedBytes = _securityService.Decrypt(encryptedBytes, _currentVaultKey);
            string plainPassword = System.Text.Encoding.UTF8.GetString(decryptedBytes);

            if (credential.Type == CredentialType.ApiToken)
            {
                StatusMessage = $"Gerando token JWT para a API '{credential.Domain}'...";
                
                if (string.IsNullOrEmpty(credential.TokenUrl) || string.IsNullOrEmpty(credential.Username) || string.IsNullOrEmpty(plainPassword))
                {
                    StatusMessage = "Erro: Configurações de API incompletas para gerar o token.";
                    ShowNotification("Erro: Configurações de API incompletas para gerar o token.", "Error");
                    return;
                }

                string jwtToken = await _tokenExchangeService.GetAccessTokenAsync(credential.TokenUrl, credential.Username, plainPassword);
                await CopyToClipboard(jwtToken);
                string successMsg = $"🔒 Token JWT para '{credential.Domain}' gerado e copiado!";
                StatusMessage = successMsg;
                ShowNotification(successMsg, "Success");
            }
            else
            {
                await CopyToClipboard(plainPassword);
                string successMsg = $"🔒 Senha para '{credential.Domain}' copiada com sucesso!";
                StatusMessage = successMsg;
                ShowNotification(successMsg, "Success");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erro ao obter dados: {ex.Message}";
            ShowNotification($"Falha ao gerar Token JWT: {ex.Message}", "Error");
        }
    }

    [RelayCommand]
    private async Task DeleteCredentialAsync(Credential credential)
    {
        if (credential == null || _currentVault == null || _currentVaultKey == null) return;
        IsDetailsPanelOpen = false; // Fechar detalhes se aberto

        try
        {
            string identifier = credential.Type == CredentialType.ApiToken ? credential.Username : credential.Domain;
            bool confirmed = false;

            var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            if (desktop?.MainWindow != null)
            {
                var dialog = new DeleteConfirmWindow(identifier);
                confirmed = await dialog.ShowDialog<bool>(desktop.MainWindow);
            }

            if (confirmed)
            {
                Credential? target = null;
                foreach (var c in _currentVault.Credentials)
                {
                    if (c.Id == credential.Id)
                    {
                        target = c;
                        break;
                    }
                }

                if (target != null)
                {
                    _currentVault.Credentials.Remove(target);
                    await _vaultRepository.SaveVaultAsync(_defaultVaultPath, _currentVault, _currentVaultKey);
                    
                    StatusMessage = $"Credencial para '{identifier}' excluída com sucesso.";
                    ShowNotification($"Credencial para '{identifier}' excluída com sucesso.", "Success");
                    
                    LoadCredentialsList();
                }
                else
                {
                    ShowNotification("Erro: Credencial não encontrada para exclusão.", "Error");
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erro ao excluir credencial: {ex.Message}";
            ShowNotification($"Erro ao excluir: {ex.Message}", "Error");
        }
    }

    private static string GenerateStrongPassword(int length = 16)
    {
        const string upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        const string lower = "abcdefghijklmnopqrstuvwxyz";
        const string digits = "0123456789";
        const string specials = "!@#$%^&*()_+-=[]{}|;:,.<>?";
        string allChars = upper + lower + digits + specials;

        var bytes = new byte[length];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);

        var result = new System.Text.StringBuilder();
        result.Append(upper[bytes[0] % upper.Length]);
        result.Append(lower[bytes[1] % lower.Length]);
        result.Append(digits[bytes[2] % digits.Length]);
        result.Append(specials[bytes[3] % specials.Length]);

        for (int i = 4; i < length; i++)
        {
            result.Append(allChars[bytes[i] % allChars.Length]);
        }

        var rawResult = result.ToString().ToCharArray();
        var shuffleBytes = new byte[rawResult.Length];
        System.Security.Cryptography.RandomNumberGenerator.Fill(shuffleBytes);
        for (int i = rawResult.Length - 1; i > 0; i--)
        {
            int j = shuffleBytes[i] % (i + 1);
            var temp = rawResult[i];
            rawResult[i] = rawResult[j];
            rawResult[j] = temp;
        }

        return new string(rawResult);
    }

    private void ShowNotification(string message, string type = "Info")
    {
        NotificationMessage = message;
        NotificationType = type;
        IsNotificationOpen = true;

        if (type != "Error")
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(5000);
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (NotificationMessage == message)
                    {
                        IsNotificationOpen = false;
                    }
                });
            });
        }
    }

    [RelayCommand]
    private void CloseNotification()
    {
        IsNotificationOpen = false;
    }
}
