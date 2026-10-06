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

        public List<List<ChatMessageDto>> CallsSnapshot() { lock (_gate) return Calls.ToList(); }
    }

    public sealed class FakeRagService : IRAGService
    {
        public string? Answer { get; set; }

        public Task<string?> SearchFAQAsync(string query) => Task.FromResult(Answer);
    }

    /// <summary>Market rates for tests: fixed live prices unless a test makes them stand-ins or changes a price.</summary>
    public sealed class FakeMarketRates : IMarketRateService
    {
        private readonly Dictionary<string, (decimal Buy, decimal Sell)> _prices = new()
        {
            ["USD"] = (30.00m, 31.00m),
            ["EUR"] = (34.00m, 35.00m),
            ["XAU"] = (3000.00m, 3100.00m),
            ["XAG"] = (30.00m, 32.00m)
        };

        /// <summary>Marks every price as a stand-in (the live feed is down).</summary>
        public bool Fallback { get; set; }

        /// <summary>No prices at all.</summary>
        public bool Empty { get; set; }

        public int Calls { get; private set; }

        public FakeMarketRates Set(string code, decimal buy, decimal sell)
        {
            _prices[code] = (buy, sell);
            return this;
        }

        public IReadOnlyList<MarketRateDto> Rates => Empty
            ? new List<MarketRateDto>()
            : _prices.Select(p => new MarketRateDto { Code = p.Key, Name = p.Key, NameEn = p.Key, Buy = p.Value.Buy, Sell = p.Value.Sell, IsFallback = Fallback }).ToList();

        public Task<IReadOnlyList<MarketRateDto>> GetRatesAsync()
        {
            Calls++;
            return Task.FromResult(Rates);
        }

        public Task<MarketRateDto?> GetRateByCodeAsync(string code)
        {
            Calls++;
            return Task.FromResult(Rates.FirstOrDefault(r => r.Code.Equals(code, StringComparison.OrdinalIgnoreCase)));
        }
    }

    /// <summary>An HttpMessageHandler whose answers a test scripts; remembers every request it saw.</summary>
    public sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this((request, _) => Task.FromResult(respond(request)))
        {
        }

        public ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = new();

        public int Count { get { lock (Requests) return Requests.Count; } }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests) Requests.Add((request, body));
            return await _respond(request, cancellationToken);
        }

        public static HttpResponseMessage Json(string json, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
    }
}
