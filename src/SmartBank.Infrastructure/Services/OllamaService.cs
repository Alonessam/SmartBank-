using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using SmartBank.Core.DTOs;
using SmartBank.Core.Interfaces;

namespace SmartBank.Infrastructure.Services
{
    /// <summary>A local Ollama server. The caller decides how long a call may take (it passes a token that is cancelled on timeout).</summary>
    public class OllamaService : IAIChatbotService
    {
        private readonly HttpClient _httpClient;
        private readonly string _model;

        public OllamaService(HttpClient httpClient, IConfiguration configuration)
        {
            _httpClient = httpClient;

            var baseUrl = configuration["OllamaSettings:BaseUrl"];
            if (string.IsNullOrEmpty(baseUrl))
            {
                baseUrl = "http://localhost:11434";
            }

            _httpClient.BaseAddress = new Uri(baseUrl);
            _model = configuration["OllamaSettings:Model"] ?? "llama3";
        }

        /// <summary>Is the server up? A 300 ms probe: when it is not there, the next AI is tried at once.</summary>
        public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromMilliseconds(300));
                using var response = await _httpClient.GetAsync("/", cts.Token);
                return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }

        public async Task<string> GetResponseAsync(List<ChatMessageDto> history, CancellationToken cancellationToken = default)
        {
            var (systemPrompt, turns) = AiConversation.Prepare(history);

            var request = new OllamaChatRequest
            {
                Model = _model,
                Stream = false,
                Messages = new List<OllamaMessage> { new() { Role = "system", Content = systemPrompt } }
            };
            request.Messages.AddRange(turns.Select(t => new OllamaMessage { Role = t.FromUser ? "user" : "assistant", Content = t.Text }));

            using var response = await _httpClient.PostAsJsonAsync("/api/chat", request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(cancellationToken);
            if (string.IsNullOrWhiteSpace(result?.Message?.Content))
            {
                throw new InvalidOperationException("Ollama returned an empty response.");
            }

            return result.Message.Content;
        }

        private class OllamaChatRequest
        {
            [JsonPropertyName("model")]
            public string Model { get; set; } = string.Empty;

            [JsonPropertyName("messages")]
            public List<OllamaMessage> Messages { get; set; } = new();

            [JsonPropertyName("stream")]
            public bool Stream { get; set; }
        }

        private class OllamaMessage
        {
            [JsonPropertyName("role")]
            public string Role { get; set; } = string.Empty;

            [JsonPropertyName("content")]
            public string Content { get; set; } = string.Empty;
        }

        private class OllamaChatResponse
        {
            [JsonPropertyName("message")]
            public OllamaMessage? Message { get; set; }
        }
    }
}
