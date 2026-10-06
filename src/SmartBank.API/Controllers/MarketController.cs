using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartBank.API.Security;
using SmartBank.Core.DTOs;
using SmartBank.Core.Interfaces;

namespace SmartBank.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [EnableRateLimiting(RateLimitPolicies.Market)]
    [Produces("application/json")]
    public class MarketController : ControllerBase
    {
        private readonly IMarketRateService _marketRateService;

        public MarketController(IMarketRateService marketRateService)
        {
            _marketRateService = marketRateService;
        }

        /// <summary>
        /// The free-market rates (USD, EUR, gram gold, gram silver). Public. A price with <c>isFallback: true</c> is a stand-in
        /// shown while the live feed is down: the bank does not trade at it.
        /// </summary>
        [HttpGet("rates")]
        [ProducesResponseType(typeof(IReadOnlyList<MarketRateDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public async Task<IActionResult> GetRates()
        {
            var rates = await _marketRateService.GetRatesAsync();
            return Ok(rates);
        }
    }
}
