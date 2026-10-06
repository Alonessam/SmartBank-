using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartBank.Core.DTOs;
using SmartBank.Core.Interfaces;

namespace SmartBank.Infrastructure.Services
{
    public class GeminiService : IAIChatbotService
    {
        public const string DefaultModel = "gemini-2.5-flash";
        public const string ApiKeyHeader = "x-goog-api-key";

        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly string _model;
        private readonly ILogger<GeminiService> _logger;

        public GeminiService(HttpClient httpClient, IConfiguration configuration, ILogger<GeminiService>? logger = null)
        {
            _httpClient = httpClient;
            _logger = logger ?? NullLogger<GeminiService>.Instance;
            _apiKey = configuration["GeminiSettings:ApiKey"] ?? string.Empty;
            _model = string.IsNullOrWhiteSpace(configuration["GeminiSettings:Model"]) ? DefaultModel : configuration["GeminiSettings:Model"]!;

            _httpClient.BaseAddress ??= new Uri("https://generativelanguage.googleapis.com/");
            _httpClient.Timeout = TimeSpan.FromSeconds(10);
        }

        public async Task<string> GetResponseAsync(List<ChatMessageDto> history, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                throw new InvalidOperationException("Gemini API key is not configured.");
            }

            var (systemPrompt, turns) = AiConversation.Prepare(history);

            var payload = new GeminiRequest
            {
                SystemInstruction = new GeminiSystemInstruction { Parts = new List<GeminiPart> { new() { Text = systemPrompt } } },
                Contents = turns.Select(t => new GeminiContent
                {
                    Role = t.FromUser ? "user" : "model",
                    Parts = new List<GeminiPart> { new() { Text = t.Text } }
                }).ToList()
            };

            // The key travels in a header, not in the URL: URLs end up in logs, traces and exception messages.
            using var request = new HttpRequestMessage(HttpMethod.Post, $"v1beta/models/{_model}:generateContent")
            {
                Content = JsonContent.Create(payload)
            };
            request.Headers.Add(ApiKeyHeader, _apiKey);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // The status says enough. The body is not part of the exception: it can quote the request.
                _logger.LogWarning("The Gemini API answered HTTP {Status}.", (int)response.StatusCode);
                throw new HttpRequestException($"Gemini API returned status code {(int)response.StatusCode}.");
            }

            var result = await response.Content.ReadFromJsonAsync<GeminiResponse>(cancellationToken);
            var text = result?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;

            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException("Gemini API returned an empty response.");
            }

            return text;
        }

        private class GeminiRequest
        {
            [JsonPropertyName("contents")]
            public List<GeminiContent> Contents { get; set; } = new();

            [JsonPropertyName("systemInstruction")]
            public GeminiSystemInstruction? SystemInstruction { get; set; }
        }

        private class GeminiSystemInstruction
        {
            [JsonPropertyName("parts")]
            public List<GeminiPart> Parts { get; set; } = new();
        }

        private class GeminiContent
        {
            [JsonPropertyName("role")]
            public string Role { get; set; } = string.Empty;

            [JsonPropertyName("parts")]
            public List<GeminiPart> Parts { get; set; } = new();
        }

        private class GeminiPart
        {
            [JsonPropertyName("text")]
            public string Text { get; set; } = string.Empty;
        }

        private class GeminiResponse
        {
            [JsonPropertyName("candidates")]
            public List<GeminiCandidate>? Candidates { get; set; }
        }

        private class GeminiCandidate
        {
            [JsonPropertyName("content")]
            public GeminiContent? Content { get; set; }
        }
    }
}
