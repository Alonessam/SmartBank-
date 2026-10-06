using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartBank.Core.Common;
using SmartBank.Core.DTOs;
using SmartBank.Core.Interfaces;

namespace SmartBank.Infrastructure.Services
{
    /// <summary>
    /// The chatbot the application uses: retrieves FAQ text for the last customer message, then asks the local Ollama
    /// server and, when that is not there or fails, the Gemini API. If both fail, a canned answer in the customer's language
    /// says that a human will take over.
    ///  - At most <see cref="MaxConcurrentGenerations"/> answers are generated at the same time in the whole application
    ///    (the external quota and the server are shared by every customer).
    ///  - Every call has its own deadline of <see cref="GenerationTimeout"/>; when it passes, the HTTP call is cancelled
    ///    (not just abandoned) and the next model is tried.
    ///  - The model sees only the newest messages (see <see cref="AiConversation.MaxMessages"/>).
    /// </summary>
    public sealed class FailoverChatbotService : IAIChatbotService
    {
        public const int MaxConcurrentGenerations = 4;
        public static readonly TimeSpan GenerationTimeout = TimeSpan.FromSeconds(20);
        public static readonly TimeSpan QueueTimeout = TimeSpan.FromSeconds(10);

        public const string OfflineEnglish = "Our AI support is currently offline. We will connect you to a live support representative shortly.";
        public const string OfflineTurkish = "Şu anda yapay zeka servisimiz çevrimdışı. Sizi en kısa sürede canlı destek temsilcimize bağlayacağız.";

        // Shared by every instance (this class is created per request, the limit is for the whole process).
        private static readonly SemaphoreSlim Generations = new(MaxConcurrentGenerations, MaxConcurrentGenerations);

        private readonly OllamaService _ollama;
        private readonly IAIChatbotService _gemini;
        private readonly IRAGService _rag;
        private readonly ILogger<FailoverChatbotService> _logger;
        private readonly TimeSpan _generationTimeout;

        public FailoverChatbotService(OllamaService ollama, GeminiService gemini, IRAGService rag, ILogger<FailoverChatbotService>? logger = null)
            : this(ollama, (IAIChatbotService)gemini, rag, logger)
        {
        }

        internal FailoverChatbotService(OllamaService ollama, IAIChatbotService gemini, IRAGService rag, ILogger<FailoverChatbotService>? logger = null, TimeSpan? generationTimeout = null)
        {
            _generationTimeout = generationTimeout ?? GenerationTimeout;
            _ollama = ollama;
            _gemini = gemini;
            _rag = rag;
            _logger = logger ?? NullLogger<FailoverChatbotService>.Instance;
        }

        public async Task<string> GetResponseAsync(List<ChatMessageDto> history, CancellationToken cancellationToken = default)
        {
            var messages = new List<ChatMessageDto>(history);
            var lastUserMessage = messages.LastOrDefault(m => m.Sender.Equals(ChatSenders.User, StringComparison.OrdinalIgnoreCase))?.Content;

            // FAQ retrieval: a failure here only means the model gets no extra context.
            if (!string.IsNullOrWhiteSpace(lastUserMessage))
            {
                try
                {
                    var faqAnswer = await _rag.SearchFAQAsync(lastUserMessage);
                    if (!string.IsNullOrWhiteSpace(faqAnswer))
                    {
                        messages.Add(new ChatMessageDto { Sender = ChatSenders.Context, Content = $"Relevant FAQ Context:\n{faqAnswer}", CreatedAt = DateTime.UtcNow });
                    }
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "FAQ retrieval failed; the model answers without it.");
                }
            }

            if (!await Generations.WaitAsync(QueueTimeout, cancellationToken))
            {
                _logger.LogWarning("All {Count} AI generation slots stayed busy for {Seconds} s; the canned answer is used.", MaxConcurrentGenerations, QueueTimeout.TotalSeconds);
                return Offline(lastUserMessage);
            }

            try
            {
                if (await _ollama.IsAvailableAsync(cancellationToken))
                {
                    var local = await TryAsync(_ollama, messages, "Ollama", cancellationToken);
                    if (local != null) return local;
                }
                else
                {
                    _logger.LogInformation("Ollama is offline; Gemini is used.");
                }

                var remote = await TryAsync(_gemini, messages, "Gemini", cancellationToken);
                return remote ?? Offline(lastUserMessage);
            }
            finally
            {
                Generations.Release();
            }
        }

        /// <summary>One model, one deadline. Null when it failed or was too slow; a cancelled caller still cancels everything.</summary>
        private async Task<string?> TryAsync(IAIChatbotService model, List<ChatMessageDto> messages, string name, CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_generationTimeout);

            try
            {
                return await model.GetResponseAsync(messages, deadline.Token);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("{Model} could not answer ({Error}).", name, ex is OperationCanceledException ? "timed out" : ex.Message);
                return null;
            }
        }

        private static string Offline(string? lastUserMessage) => TurkishText.LooksTurkish(lastUserMessage) ? OfflineTurkish : OfflineEnglish;
    }
}
