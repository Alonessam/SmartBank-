using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using SmartBank.Core.DTOs;
using SmartBank.Core.Interfaces;

namespace SmartBank.Infrastructure.Services
{
    public class OllamaService : IAIChatbotService
    {
        private readonly HttpClient _httpClient;
        private readonly string _model;

        public OllamaService(HttpClient httpClient, IConfiguration configuration)
        {
            _httpClient = httpClient;
            
            // Read OllamaSettings from configuration
            var baseUrl = configuration["OllamaSettings:BaseUrl"];
            if (string.IsNullOrEmpty(baseUrl))
            {
                baseUrl = "http://localhost:11434";
            }
            _httpClient.BaseAddress = new Uri(baseUrl);

            _model = configuration["OllamaSettings:Model"] ?? "llama3";
        }

        public async Task<bool> IsAvailableAsync()
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(300));
                var response = await _httpClient.GetAsync("/", cts.Token);
                return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound || response.StatusCode == System.Net.HttpStatusCode.OK;
            }
            catch
            {
                return false;
            }
        }

        public async Task<string> GetResponseAsync(List<ChatMessageDto> history)
        {
            var systemPrompt = "You are SmartBank AI, a helpful, polite, and professional customer support assistant for SmartBank Fintech. " +
                               "Answer general banking questions (such as credit card limits, interest rates, account types) and keep answers concise and safe. " +
                               "Analyze the language of the user's message. If they write in Turkish, reply in Turkish. If they write in English, reply in English. " +
                               "If the user asks for a human representative, or if you cannot answer a complex transaction/security issue, tell the user politely in their language that you will notify and connect them to a human customer representative. " +
                               "\n\nCRITICAL ACTION CAPABILITIES:\n" +
                               "1. If the user wants to check their balance, see their accounts, or asks 'bakiyem', 'hesaplarım', you MUST respond with EXACTLY: [ACTION:GET_BALANCES]\n" +
                               "2. If the user wants to transfer money (para transferi, para gönder vb.), you must ask them for: (a) Source Account, (b) Destination Account, and (c) Amount. Once you have collected these 3 details from the conversation, you MUST respond with EXACTLY: [ACTION:TRANSFER, source:SOURCE_ACCOUNT, destination:DESTINATION_ACCOUNT, amount:AMOUNT, description:DESCRIPTION]\n" +
                               "Do NOT output any other text when returning these actions.";

            var systemPromptBuilder = new System.Text.StringBuilder(systemPrompt);
            var chatMessages = new List<OllamaMessage>();

            // Process history: split System context from user/assistant turns
            foreach (var msg in history)
            {
                if (msg.Sender.Equals("System", StringComparison.OrdinalIgnoreCase))
                {
                    systemPromptBuilder.Append("\n\nAdditional System Context:\n").Append(msg.Content);
                }
                else
                {
                    var role = msg.Sender.Equals("User", StringComparison.OrdinalIgnoreCase) ? "user" : "assistant";
                    chatMessages.Add(new OllamaMessage { Role = role, Content = msg.Content });
                }
            }

            var requestPayload = new OllamaChatRequest
            {
                Model = _model,
                Stream = false,
                Messages = new List<OllamaMessage>
                {
                    new OllamaMessage { Role = "system", Content = systemPromptBuilder.ToString() }
                }
            };
            requestPayload.Messages.AddRange(chatMessages);

            // Send request
            var response = await _httpClient.PostAsJsonAsync("/api/chat", requestPayload);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OllamaChatResponse>();
            
            if (result?.Message?.Content == null)
            {
                throw new Exception("Ollama returned an empty response.");
            }

            return result.Message.Content;
        }

        private string GetFallbackMessage(List<ChatMessageDto> history)
        {
            // Detect language from history to show a localized fallback message
            var lastUserMessage = history.LastOrDefault(m => m.Sender.Equals("User", StringComparison.OrdinalIgnoreCase))?.Content ?? "";
            
            var text = lastUserMessage.ToLowerInvariant();
            
            bool isTurkish = text.Contains("merhaba") || 
                             text.Contains("selam") || 
                             text.Contains("nasıl") || 
                             text.Contains("nasil") || 
                             text.Contains("yardım") || 
                             text.Contains("yardim") || 
                             text.Contains("kredi") || 
                             text.Contains("hesap") || 
                             text.Contains("hesab") || 
                             text.Contains("para") || 
                             text.Contains("cek") || 
                             text.Contains("çek") || 
                             text.Contains("kart") || 
                             text.Contains("bakiye") || 
                             text.Contains("gönder") || 
                             text.Contains("gonder") || 
                             text.Contains("işlem") || 
                             text.Contains("islem") || 
                             text.Contains("destek") || 
                             text.Contains("bağla") || 
                             text.Contains("bagla") || 
                             text.Contains("istiyorum");

            return isTurkish 
                ? "Şu anda yapay zeka servisimiz çevrimdışı. Sizi en kısa sürede canlı destek temsilcimize bağlayacağız."
                : "Our AI support is currently offline. We will connect you to a live support representative shortly.";
        }

        // Helper classes for Ollama API serialization
        private class OllamaChatRequest
        {
            [JsonPropertyName("model")]
            public string Model { get; set; } = string.Empty;

            [JsonPropertyName("messages")]
            public List<OllamaMessage> Messages { get; set; } = new();

            [JsonPropertyName("stream")]
            public bool Stream { get; set; } = false;
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
