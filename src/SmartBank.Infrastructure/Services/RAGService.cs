using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartBank.Core.Interfaces;

namespace SmartBank.Infrastructure.Services
{
    /// <summary>
    /// Finds the FAQ entry that fits a question. This is a SINGLETON: the FAQ file is read once, and the questions are
    /// embedded (one request to Ollama each) once, lazily and in the background, the first time somebody searches. Until
    /// that has worked, and whenever Ollama is not there, the keyword search answers. (It used to be created for every
    /// message and re-embedded every question each time, so semantic search never had its vectors ready.)
    /// </summary>
    public class RAGService : IRAGService
    {
        // After a failed attempt (Ollama offline) the embeddings are tried again no sooner than this.
        private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(10);

        private readonly HttpClient _httpClient;
        private readonly string _model;
        private readonly ILogger<RAGService> _logger;
        private readonly TimeProvider _time;
        private readonly List<FaqDocument> _documents;

        private readonly object _embeddingGate = new();
        private Task? _embeddingTask;
        private DateTime _nextEmbeddingAttemptUtc = DateTime.MinValue;
        private volatile bool _embeddingsReady;

        public RAGService(HttpClient httpClient, IConfiguration configuration, ILogger<RAGService>? logger = null, TimeProvider? timeProvider = null)
            : this(httpClient, configuration, null, logger, timeProvider)
        {
        }

        /// <param name="documents">The FAQ entries; null reads faq_documents.json.</param>
        internal RAGService(HttpClient httpClient, IConfiguration configuration, IEnumerable<FaqDocument>? documents, ILogger<RAGService>? logger = null, TimeProvider? timeProvider = null)
        {
            _httpClient = httpClient;
            _logger = logger ?? NullLogger<RAGService>.Instance;
            _time = timeProvider ?? TimeProvider.System;

            _httpClient.BaseAddress ??= new Uri(configuration["OllamaSettings:BaseUrl"] ?? "http://localhost:11434");
            _httpClient.Timeout = TimeSpan.FromSeconds(2); // a short timeout: this is only the embeddings endpoint

            _model = configuration["OllamaSettings:Model"] ?? "llama3";
            _documents = documents?.ToList() ?? LoadFaqDocuments();
        }

        internal bool EmbeddingsReady => _embeddingsReady;

        public async Task<string?> SearchFAQAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query) || _documents.Count == 0)
                return null;

            StartEmbeddingIfNeeded();

            // Semantic search, only once every question has its vector.
            if (_embeddingsReady)
            {
                try
                {
                    var queryEmbedding = await GetEmbeddingAsync(query);
                    if (queryEmbedding != null)
                    {
                        var best = FindBestSemanticMatch(queryEmbedding);
                        if (best.Doc != null && best.Score > 0.70f)
                        {
                            _logger.LogDebug("FAQ semantic match: '{Question}' (score {Score:F2}).", best.Doc.Question, best.Score);
                            return best.Doc.Answer;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogInformation(ex, "The embedding request failed; the keyword search is used instead.");
                }
            }

            var keyword = FindBestKeywordMatch(query);
            if (keyword.Doc != null && keyword.Score > 0.25f)
            {
                _logger.LogDebug("FAQ keyword match: '{Question}' (score {Score:F2}).", keyword.Doc.Question, keyword.Score);
                return keyword.Doc.Answer;
            }

            return null;
        }

        private void StartEmbeddingIfNeeded()
        {
            if (_embeddingsReady) return;

            lock (_embeddingGate)
            {
                if (_embeddingTask is { IsCompleted: false }) return;
                if (_time.GetUtcNow().UtcDateTime < _nextEmbeddingAttemptUtc) return;

                _nextEmbeddingAttemptUtc = _time.GetUtcNow().UtcDateTime + RetryAfterFailure;
                _embeddingTask = Task.Run(EmbedAllAsync);
            }
        }

        /// <summary>Embeds every question. All or nothing: a half-embedded FAQ would answer from a random subset.</summary>
        internal async Task EmbedAllAsync()
        {
            try
            {
                var vectors = new List<float[]>();
                foreach (var doc in _documents)
                {
                    var vector = await GetEmbeddingAsync(doc.Question);
                    if (vector == null)
                    {
                        _logger.LogInformation("Ollama gave no embeddings; the FAQ keeps using the keyword search.");
                        return;
                    }

                    vectors.Add(vector);
                }

                for (var i = 0; i < _documents.Count; i++) _documents[i].Embedding = vectors[i];
                _embeddingsReady = true;
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "Ollama is not available for embeddings; the FAQ keeps using the keyword search.");
            }
        }

        private List<FaqDocument> LoadFaqDocuments()
        {
            try
            {
                var candidates = new[]
                {
                    Path.Combine(AppContext.BaseDirectory, "Data", "faq_documents.json"),
                    Path.Combine(Directory.GetCurrentDirectory(), "Data", "faq_documents.json"),
                    Path.Combine(Directory.GetCurrentDirectory(), "src", "SmartBank.API", "Data", "faq_documents.json")
                };

                var path = candidates.FirstOrDefault(File.Exists);
                if (path != null)
                {
                    var json = File.ReadAllText(path);
                    return JsonSerializer.Deserialize<List<FaqDocument>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                           ?? new List<FaqDocument>();
                }

                _logger.LogWarning("faq_documents.json was not found; the FAQ search is off.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The FAQ documents could not be loaded.");
            }

            return new List<FaqDocument>();
        }

        private async Task<float[]?> GetEmbeddingAsync(string text)
        {
            var payload = new OllamaEmbeddingRequest { Model = _model, Prompt = text };

            using var response = await _httpClient.PostAsJsonAsync("/api/embeddings", payload);
            if (!response.IsSuccessStatusCode) return null;

            var result = await response.Content.ReadFromJsonAsync<OllamaEmbeddingResponse>();
            return result?.Embedding;
        }

        private (FaqDocument? Doc, float Score) FindBestSemanticMatch(float[] queryEmbedding)
        {
            FaqDocument? bestDoc = null;
            var maxSimilarity = -1.0f;

            foreach (var doc in _documents)
            {
                if (doc.Embedding == null) continue;

                var similarity = CosineSimilarity(queryEmbedding, doc.Embedding);
                if (similarity > maxSimilarity)
                {
                    maxSimilarity = similarity;
                    bestDoc = doc;
                }
            }

            return (bestDoc, maxSimilarity);
        }

        private (FaqDocument? Doc, float Score) FindBestKeywordMatch(string query)
        {
            var queryTokens = Tokenize(query);
            if (queryTokens.Count == 0) return (null, 0);

            FaqDocument? bestDoc = null;
            var maxScore = 0.0f;

            foreach (var doc in _documents)
            {
                // 1. Jaccard similarity with the keywords (normalised, split by space/hyphen)
                var keywordTokens = doc.Keywords
                    .Select(k => NormalizeTurkish(k.ToLowerInvariant()))
                    .SelectMany(k => k.Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries))
                    .ToList();

                var intersection = queryTokens.Intersect(keywordTokens).Count();
                var union = queryTokens.Union(keywordTokens).Count();
                var jaccard = union > 0 ? (float)intersection / union : 0f;

                // 2. Term overlap with the question title (normalised)
                var questionTokens = Tokenize(doc.Question);
                var questionOverlap = (float)queryTokens.Intersect(questionTokens).Count() / queryTokens.Count;

                var combinedScore = (jaccard * 0.6f) + (questionOverlap * 0.4f);
                if (combinedScore > maxScore)
                {
                    maxScore = combinedScore;
                    bestDoc = doc;
                }
            }

            return (bestDoc, maxScore);
        }

        private static string NormalizeTurkish(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            return text
                .Replace('ı', 'i').Replace('ş', 's').Replace('ğ', 'g').Replace('ü', 'u').Replace('ö', 'o').Replace('ç', 'c')
                .Replace('İ', 'i').Replace('Ş', 's').Replace('Ğ', 'g').Replace('Ü', 'u').Replace('Ö', 'o').Replace('Ç', 'c');
        }

        private static List<string> Tokenize(string text)
        {
            text = NormalizeTurkish(text.ToLowerInvariant());
            text = Regex.Replace(text, @"[^\w\s]", string.Empty);
            return text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                       .Where(w => w.Length > 2) // skip tiny words/stop words
                       .ToList();
        }

        private static float CosineSimilarity(float[] a, float[] b)
        {
            if (a.Length != b.Length) return 0f;

            float dot = 0f, normA = 0f, normB = 0f;
            for (var i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                normA += a[i] * a[i];
                normB += b[i] * b[i];
            }

            return normA == 0f || normB == 0f ? 0f : dot / ((float)Math.Sqrt(normA) * (float)Math.Sqrt(normB));
        }

        internal sealed class FaqDocument
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
