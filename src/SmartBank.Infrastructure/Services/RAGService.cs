using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using SmartBank.Core.Interfaces;

namespace SmartBank.Infrastructure.Services
{
    public class RAGService : IRAGService
    {
        private readonly HttpClient _httpClient;
        private readonly string _model;
        private readonly List<FAQDocument> _documents;

        public RAGService(HttpClient httpClient, IConfiguration configuration)
        {
            _httpClient = httpClient;
            
            // Ollama configuration
            var baseUrl = configuration["OllamaSettings:BaseUrl"] ?? "http://localhost:11434";
            _httpClient.BaseAddress = new Uri(baseUrl);
            _httpClient.Timeout = TimeSpan.FromSeconds(2); // Short timeout for embeddings ping
            
            _model = configuration["OllamaSettings:Model"] ?? "llama3";
            _documents = LoadFAQDocuments();
        }

        public async Task<string?> SearchFAQAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query) || _documents.Count == 0)
                return null;

            try
            {
                // Attempt 1: Semantic Embedding search if Ollama is online
                var queryEmbedding = await GetEmbeddingAsync(query);
                if (queryEmbedding != null)
                {
                    var bestMatch = FindBestSemanticMatch(queryEmbedding);
                    if (bestMatch.doc != null && bestMatch.score > 0.70f)
                    {
                        Console.WriteLine($"[RAG Service] Found semantic match: '{bestMatch.doc.Question}' (Score: {bestMatch.score:F2})");
                        return bestMatch.doc.Answer;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RAG Service] Ollama embedding failed, falling back to keyword search: {ex.Message}");
            }

            // Attempt 2: Fallback Keyword/Jaccard search
            var bestKeywordMatch = FindBestKeywordMatch(query);
            if (bestKeywordMatch.doc != null && bestKeywordMatch.score > 0.25f)
            {
                Console.WriteLine($"[RAG Service] Found keyword match: '{bestKeywordMatch.doc.Question}' (Score: {bestKeywordMatch.score:F2})");
                return bestKeywordMatch.doc.Answer;
            }

            return null;
        }

        private List<FAQDocument> LoadFAQDocuments()
        {
            try
            {
                var basePath = AppContext.BaseDirectory;
                var path = Path.Combine(basePath, "Data", "faq_documents.json");
                
                if (!File.Exists(path))
                {
                    path = Path.Combine(Directory.GetCurrentDirectory(), "Data", "faq_documents.json");
                }
                if (!File.Exists(path))
                {
                    path = Path.Combine(Directory.GetCurrentDirectory(), "src", "SmartBank.API", "Data", "faq_documents.json");
                }

                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var docs = JsonSerializer.Deserialize<List<FAQDocument>>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    }) ?? new List<FAQDocument>();

                    // Pre-embed documents synchronously in background task to avoid blocking constructor
                    _ = Task.Run(async () =>
                    {
                        foreach (var doc in docs)
                        {
                            try
                            {
                                doc.Embedding = await GetEmbeddingAsync(doc.Question);
                            }
                            catch
                            {
                                // Fail silently, fallback is keyword search
                            }
                        }
                    });

                    return docs;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RAG Service] Failed to load SSS documents: {ex.Message}");
            }

            return new List<FAQDocument>();
        }

        private async Task<float[]?> GetEmbeddingAsync(string text)
        {
            var payload = new OllamaEmbeddingRequest
            {
                Model = _model,
                Prompt = text
            };

            var response = await _httpClient.PostAsJsonAsync("/api/embeddings", payload);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<OllamaEmbeddingResponse>();
                return result?.Embedding;
            }
            return null;
        }

        private (FAQDocument? doc, float score) FindBestSemanticMatch(float[] queryEmbedding)
        {
            FAQDocument? bestDoc = null;
            float maxSimilarity = -1.0f;

            foreach (var doc in _documents)
            {
                if (doc.Embedding == null) continue;
                
                float similarity = CosineSimilarity(queryEmbedding, doc.Embedding);
                if (similarity > maxSimilarity)
                {
                    maxSimilarity = similarity;
                    bestDoc = doc;
                }
            }

            return (bestDoc, maxSimilarity);
        }

        private (FAQDocument? doc, float score) FindBestKeywordMatch(string query)
        {
            var queryTokens = Tokenize(query);
            if (queryTokens.Count == 0) return (null, 0);

            FAQDocument? bestDoc = null;
            float maxScore = 0.0f;

            foreach (var doc in _documents)
            {
                // 1. Calculate Jaccard similarity with keywords (normalized and split by space/hyphen)
                var keywordTokens = doc.Keywords
                    .Select(k => NormalizeTurkish(k.ToLowerInvariant()))
                    .SelectMany(k => k.Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries))
                    .ToList();
                    
                var intersection = queryTokens.Intersect(keywordTokens).Count();
                var union = queryTokens.Union(keywordTokens).Count();
                float jaccard = union > 0 ? (float)intersection / union : 0f;

                // 2. Term overlap check inside question title (normalized)
                var questionTokens = Tokenize(doc.Question);
                var qIntersection = queryTokens.Intersect(questionTokens).Count();
                float questionOverlap = (float)qIntersection / queryTokens.Count;

                // Combined score
                float combinedScore = (jaccard * 0.6f) + (questionOverlap * 0.4f);

                if (combinedScore > maxScore)
                {
                    maxScore = combinedScore;
                    bestDoc = doc;
                }
            }

            return (bestDoc, maxScore);
        }

        private string NormalizeTurkish(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            
            var sb = new System.Text.StringBuilder(text);
            sb.Replace('ı', 'i')
              .Replace('ş', 's')
              .Replace('ğ', 'g')
              .Replace('ü', 'u')
              .Replace('ö', 'o')
              .Replace('ç', 'c')
              .Replace('İ', 'i')
              .Replace('Ş', 's')
              .Replace('Ğ', 'g')
              .Replace('Ü', 'u')
              .Replace('Ö', 'o')
              .Replace('Ç', 'c');
            return sb.ToString();
        }

        private List<string> Tokenize(string text)
        {
            text = text.ToLowerInvariant();
            text = NormalizeTurkish(text);
            // Remove non-alphabetic chars
            text = Regex.Replace(text, @"[^\w\s]", "");
            return text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                       .Where(w => w.Length > 2) // skip tiny words/stop words
                       .ToList();
        }

        private float CosineSimilarity(float[] vectorA, float[] vectorB)
        {
            if (vectorA.Length != vectorB.Length) return 0f;

            float dotProduct = 0f;
            float normA = 0f;
            float normB = 0f;

            for (int i = 0; i < vectorA.Length; i++)
            {
                dotProduct += vectorA[i] * vectorB[i];
                normA += vectorA[i] * vectorA[i];
                normB += vectorB[i] * vectorB[i];
            }

            if (normA == 0f || normB == 0f) return 0f;
            return dotProduct / ((float)Math.Sqrt(normA) * (float)Math.Sqrt(normB));
        }

        private class FAQDocument
        {
            public int Id { get; set; }
            public List<string> Keywords { get; set; } = new();
            public string Question { get; set; } = string.Empty;
            public string Answer { get; set; } = string.Empty;
            public float[]? Embedding { get; set; }
        }

        private class OllamaEmbeddingRequest
        {
            [JsonPropertyName("model")]
            public string Model { get; set; } = string.Empty;

            [JsonPropertyName("prompt")]
            public string Prompt { get; set; } = string.Empty;
        }

        private class OllamaEmbeddingResponse
        {
            [JsonPropertyName("embedding")]
            public float[]? Embedding { get; set; }
        }
    }
}
