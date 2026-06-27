using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using SmartBank.Core.Interfaces;

namespace SmartBank.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MarketController : ControllerBase
    {
        private readonly IMarketRateService _marketRateService;

        public MarketController(IMarketRateService marketRateService)
        {
            _marketRateService = marketRateService;
        }

        [HttpGet("rates")]
        public async Task<IActionResult> GetRates()
        {
            var rates = await _marketRateService.GetRatesAsync();
            return Ok(rates);
        }
    }
}
