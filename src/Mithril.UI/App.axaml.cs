using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Mithril.Domain.Interfaces;
using Mithril.Infrastructure.Mcp;
using Mithril.Infrastructure.Persistence;
using Mithril.Infrastructure.Security;
using Mithril.Infrastructure.Services;
using Mithril.UI.Services;
using Mithril.UI.ViewModels;
using Mithril.UI.Views;

namespace Mithril.UI;

public partial class App : Application
{
    public static IServiceProvider? Services { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var serviceCollection = new ServiceCollection();

        // 1. Registrar Serviços do Domínio e Infraestrutura
        serviceCollection.AddSingleton<ISecurityService, AesGcmSecurityService>();
        serviceCollection.AddSingleton<IVaultRepository, LocalFileVaultRepository>();
        serviceCollection.AddSingleton<IBackupService, VaultBackupService>();
        serviceCollection.AddSingleton<ITokenExchangeService, HttpTokenExchangeService>();

        // 2. Registrar o Coordenador de Consentimento MCP
        var mcpConsentService = new McpConsentService();
        serviceCollection.AddSingleton<IMcpConsentService>(mcpConsentService);
        serviceCollection.AddSingleton(mcpConsentService); // Permite injeção como concreto

        // 3. Registrar o Servidor MCP
        serviceCollection.AddSingleton<McpServerService>();

        // 4. Registrar ViewModels
        serviceCollection.AddTransient<MainViewModel>();

        // Construir o provedor
        Services = serviceCollection.BuildServiceProvider();

        // Obter o Servidor MCP
        var mcpServer = Services.GetRequiredService<McpServerService>();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainViewModel = Services.GetRequiredService<MainViewModel>();
            var mainWindow = new MainWindow
            {
                DataContext = mainViewModel,
            };

            desktop.MainWindow = mainWindow;

            // Roda o servidor MCP em background apenas após a janela estar totalmente aberta na tela (Evita travar o loop do Cocoa no Mac)
            mainWindow.Opened += (sender, e) =>
            {
                mcpServer.Start();
            };

            // Certifica-se de parar as threads do servidor MCP ao encerrar a GUI
            desktop.Exit += (sender, e) =>
            {
                mcpServer.Stop();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}