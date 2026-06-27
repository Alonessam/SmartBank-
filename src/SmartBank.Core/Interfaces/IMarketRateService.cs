using System.Collections.Generic;
using System.Threading.Tasks;
using SmartBank.Core.DTOs;

namespace SmartBank.Core.Interfaces
{
    public interface IMarketRateService
    {
        Task<IReadOnlyList<MarketRateDto>> GetRatesAsync();
        Task<MarketRateDto?> GetRateByCodeAsync(string code);
    }
}
