using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Mithril.Domain.Interfaces;
using Mithril.Infrastructure.Mcp;
using Mithril.Infrastructure.Persistence;
using Mithril.Infrastructure.Security;
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

        // Inicializar e rodar o Servidor MCP em background
        var mcpServer = Services.GetRequiredService<McpServerService>();
        mcpServer.Start();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainViewModel = Services.GetRequiredService<MainViewModel>();
            desktop.MainWindow = new MainWindow
            {
                DataContext = mainViewModel,
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