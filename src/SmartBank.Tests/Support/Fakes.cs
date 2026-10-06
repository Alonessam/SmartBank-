using SmartBank.Core.DTOs;
using SmartBank.Core.Interfaces;

namespace SmartBank.Tests.Support
{
    /// <summary>The AI chatbot for tests: never touches the network or a local Ollama, answers with whatever the test says.</summary>
    public sealed class FakeAiChatbot : IAIChatbotService
    {
        private readonly object _gate = new();

        /// <summary>What the "model" answers. Gets the conversation it was sent.</summary>
        public Func<List<ChatMessageDto>, string> Reply { get; set; } = _ => "Fake AI reply.";

        public List<List<ChatMessageDto>> Calls { get; } = new();

        public Task<string> GetResponseAsync(List<ChatMessageDto> history, CancellationToken cancellationToken = default)
        {
            lock (_gate) Calls.Add(history.Select(m => new ChatMessageDto { Id = m.Id, Sender = m.Sender, Content = m.Content, CreatedAt = m.CreatedAt }).ToList());
            return Task.FromResult(Reply(history));
        }

        public int CallCount { get { lock (_gate) return Calls.Count; } }
    }

    public sealed class FakeRagService : IRAGService
    {
        public string? Answer { get; set; }

        public Task<string?> SearchFAQAsync(string query) => Task.FromResult(Answer);
    }

    /// <summary>Market rates for tests: fixed live prices unless a test makes them stand-ins.</summary>
    public sealed class FakeMarketRates : IMarketRateService
    {
        public bool Fallback { get; set; }

        public bool Empty { get; set; }

        public IReadOnlyList<MarketRateDto> Rates => Empty
            ? new List<MarketRateDto>()
            : new List<MarketRateDto>
            {
                new() { Code = "USD", Name = "Dollar", NameEn = "US Dollar", Buy = 30.00m, Sell = 31.00m, IsFallback = Fallback },
                new() { Code = "EUR", Name = "Euro", NameEn = "Euro", Buy = 34.00m, Sell = 35.00m, IsFallback = Fallback },
                new() { Code = "XAU", Name = "Altin", NameEn = "Gold", Buy = 3000.00m, Sell = 3100.00m, IsFallback = Fallback },
                new() { Code = "XAG", Name = "Gumus", NameEn = "Silver", Buy = 30.00m, Sell = 32.00m, IsFallback = Fallback }
            };

        public Task<IReadOnlyList<MarketRateDto>> GetRatesAsync() => Task.FromResult(Rates);

        public Task<MarketRateDto?> GetRateByCodeAsync(string code) =>
            Task.FromResult(Rates.FirstOrDefault(r => r.Code.Equals(code, StringComparison.OrdinalIgnoreCase)));
    }
}
