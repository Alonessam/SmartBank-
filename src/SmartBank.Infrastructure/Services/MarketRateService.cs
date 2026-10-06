using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartBank.Core.Common;
using SmartBank.Core.DTOs;
using SmartBank.Core.Interfaces;

namespace SmartBank.Infrastructure.Services
{
    /// <summary>
    /// Reads the free-market rates from a public feed. When the feed is down, or one of its values is missing or makes no
    /// sense, that price is replaced by a stand-in and flagged with <see cref="MarketRateDto.IsFallback"/>. Stand-ins are
    /// only good enough to fill the rates box on the dashboard: BankingService refuses to trade or convert against them.
    /// </summary>
    public class MarketRateService : IMarketRateService
    {
        public const string FeedUrl = "https://finans.truncgil.com/today.json";

        /// <summary>What the bank pays (buy) and charges (sell) relative to the mid price of the feed.</summary>
        public static class Spread
        {
            public const decimal UsdBuy = 0.9985m, UsdSell = 1.0015m;
            public const decimal EurBuy = 0.9985m, EurSell = 1.0015m;
            public const decimal GoldBuy = 0.995m, GoldSell = 1.005m;
            public const decimal SilverBuy = 0.99m, SilverSell = 1.01m;
        }

        // A parsed price outside these bounds (per TRY, per gram) is treated as a broken feed, not as a rate.
        private static readonly IReadOnlyDictionary<string, (decimal Min, decimal Max)> Bounds = new Dictionary<string, (decimal, decimal)>
        {
            [Currencies.Usd] = (0.1m, 10_000m),
            [Currencies.Eur] = (0.1m, 10_000m),
            [Currencies.Gold] = (10m, 1_000_000m),
            [Currencies.Silver] = (0.1m, 100_000m)
        };

        // Stand-in mid prices. They do not accumulate drift: every call jitters the same base value.
        private const decimal FallbackUsd = 34.50m, FallbackEur = 37.30m, FallbackGold = 2450.00m, FallbackSilver = 31.10m;

        private readonly HttpClient _httpClient;
        private readonly ILogger<MarketRateService> _logger;

        public MarketRateService(HttpClient httpClient, ILogger<MarketRateService>? logger = null)
        {
            _httpClient = httpClient;
            _logger = logger ?? NullLogger<MarketRateService>.Instance;

            if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
            {
                _httpClient.DefaultRequestHeaders.Add("User-Agent", "SmartBankDemo/1.3");
            }
        }

        public async Task<IReadOnlyList<MarketRateDto>> GetRatesAsync()
        {
            var feed = await ReadFeedAsync();

            return new List<MarketRateDto>
            {
                Build(Currencies.Usd, "Amerikan Doları", "US Dollar", feed, "USD", FallbackUsd, 0.04, 4, Spread.UsdBuy, Spread.UsdSell, 0.4),
                Build(Currencies.Eur, "Euro", "Euro", feed, "EUR", FallbackEur, 0.04, 4, Spread.EurBuy, Spread.EurSell, 0.4),
                Build(Currencies.Gold, "Gram Altın", "Gram Gold", feed, "gram-altin", FallbackGold, 2.0, 2, Spread.GoldBuy, Spread.GoldSell, 1.2),
                Build(Currencies.Silver, "Gram Gümüş", "Gram Silver", feed, "gumus", FallbackSilver, 0.1, 2, Spread.SilverBuy, Spread.SilverSell, 1.6)
            };
        }

        public async Task<MarketRateDto?> GetRateByCodeAsync(string code)
        {
            var rates = await GetRatesAsync();
            return rates.FirstOrDefault(r => r.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        }

        private sealed record FeedValue(decimal Spot, decimal Change);

        private MarketRateDto Build(string code, string name, string nameEn, IReadOnlyDictionary<string, FeedValue> feed, string feedKey,
            decimal fallbackSpot, double jitterSpot, int decimals, decimal buySpread, decimal sellSpread, double jitterChange)
        {
            var isFallback = !feed.TryGetValue(feedKey, out var value);
            var spot = isFallback ? Math.Round(fallbackSpot + (decimal)(Random.Shared.NextDouble() * jitterSpot - jitterSpot / 2), decimals) : value!.Spot;
            var change = isFallback || value!.Change == 0m
                ? Math.Round((decimal)(Random.Shared.NextDouble() * jitterChange - jitterChange / 2), 2)
                : value.Change;

            return new MarketRateDto
            {
                Code = code,
                Name = name,
                NameEn = nameEn,
                Buy = Math.Round(spot * buySpread, decimals),
                Sell = Math.Round(spot * sellSpread, decimals),
                Change = change,
                IsFallback = isFallback
            };
        }

        /// <summary>Every value of the feed that could be read and passed the bounds check. Missing ones are simply absent.</summary>
        private async Task<IReadOnlyDictionary<string, FeedValue>> ReadFeedAsync()
        {
            var result = new Dictionary<string, FeedValue>();

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var response = await _httpClient.GetAsync(FeedUrl, cts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("The market-rate feed answered HTTP {Status}; stand-in prices are used.", (int)response.StatusCode);
                    return result;
                }

                var json = await response.Content.ReadAsStringAsync(cts.Token);
                using var doc = JsonDocument.Parse(json);

                foreach (var (feedKey, code) in new[] { ("USD", Currencies.Usd), ("EUR", Currencies.Eur), ("gram-altin", Currencies.Gold), ("gumus", Currencies.Silver) })
                {
                    try
                    {
                        if (!doc.RootElement.TryGetProperty(feedKey, out var element)) continue;

                        var spot = ParseDecimalString(element.GetProperty("Satış").GetString());
                        var (min, max) = Bounds[code];
                        if (spot < min || spot > max)
                        {
                            _logger.LogWarning("The market-rate feed gave an implausible {Code} price ({Spot}); a stand-in is used.", code, spot);
                            continue;
                        }

                        var change = element.TryGetProperty("Değişim", out var changeElement) ? ParsePercentString(changeElement.GetString()) : 0m;
                        result[feedKey] = new FeedValue(spot, change);
                    }
                    catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
                    {
                        // One broken entry must not hide the others.
                        _logger.LogWarning(ex, "The market-rate feed entry {Code} could not be read; a stand-in is used.", code);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The market-rate feed could not be read; stand-in prices are used.");
                result.Clear();
            }

            return result;
        }

        internal static decimal ParseDecimalString(string? value)
        {
            if (string.IsNullOrEmpty(value)) return 0m;

            // The feed writes Turkish numbers: "1.234,56" (dot groups thousands, comma is the decimal separator).
            var cleaned = value.Replace(".", "").Replace(",", ".");
            return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var result) ? result : 0m;
        }

        internal static decimal ParsePercentString(string? value)
        {
            if (string.IsNullOrEmpty(value)) return 0m;

            var cleaned = value.Replace("%", "").Replace(" ", "").Replace(",", ".");
            return decimal.TryParse(cleaned, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var result) ? result : 0m;
        }
    }
}
