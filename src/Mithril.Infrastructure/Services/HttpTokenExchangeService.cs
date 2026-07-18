using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Mithril.Domain.Exceptions;
using Mithril.Domain.Interfaces;

namespace Mithril.Infrastructure.Services;

public class HttpTokenExchangeService : ITokenExchangeService
{
    private readonly HttpClient _client;
    private static readonly HttpClient _defaultClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15) // Prática recomendada de ciclo de vida de sockets
    });

    public HttpTokenExchangeService(HttpClient? httpClient = null)
    {
        _client = httpClient ?? _defaultClient;
    }

    public async Task<string> GetAccessTokenAsync(string tokenUrl, string clientId, string clientSecret)
    {
        if (string.IsNullOrEmpty(tokenUrl))
            throw new ArgumentException("A URL de Token da API não pode ser vazia.", nameof(tokenUrl));

        try
        {
            // Monta os parâmetros OAuth2 Client Credentials
            var postData = new Dictionary<string, string>
            {
                { "grant_type", "client_credentials" },
                { "client_id", clientId },
                { "client_secret", clientSecret }
            };

            var content = new FormUrlEncodedContent(postData);

            // Faz a requisição POST (simulando Postman / Curl)
            var response = await _client.PostAsync(tokenUrl, content);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new SecurityException($"A API respondeu com código de erro {response.StatusCode}. Detalhes: {responseBody}");
            }

            // Parseia a resposta JSON para obter o JWT
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            // Tenta obter propriedades padrão: access_token, token ou jwt
            if (root.TryGetProperty("access_token", out var tokenProp) || 
                root.TryGetProperty("token", out tokenProp) ||
                root.TryGetProperty("jwt", out tokenProp))
            {
                var token = tokenProp.GetString();
                if (!string.IsNullOrEmpty(token))
                {
                    return token;
                }
            }

            throw new SecurityException("A resposta da API foi bem-sucedida, mas o campo 'access_token' ou 'token' não foi encontrado no JSON.");
        }
        catch (HttpRequestException ex)
        {
            throw new SecurityException($"Falha física de rede ao conectar com a API: {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            throw new SecurityException($"A resposta da API não está em um formato JSON válido: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is not SecurityException)
        {
            throw new SecurityException($"Erro inesperado na troca de token: {ex.Message}", ex);
        }
    }
}
