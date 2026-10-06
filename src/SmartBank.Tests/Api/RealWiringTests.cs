using Microsoft.Extensions.DependencyInjection;
using SmartBank.API.Hubs;
using SmartBank.API.Services;
using SmartBank.Core.Interfaces;
using SmartBank.Infrastructure.Services;
using SmartBank.Tests.Support;

namespace SmartBank.Tests.Api
{
    /// <summary>The registrations in Program.cs as production runs them: nothing is replaced by a fake here.</summary>
    [Collection("EncryptionHelper")]
    public class RealWiringTests : IClassFixture<RealWiringApiFactory>
    {
        private readonly RealWiringApiFactory _factory;

        public RealWiringTests(RealWiringApiFactory factory) => _factory = factory;

        [Fact]
        public void The_chatbot_is_the_failover_chain_and_the_faq_search_is_one_instance_for_the_whole_application()
        {
            using var first = _factory.Services.CreateScope();
            using var second = _factory.Services.CreateScope();

            Assert.IsType<FailoverChatbotService>(first.ServiceProvider.GetRequiredService<IAIChatbotService>());
            var rag = first.ServiceProvider.GetRequiredService<IRAGService>();
            Assert.IsType<RAGService>(rag);
            Assert.Same(rag, second.ServiceProvider.GetRequiredService<IRAGService>());
        }

        [Fact]
        public void The_market_rates_are_cached_decorators_and_the_banking_services_resolve_with_the_shared_clock()
        {
            using var scope = _factory.Services.CreateScope();

            Assert.IsType<CachedMarketRateService>(scope.ServiceProvider.GetRequiredService<IMarketRateService>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IBankingService>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IAuthService>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IChatService>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<SupportAiResponder>());
            Assert.Same(TimeProvider.System, _factory.Services.GetRequiredService<TimeProvider>());
            Assert.NotNull(_factory.Services.GetRequiredService<HubTokenExpiryFilter>());
        }

        [Fact]
        public async Task The_real_faq_file_is_found_and_answers_a_lost_card_question_without_ollama()
        {
            var rag = _factory.Services.GetRequiredService<IRAGService>();

            var answer = await rag.SearchFAQAsync("kartımı kaybettim, kart çalındı");

            Assert.NotNull(answer);
            Assert.Contains("Müşteri Hizmetleri", answer);
        }
    }
}
