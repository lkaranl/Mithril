using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Mithril.Domain.Interfaces;
using Mithril.Domain.Models;

namespace Mithril.Infrastructure.Mcp;

public class McpServerService
{
    private readonly IMcpConsentService _consentService;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public McpServerService(IMcpConsentService consentService)
    {
        _consentService = consentService;
    }

    public void Start()
    {
        if (_listenTask != null) return;

        _cts = new CancellationTokenSource();
        // Iniciamos a escuta do stdin em uma Task dedicada que roda em background
        _listenTask = Task.Run(() => ListenToStdInAsync(_cts.Token));
        LogToErrorStream("Servidor MCP local inicializado e aguardando mensagens via stdio...");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try
        {
            _listenTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { }
        _listenTask = null;
        LogToErrorStream("Servidor MCP parado.");
    }

    private async Task ListenToStdInAsync(CancellationToken cancellationToken)
    {
        // Garante codificação UTF-8 pura
        Console.InputEncoding = System.Text.Encoding.UTF8;
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        using var reader = new StreamReader(Console.OpenStandardInput(), Console.InputEncoding);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line == null) break; // End of Stream (EOF)

                if (!string.IsNullOrWhiteSpace(line))
                {
                    // Processa a mensagem em background para não bloquear a leitura de novas mensagens
                    _ = Task.Run(() => ProcessMessageAsync(line, cancellationToken), cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                LogToErrorStream($"Erro ao ler do stdin: {ex.Message}");
            }
        }
    }

    private async Task ProcessMessageAsync(string messageJson, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(messageJson);
            var root = doc.RootElement;

            // Valida estrutura básica do JSON-RPC 2.0
            if (!root.TryGetProperty("jsonrpc", out var rpcProp) || rpcProp.GetString() != "2.0")
            {
                LogToErrorStream("Mensagem ignorada: Não é JSON-RPC 2.0 válido.");
                return;
            }

            // Identifica se é uma Request (tem ID) ou Notification (sem ID)
            long? id = null;
            if (root.TryGetProperty("id", out var idProp))
            {
                if (idProp.ValueKind == JsonValueKind.Number) id = idProp.GetInt64();
                else if (idProp.ValueKind == JsonValueKind.String && long.TryParse(idProp.GetString(), out var idParsed)) id = idParsed;
            }

            if (!root.TryGetProperty("method", out var methodProp))
            {
                // Respostas do cliente ao servidor, ou mensagens inválidas
                return;
            }

            var method = methodProp.GetString() ?? "";

            if (id.HasValue)
            {
                // É um Request (espera resposta)
                await HandleRequestAsync(id.Value, method, root, cancellationToken);
            }
            else
            {
                // É uma Notification (não envia resposta)
                HandleNotification(method, root);
            }
        }
        catch (JsonException ex)
        {
            LogToErrorStream($"Falha ao parsear JSON: {ex.Message}");
        }
        catch (Exception ex)
        {
            LogToErrorStream($"Erro ao processar mensagem: {ex.Message}");
        }
    }

    private async Task HandleRequestAsync(long id, string method, JsonElement root, CancellationToken cancellationToken)
    {
        LogToErrorStream($"Recebido request ID: {id}, Método: {method}");

        switch (method)
        {
            case "initialize":
                SendResponse(id, new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new { tools = new { } },
                    serverInfo = new { name = "Mithril-Vault-MCP", version = "1.0.0" }
                });
                break;

            case "tools/list":
                SendResponse(id, new
                {
                    tools = new[]
                    {
                        new
                        {
                            name = "get_credentials",
                            description = "Solicita de forma segura as credenciais (usuário e senha) para um domínio específico do gerenciador de senhas. Esta operação exige o consentimento explícito do usuário final na tela do aplicativo.",
                            inputSchema = new
                            {
                                type = "object",
                                properties = new
                                {
                                    domain = new
                                    {
                                        type = "string",
                                        description = "O domínio ou endereço do site/serviço para o qual as credenciais são solicitadas (ex: github.com, google.com)."
                                    }
                                },
                                required = new[] { "domain" }
                            }
                        }
                    }
                });
                break;

            case "tools/call":
                if (!root.TryGetProperty("params", out var paramsEl))
                {
                    SendError(id, -32602, "Parâmetros ausentes.");
                    return;
                }

                var toolName = paramsEl.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                if (toolName == "get_credentials")
                {
                    if (!paramsEl.TryGetProperty("arguments", out var argsEl) ||
                        !argsEl.TryGetProperty("domain", out var domainProp) ||
                        domainProp.ValueKind != JsonValueKind.String)
                    {
                        SendError(id, -32602, "Argumento 'domain' inválido ou ausente.");
                        return;
                    }

                    var domain = domainProp.GetString() ?? "";

                    try
                    {
                        // Solicita consentimento do usuário via serviço coordenador na UI
                        LogToErrorStream($"Aguardando consentimento do usuário para o domínio: {domain}");
                        
                        ConsentResponse consent = await _consentService.RequestConsentAsync("Agente de IA", domain);

                        if (consent.Approved)
                        {
                            LogToErrorStream($"Acesso aprovado pelo usuário para o domínio: {domain}");
                            SendResponse(id, new
                            {
                                content = new[]
                                {
                                    new
                                    {
                                        type = "text",
                                        text = $"username: {consent.Username}\npassword: {consent.Password}"
                                    }
                                }
                            });
                        }
                        else
                        {
                            LogToErrorStream($"Acesso rejeitado pelo usuário para o domínio: {domain}");
                            SendResponse(id, new
                            {
                                isError = true,
                                content = new[]
                                {
                                    new
                                    {
                                        type = "text",
                                        text = "Acesso às credenciais foi expressamente rejeitado pelo usuário."
                                    }
                                }
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        LogToErrorStream($"Erro no fluxo de consentimento: {ex.Message}");
                        SendError(id, -32001, $"Erro interno ao solicitar consentimento: {ex.Message}");
                    }
                }
                else
                {
                    SendError(id, -32601, $"Ferramenta não encontrada: {toolName}");
                }
                break;

            default:
                SendError(id, -32601, $"Método não encontrado: {method}");
                break;
        }
    }

    private void HandleNotification(string method, JsonElement root)
    {
        LogToErrorStream($"Recebida notificação: {method}");
        // Notifications não requerem respostas JSON-RPC
    }

    private void SendResponse(long id, object result)
    {
        var response = new
        {
            jsonrpc = "2.0",
            id = id,
            result = result
        };
        WriteToStdout(JsonSerializer.Serialize(response));
    }

    private void SendError(long id, int errorCode, string errorMessage)
    {
        var response = new
        {
            jsonrpc = "2.0",
            id = id,
            error = new { code = errorCode, message = errorMessage }
        };
        WriteToStdout(JsonSerializer.Serialize(response));
    }

    private void WriteToStdout(string rawJson)
    {
        lock (Console.Out)
        {
            Console.Out.WriteLine(rawJson);
            Console.Out.Flush();
        }
    }

    private void LogToErrorStream(string message)
    {
        // Importante: logs em servidores stdio DEVEM ir para o stderr
        lock (Console.Error)
        {
            Console.Error.WriteLine($"[Mithril MCP Server] {message}");
            Console.Error.Flush();
        }
    }
}
