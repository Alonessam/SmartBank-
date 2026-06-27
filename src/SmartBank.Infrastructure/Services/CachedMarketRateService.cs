using Microsoft.Extensions.Caching.Memory;
using SmartBank.Core.DTOs;
using SmartBank.Core.Interfaces;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SmartBank.Infrastructure.Services
{
    public class CachedMarketRateService : IMarketRateService
    {
        private readonly IMarketRateService _innerService;
        private readonly IMemoryCache _cache;
        private const string RatesCacheKey = "MarketRates_CacheKey";
        private static readonly TimeSpan CacheExpiration = TimeSpan.FromMinutes(5);

        public CachedMarketRateService(IMarketRateService innerService, IMemoryCache cache)
        {
            _innerService = innerService;
            _cache = cache;
        }

        public async Task<IReadOnlyList<MarketRateDto>> GetRatesAsync()
        {
            if (!_cache.TryGetValue(RatesCacheKey, out IReadOnlyList<MarketRateDto>? rates) || rates == null)
            {
                rates = await _innerService.GetRatesAsync();
                
                if (rates != null && rates.Count > 0)
                {
                    _cache.Set(RatesCacheKey, rates, CacheExpiration);
                }
            }

            return rates ?? new List<MarketRateDto>();
        }

        public async Task<MarketRateDto?> GetRateByCodeAsync(string code)
        {
            var rates = await GetRatesAsync();
            return System.Linq.Enumerable.FirstOrDefault(rates, r => r.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        }
    }
}
