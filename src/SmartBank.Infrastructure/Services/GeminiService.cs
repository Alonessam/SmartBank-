using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using SmartBank.Core.DTOs;
using SmartBank.Core.Interfaces;

namespace SmartBank.Infrastructure.Services
{
    public class GeminiService : IAIChatbotService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly string _model;

        public GeminiService(HttpClient httpClient, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _apiKey = configuration["GeminiSettings:ApiKey"] ?? string.Empty;
            _model = configuration["GeminiSettings:Model"] ?? "gemini-1.5-flash";
            
            // Set base address for Gemini API
            _httpClient.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
            _httpClient.Timeout = TimeSpan.FromSeconds(10);
        }

        public async Task<string> GetResponseAsync(List<ChatMessageDto> history)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                throw new InvalidOperationException("Gemini API key is not configured.");
            }

            try
            {
                var baseSystemPrompt = "You are SmartBank AI, a helpful, polite, and professional customer support assistant for SmartBank Fintech. " +
                                       "Answer general banking questions (such as credit card limits, interest rates, account types) and keep answers concise and safe. " +
                                       "Analyze the language of the user's message. If they write in Turkish, reply in Turkish. If they write in English, reply in English. " +
                                       "If the user asks for a human representative, or if you cannot answer a complex transaction/security issue, tell the user politely in their language that you will notify and connect them to a human customer representative. " +
                                       "\n\nCRITICAL ACTION CAPABILITIES:\n" +
                                       "1. If the user wants to check their balance, see their accounts, or asks 'bakiyem', 'hesaplarım', you MUST respond with EXACTLY: [ACTION:GET_BALANCES]\n" +
                                       "2. If the user wants to transfer money (para transferi, para gönder vb.), you must ask them for: (a) Source Account, (b) Destination Account, and (c) Amount. Once you have collected these 3 details from the conversation, you MUST respond with EXACTLY: [ACTION:TRANSFER, source:SOURCE_ACCOUNT, destination:DESTINATION_ACCOUNT, amount:AMOUNT, description:DESCRIPTION]\n" +
                                       "Do NOT output any other text when returning these actions.";

                var systemPromptBuilder = new StringBuilder(baseSystemPrompt);

                var contents = new List<GeminiContent>();
                foreach (var msg in history)
                {
                    if (msg.Sender.Equals("System", StringComparison.OrdinalIgnoreCase))
                    {
                        systemPromptBuilder.Append("\n\nAdditional System Context:\n").Append(msg.Content);
                    }
                    else
                    {
                        var role = msg.Sender.Equals("User", StringComparison.OrdinalIgnoreCase) ? "user" : "model";
                        
                        // To avoid empty content or invalid structures
                        var textContent = string.IsNullOrWhiteSpace(msg.Content) ? "..." : msg.Content;
                        
                        contents.Add(new GeminiContent
                        {
                            Role = role,
                            Parts = new List<GeminiPart> { new GeminiPart { Text = textContent } }
                        });
                    }
                }

                var requestPayload = new GeminiRequest
                {
                    SystemInstruction = new GeminiSystemInstruction
                    {
                        Parts = new List<GeminiPart> { new GeminiPart { Text = systemPromptBuilder.ToString() } }
                    },
                    Contents = contents
                };

                var url = $"v1beta/models/{_model}:generateContent?key={_apiKey}";
                var response = await _httpClient.PostAsJsonAsync(url, requestPayload);
                
                if (!response.IsSuccessStatusCode)
                {
                    var errorDetails = await response.Content.ReadAsStringAsync();
                    throw new HttpRequestException($"Gemini API returned status code {response.StatusCode}. Details: {errorDetails}");
                }

                var result = await response.Content.ReadFromJsonAsync<GeminiResponse>();
                var responseText = result?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;

                if (string.IsNullOrWhiteSpace(responseText))
                {
                    throw new Exception("Gemini API returned an empty response.");
                }

                return responseText;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GeminiService Error] {ex.Message}");
                throw;
            }
        }

        // Gemini Request Models
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

        // Gemini Response Models
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
