using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Mithril.Domain.Interfaces;
using Mithril.Domain.Models;

namespace Mithril.Infrastructure.Mcp;

public class McpSseServerService
{
    private readonly IMcpConsentService _consentService;
    private readonly ITokenExchangeService _tokenExchangeService;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenerTask;
    private readonly ConcurrentDictionary<string, SseSession> _sessions = new();
    private readonly int _port = 12121;

    public McpSseServerService(IMcpConsentService consentService, ITokenExchangeService tokenExchangeService)
    {
        _consentService = consentService;
        _tokenExchangeService = tokenExchangeService;
    }

    private class SseSession
    {
        public string Id { get; }
        public HttpListenerResponse Response { get; }
        public SemaphoreSlim WriteSemaphore { get; } = new(1, 1);

        public SseSession(string id, HttpListenerResponse response)
        {
            Id = id;
            Response = response;
        }
    }

    public void Start()
    {
        if (_listener != null) return;

        _listener = new HttpListener();
        _listener.IgnoreWriteExceptions = true;
        
        try
        {
            // Tenta escutar em todas as interfaces de rede para permitir acesso remoto na rede local
            _listener.Prefixes.Add($"http://*:{_port}/");
            _listener.Start();
            Console.Error.WriteLine($"[Mithril MCP SSE] Servidor rodando em todas as interfaces na porta {_port}...");
        }
        catch (HttpListenerException ex)
        {
            // Se falhar (por exemplo, no Windows/Mac sem privilégios administrativos para bind de '*'),
            // faz fallback para localhost.
            Console.Error.WriteLine($"[Mithril MCP SSE] Não foi possível escutar em todas as interfaces (erro de permissão). Tentando apenas em localhost... Detalhes: {ex.Message}");
            _listener = new HttpListener();
            _listener.IgnoreWriteExceptions = true;
            _listener.Prefixes.Add($"http://localhost:{_port}/");
            _listener.Start();
            Console.Error.WriteLine($"[Mithril MCP SSE] Servidor rodando localmente na porta {_port}...");
        }

        _cts = new CancellationTokenSource();
        _listenerTask = Task.Run(() => ListenConnectionsAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        
        foreach (var session in _sessions.Values)
        {
            try { session.Response.Close(); } catch { }
        }
        _sessions.Clear();

        try
        {
            _listenerTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Mithril MCP SSE] Erro ao parar thread do servidor: {ex.Message}");
        }
        
        _listener = null;
        _listenerTask = null;
        Console.Error.WriteLine("[Mithril MCP SSE] Servidor parado.");
    }

    private async Task ListenConnectionsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener != null && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleHttpRequestAsync(context, cancellationToken), cancellationToken);
            }
            catch (Exception)
            {
                break;
            }
        }
    }

    private async Task HandleHttpRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var response = context.Response;

        // Cabeçalhos CORS padrão do MCP
        response.Headers.Add("Access-Control-Allow-Origin", "*");
        response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, X-Session-Id");

        if (request.HttpMethod == "OPTIONS")
        {
            response.StatusCode = (int)HttpStatusCode.OK;
            response.Close();
            return;
        }

        try
        {
            if (request.HttpMethod == "GET" && request.Url?.AbsolutePath == "/sse")
            {
                await HandleSseConnectionAsync(context);
            }
            else if (request.HttpMethod == "POST" && request.Url?.AbsolutePath == "/message")
            {
                await HandleSseMessageAsync(context, cancellationToken);
            }
            else
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                response.Close();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Mithril MCP SSE] Erro ao tratar requisição HTTP: {ex.Message}");
            try
            {
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                response.Close();
            }
            catch { }
        }
    }

    private async Task HandleSseConnectionAsync(HttpListenerContext context)
    {
        var response = context.Response;
        
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.Add("Cache-Control", "no-cache");
        response.Headers.Add("Connection", "keep-alive");
        response.StatusCode = (int)HttpStatusCode.OK;

        string sessionId = Guid.NewGuid().ToString("N");
        var session = new SseSession(sessionId, response);
        _sessions.TryAdd(sessionId, session);

        Console.Error.WriteLine($"[Mithril MCP SSE] Nova conexão SSE estabelecida. Session ID: {sessionId}");

        // Na especificação oficial do MCP SSE, enviamos imediatamente o evento 'endpoint'
        // contendo a URL onde o cliente deve fazer POST das mensagens JSON-RPC.
        string endpointData = $"/message?sessionId={sessionId}";
        await SendSseEventAsync(session, "endpoint", endpointData);

        // Mantém a conexão aberta enviando pings (comentários SSE) periodicamente
        try
        {
            while (!session.Response.KeepAlive)
            {
                await Task.Delay(15000); // 15 segundos de intervalo de keep-alive
                await SendSsePingAsync(session);
            }
        }
        catch (Exception)
        {
            // Socket desconectado pelo cliente
        }
        finally
        {
            _sessions.TryRemove(sessionId, out _);
            Console.Error.WriteLine($"[Mithril MCP SSE] Conexão SSE encerrada. Session ID: {sessionId}");
            try { response.Close(); } catch { }
        }
    }

    private async Task HandleSseMessageAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var response = context.Response;

        string? sessionId = request.QueryString["sessionId"];
        if (string.IsNullOrEmpty(sessionId) || !_sessions.TryGetValue(sessionId, out var session))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            using (var writer = new StreamWriter(response.OutputStream))
            {
                await writer.WriteAsync("ID de sessão SSE inválido ou inativo.");
            }
            response.Close();
            return;
        }

        string requestBody;
        using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
        {
            requestBody = await reader.ReadToEndAsync();
        }

        // De acordo com o protocolo MCP SSE, o endpoint POST responde com HTTP 202 (Accepted) imediatamente.
        // As respostas reais do processamento JSON-RPC viajam assincronamente pelo canal SSE aberto.
        response.StatusCode = (int)HttpStatusCode.Accepted;
        response.Close();

        // Processa o request JSON-RPC em background
        _ = Task.Run(() => ProcessSseMessageAsync(session, requestBody, cancellationToken), cancellationToken);
    }

    private async Task ProcessSseMessageAsync(SseSession session, string messageJson, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(messageJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("jsonrpc", out var rpcProp) || rpcProp.GetString() != "2.0")
            {
                return;
            }

            long? id = null;
            if (root.TryGetProperty("id", out var idProp))
            {
                if (idProp.ValueKind == JsonValueKind.Number) id = idProp.GetInt64();
                else if (idProp.ValueKind == JsonValueKind.String && long.TryParse(idProp.GetString(), out var idParsed)) id = idParsed;
            }

            if (!root.TryGetProperty("method", out var methodProp))
            {
                return;
            }

            var method = methodProp.GetString() ?? "";

            if (id.HasValue)
            {
                await HandleSseRequestAsync(session, id.Value, method, root, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Mithril MCP SSE] Erro ao analisar ou processar JSON-RPC: {ex.Message}");
        }
    }

    private async Task HandleSseRequestAsync(SseSession session, long id, string method, JsonElement root, CancellationToken cancellationToken)
    {
        if (method == "initialize")
        {
            await SendSseResponseAsync(session, id, new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                serverInfo = new { name = "Mithril-Vault-MCP-SSE", version = "1.0.0" }
            });
            return;
        }

        if (method == "tools/list")
        {
            await SendSseResponseAsync(session, id, new
            {
                tools = new object[]
                {
                    new
                    {
                        name = "get_api_token",
                        description = "Solicita a geração de um token de acesso JWT para uma API cadastrada no cofre (ex: reciprocidade). Exige consentimento explícito do usuário na interface gráfica. As credenciais (client_id e client_secret) nunca são expostas ao agente de IA.",
                        inputSchema = new
                        {
                            type = "object",
                            properties = new
                            {
                                api_name = new
                                {
                                    type = "string",
                                    description = "Nome da API cadastrada no cofre para a qual o token JWT é solicitado (ex: reciprocidade)."
                                }
                            },
                            required = new[] { "api_name" }
                        }
                    }
                }
            });
            return;
        }

        if (method == "tools/call")
        {
            if (!root.TryGetProperty("params", out var paramsEl))
            {
                await SendSseErrorAsync(session, id, -32602, "Parâmetros ausentes.");
                return;
            }

            var toolName = paramsEl.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            if (toolName == "get_api_token")
            {
                if (!paramsEl.TryGetProperty("arguments", out var argsEl) ||
                    !argsEl.TryGetProperty("api_name", out var apiNameProp) ||
                    apiNameProp.ValueKind != JsonValueKind.String)
                {
                    await SendSseErrorAsync(session, id, -32602, "Argumento 'api_name' inválido ou ausente.");
                    return;
                }

                var apiName = apiNameProp.GetString() ?? "";

                try
                {
                    Console.Error.WriteLine($"[Mithril MCP SSE] Aguardando consentimento para gerar JWT da API: {apiName}");
                    
                    ConsentResponse consent = await _consentService.RequestConsentAsync("Agente de IA (SSE)", apiName);

                    if (consent.Approved)
                    {
                        Console.Error.WriteLine($"[Mithril MCP SSE] Acesso aprovado pelo usuário. Iniciando troca de token para API: {apiName}");
                        
                        if (string.IsNullOrEmpty(consent.TokenUrl) || string.IsNullOrEmpty(consent.Username) || string.IsNullOrEmpty(consent.Password))
                        {
                            await SendSseResponseAsync(session, id, new
                            {
                                isError = true,
                                content = new object[]
                                {
                                    new { type = "text", text = "Erro: Configurações de API incompletas ou ausentes no cofre." }
                                }
                            });
                            return;
                        }

                        string jwtToken = await _tokenExchangeService.GetAccessTokenAsync(consent.TokenUrl, consent.Username, consent.Password);

                        Console.Error.WriteLine($"[Mithril MCP SSE] Token JWT obtido com sucesso para a API: {apiName}");
                        await SendSseResponseAsync(session, id, new
                        {
                            content = new object[]
                            {
                                new
                                {
                                    type = "text",
                                    text = jwtToken
                                }
                            }
                        });
                    }
                    else
                    {
                        Console.Error.WriteLine($"[Mithril MCP SSE] Geração de token rejeitada pelo usuário para a API: {apiName}");
                        await SendSseResponseAsync(session, id, new
                        {
                            isError = true,
                            content = new object[]
                            {
                                new
                                {
                                    type = "text",
                                    text = "Acesso negado pelo usuário. Não foi possível gerar o token JWT."
                                }
                            }
                        });
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Mithril MCP SSE] Erro no fluxo de geração de token: {ex.Message}");
                    await SendSseErrorAsync(session, id, -32001, $"Erro ao obter token JWT: {ex.Message}");
                }
            }
            else
            {
                await SendSseErrorAsync(session, id, -32601, $"Ferramenta não encontrada: {toolName}");
            }
            return;
        }

        await SendSseErrorAsync(session, id, -32601, $"Método não encontrado: {method}");
    }

    private async Task SendSseResponseAsync(SseSession session, long id, object result)
    {
        var response = new
        {
            jsonrpc = "2.0",
            id = id,
            result = result
        };
        string json = JsonSerializer.Serialize(response);
        await SendSseEventAsync(session, "message", json);
    }

    private async Task SendSseErrorAsync(SseSession session, long id, int errorCode, string errorMessage)
    {
        var response = new
        {
            jsonrpc = "2.0",
            id = id,
            error = new { code = errorCode, message = errorMessage }
        };
        string json = JsonSerializer.Serialize(response);
        await SendSseEventAsync(session, "message", json);
    }

    private async Task SendSseEventAsync(SseSession session, string eventName, string data)
    {
        await session.WriteSemaphore.WaitAsync();
        try
        {
            string payload = $"event: {eventName}\ndata: {data}\n\n";
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            await session.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            await session.Response.OutputStream.FlushAsync();
        }
        finally
        {
            session.WriteSemaphore.Release();
        }
    }

    private async Task SendSsePingAsync(SseSession session)
    {
        await session.WriteSemaphore.WaitAsync();
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(":\n\n");
            await session.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            await session.Response.OutputStream.FlushAsync();
        }
        finally
        {
            session.WriteSemaphore.Release();
        }
    }
}
