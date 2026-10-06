using System.Net;
using Microsoft.Extensions.Caching.Memory;
using SmartBank.Core.DTOs;
using SmartBank.Core.Interfaces;
using SmartBank.Infrastructure.Services;
using SmartBank.Tests.Support;

namespace SmartBank.Tests
{
    public class MarketRateTests
    {
        private const string Feed = @"{
            ""USD"": { ""Satış"": ""34,5000"", ""Değişim"": ""%0,45"" },
            ""EUR"": { ""Satış"": ""37,2500"", ""Değişim"": ""-0,30"" },
            ""gram-altin"": { ""Satış"": ""3.456,78"", ""Değişim"": ""%1,20"" },
            ""gumus"": { ""Satış"": ""41,10"", ""Değişim"": ""%0,00"" }
        }";

        private static (MarketRateService Service, ScriptedHandler Handler) Service(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            var handler = new ScriptedHandler(respond);
            return (new MarketRateService(new HttpClient(handler)), handler);
        }

        private static (MarketRateService Service, ScriptedHandler Handler) ServiceWith(string json) =>
            Service(_ => ScriptedHandler.Json(json));

        // ---- the live feed -----------------------------------------------------------------------------------

        [Fact]
        public async Task A_good_feed_gives_live_prices_with_the_bank_spread_and_no_stand_ins()
        {
            var (service, handler) = ServiceWith(Feed);

            var rates = await service.GetRatesAsync();

            Assert.Equal(4, rates.Count);
            Assert.All(rates, r => Assert.False(r.IsFallback));
            var usd = rates.Single(r => r.Code == "USD");
            Assert.Equal(Math.Round(34.5m * 0.9985m, 4), usd.Buy);
            Assert.Equal(Math.Round(34.5m * 1.0015m, 4), usd.Sell);
            Assert.Equal(0.45m, usd.Change);
            Assert.Equal(-0.30m, rates.Single(r => r.Code == "EUR").Change);
            var gold = rates.Single(r => r.Code == "XAU");
            Assert.Equal(Math.Round(3456.78m * 0.995m, 2), gold.Buy);
            Assert.Equal(Math.Round(3456.78m * 1.005m, 2), gold.Sell);
            Assert.Equal(1, handler.Count);
        }

        [Fact]
        public async Task The_request_goes_to_the_feed_with_a_plain_application_user_agent()
        {
            var (service, handler) = ServiceWith(Feed);

            await service.GetRatesAsync();

            var request = handler.Requests.Single().Request;
            Assert.Equal(MarketRateService.FeedUrl, request.RequestUri!.ToString());
            var agent = string.Join(" ", request.Headers.UserAgent.Select(a => a.ToString()));
            Assert.StartsWith("SmartBankDemo/", agent);
            Assert.DoesNotContain("Mozilla", agent);
        }

        [Theory]
        [InlineData("1.234,56", 1234.56)]
        [InlineData("34,5012", 34.5012)]
        [InlineData("2.450", 2450)]
        [InlineData("", 0)]
        [InlineData("n/a", 0)]
        public void Turkish_numbers_are_read_with_the_invariant_culture(string text, double expected)
        {
            Assert.Equal((decimal)expected, MarketRateService.ParseDecimalString(text));
        }

        [Theory]
        [InlineData("%0,45", 0.45)]
        [InlineData("-1,2", -1.2)]
        [InlineData("% 3", 3)]
        [InlineData(null, 0)]
        public void Percentages_are_read_with_the_invariant_culture(string? text, double expected)
        {
            Assert.Equal((decimal)expected, MarketRateService.ParsePercentString(text));
        }

        [Fact]
        public async Task Parsing_does_not_depend_on_the_machine_culture()
        {
            var previous = System.Globalization.CultureInfo.CurrentCulture;
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            try
            {
                var (service, _) = ServiceWith(Feed);
                var rates = await service.GetRatesAsync();
                Assert.Equal(Math.Round(34.5m * 0.9985m, 4), rates.Single(r => r.Code == "USD").Buy);
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = previous;
            }
        }

        // ---- when the feed fails -----------------------------------------------------------------------------

        [Fact]
        public async Task A_server_error_makes_every_price_a_flagged_stand_in()
        {
            var (service, _) = Service(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

            var rates = await service.GetRatesAsync();

            Assert.Equal(4, rates.Count);
            Assert.All(rates, r => Assert.True(r.IsFallback));
            Assert.All(rates, r => Assert.True(r.Buy > 0 && r.Sell > r.Buy));
        }

        [Fact]
        public async Task A_network_failure_and_broken_json_do_the_same()
        {
            var (down, _) = Service(_ => throw new HttpRequestException("no route to host"));
            var (garbage, _) = ServiceWith("this is not json");
            var (empty, _) = ServiceWith("{}");

            Assert.All(await down.GetRatesAsync(), r => Assert.True(r.IsFallback));
            Assert.All(await garbage.GetRatesAsync(), r => Assert.True(r.IsFallback));
            Assert.All(await empty.GetRatesAsync(), r => Assert.True(r.IsFallback));
        }

        [Fact]
        public async Task One_broken_entry_flags_only_that_price_and_keeps_the_others_live()
        {
            var json = @"{ ""USD"": { ""Satış"": ""34,50"" }, ""EUR"": { ""Alış"": ""36,00"" }, ""gram-altin"": { ""Satış"": ""3.456,78"" }, ""gumus"": ""oops"" }";
            var (service, _) = ServiceWith(json);

            var rates = await service.GetRatesAsync();

            Assert.False(rates.Single(r => r.Code == "USD").IsFallback);
            Assert.True(rates.Single(r => r.Code == "EUR").IsFallback);   // no "Satış"
            Assert.False(rates.Single(r => r.Code == "XAU").IsFallback);
            Assert.True(rates.Single(r => r.Code == "XAG").IsFallback);   // not an object
        }

        [Theory]
        [InlineData("0,01")]       // far below any real USD price
        [InlineData("999999,00")]  // far above
        [InlineData("0")]
        public async Task An_implausible_price_is_not_used_as_a_rate(string usd)
        {
            var (service, _) = ServiceWith(@"{ ""USD"": { ""Satış"": """ + usd + @""" } }");

            var rate = (await service.GetRateByCodeAsync("usd"))!;

            Assert.True(rate.IsFallback);
            Assert.InRange(rate.Sell, 30m, 40m);
        }

        [Fact]
        public async Task Stand_in_prices_do_not_drift_without_limit()
        {
            var (service, _) = Service(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

            for (var i = 0; i < 200; i++)
            {
                var gold = (await service.GetRateByCodeAsync("XAU"))!;
                Assert.InRange(gold.Sell, 2400m, 2500m); // always the same base value plus a small jitter
            }
        }

        [Fact]
        public async Task A_rate_is_found_whatever_the_case_and_an_unknown_code_gives_null()
        {
            var (service, _) = ServiceWith(Feed);

            Assert.NotNull(await service.GetRateByCodeAsync("xau"));
            Assert.Null(await service.GetRateByCodeAsync("GBP"));
        }

        // ---- the cache ---------------------------------------------------------------------------------------

        private sealed class CountingRates : IMarketRateService
        {
            private int _calls;
            public bool Fallback { get; set; }
            public TimeSpan Delay { get; set; }
            public int Calls => _calls;

            public async Task<IReadOnlyList<MarketRateDto>> GetRatesAsync()
            {
                Interlocked.Increment(ref _calls);
                if (Delay > TimeSpan.Zero) await Task.Delay(Delay);
                return new List<MarketRateDto> { new() { Code = "USD", Buy = 1, Sell = 2, IsFallback = Fallback } };
            }

            public async Task<MarketRateDto?> GetRateByCodeAsync(string code) => (await GetRatesAsync()).FirstOrDefault();
        }

        [Fact]
        public async Task Live_prices_are_served_from_the_cache()
        {
            var inner = new CountingRates();
            var cached = new CachedMarketRateService(inner, new MemoryCache(new MemoryCacheOptions()));

            await cached.GetRatesAsync();
            await cached.GetRatesAsync();
            await cached.GetRateByCodeAsync("USD");

            Assert.Equal(1, inner.Calls);
        }

        [Fact]
        public async Task Stand_ins_live_for_seconds_and_live_prices_for_minutes()
        {
            Assert.Equal(TimeSpan.FromMinutes(5), CachedMarketRateService.LiveRatesLifetime);
            Assert.Equal(TimeSpan.FromSeconds(30), CachedMarketRateService.FallbackRatesLifetime);

            // The cache is actually set with the short lifetime for stand-ins: with a clock we control, it expires after 30 seconds.
            var clock = new TestClock();
            var memory = new MemoryCache(new MemoryCacheOptions { Clock = new ClockAdapter(clock) });
            var inner = new CountingRates { Fallback = true };
            var cached = new CachedMarketRateService(inner, memory);

            await cached.GetRatesAsync();
            clock.Advance(TimeSpan.FromSeconds(20));
            await cached.GetRatesAsync();
            Assert.Equal(1, inner.Calls);

            clock.Advance(TimeSpan.FromSeconds(11));
            inner.Fallback = false;
            var fresh = await cached.GetRatesAsync();

            Assert.Equal(2, inner.Calls);
            Assert.False(fresh[0].IsFallback);

            clock.Advance(TimeSpan.FromMinutes(4));
            await cached.GetRatesAsync();
            Assert.Equal(2, inner.Calls); // live prices are still cached after four minutes
        }

#pragma warning disable CS0618 // ISystemClock is the way to give a MemoryCache a clock
        private sealed class ClockAdapter : Microsoft.Extensions.Internal.ISystemClock
        {
            private readonly TestClock _clock;
            public ClockAdapter(TestClock clock) => _clock = clock;
            public DateTimeOffset UtcNow => _clock.GetUtcNow();
        }
#pragma warning restore CS0618

        [Fact]
        public async Task Many_callers_with_an_empty_cache_cause_one_call_to_the_feed()
        {
            var inner = new CountingRates { Delay = TimeSpan.FromMilliseconds(150) };
            var cached = new CachedMarketRateService(inner, new MemoryCache(new MemoryCacheOptions()));

            var results = await Task.WhenAll(Enumerable.Range(0, 25).Select(_ => Task.Run(() => cached.GetRatesAsync())));

            Assert.Equal(1, inner.Calls);
            Assert.All(results, r => Assert.Single(r));
        }

        [Fact]
        public async Task An_empty_answer_is_not_cached()
        {
            var inner = new Mock_EmptyRates();
            var cached = new CachedMarketRateService(inner, new MemoryCache(new MemoryCacheOptions()));

            await cached.GetRatesAsync();
            await cached.GetRatesAsync();

            Assert.Equal(2, inner.Calls);
        }

        private sealed class Mock_EmptyRates : IMarketRateService
        {
            public int Calls { get; private set; }

            public Task<IReadOnlyList<MarketRateDto>> GetRatesAsync()
            {
                Calls++;
                return Task.FromResult<IReadOnlyList<MarketRateDto>>(new List<MarketRateDto>());
            }

            public Task<MarketRateDto?> GetRateByCodeAsync(string code) => Task.FromResult<MarketRateDto?>(null);
        }
    }
}
