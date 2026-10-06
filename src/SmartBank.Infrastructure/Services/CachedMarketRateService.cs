using Microsoft.Extensions.Caching.Memory;
using SmartBank.Core.DTOs;
using SmartBank.Core.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SmartBank.Infrastructure.Services
{
    /// <summary>
    /// Keeps the market rates in memory. Live prices are kept for five minutes; a list that contains any stand-in price is
    /// kept for 30 seconds only, so the live feed is tried again soon and a stand-in never lives long. Only one caller at a
    /// time asks the feed when the cache is empty (the others wait for that answer instead of each making their own call).
    /// </summary>
    public class CachedMarketRateService : IMarketRateService
    {
        private readonly IMarketRateService _innerService;
        private readonly IMemoryCache _cache;
        private const string RatesCacheKey = "MarketRates_CacheKey";

        public static readonly TimeSpan LiveRatesLifetime = TimeSpan.FromMinutes(5);
        public static readonly TimeSpan FallbackRatesLifetime = TimeSpan.FromSeconds(30);

        // This class is created per request, the cache is shared: so the lock has to be shared too.
        private static readonly SemaphoreSlim RefreshLock = new(1, 1);

        public CachedMarketRateService(IMarketRateService innerService, IMemoryCache cache)
        {
            _innerService = innerService;
            _cache = cache;
        }

        public async Task<IReadOnlyList<MarketRateDto>> GetRatesAsync()
        {
            if (_cache.TryGetValue(RatesCacheKey, out IReadOnlyList<MarketRateDto>? cached) && cached != null)
            {
                return cached;
            }

            await RefreshLock.WaitAsync();
            try
            {
                // Someone else may have filled the cache while this caller waited.
                if (_cache.TryGetValue(RatesCacheKey, out cached) && cached != null)
                {
                    return cached;
                }

                var rates = await _innerService.GetRatesAsync() ?? new List<MarketRateDto>();
                if (rates.Count > 0)
                {
                    var lifetime = rates.Any(r => r.IsFallback) ? FallbackRatesLifetime : LiveRatesLifetime;
                    _cache.Set(RatesCacheKey, rates, lifetime);
                }

                return rates;
            }
            finally
            {
                RefreshLock.Release();
            }
        }

        public async Task<MarketRateDto?> GetRateByCodeAsync(string code)
        {
            var rates = await GetRatesAsync();
            return rates.FirstOrDefault(r => r.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        }
    }
}
