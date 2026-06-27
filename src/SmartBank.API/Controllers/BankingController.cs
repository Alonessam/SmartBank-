using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartBank.Core.DTOs;
using SmartBank.Core.Interfaces;
using FluentValidation;
using System.Linq;

namespace SmartBank.API.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class BankingController : ControllerBase
    {
        private readonly IBankingService _bankingService;
        private readonly IValidator<TransferRequestDto> _transferValidator;

        public BankingController(IBankingService bankingService, IValidator<TransferRequestDto> transferValidator)
        {
            _bankingService = bankingService;
            _transferValidator = transferValidator;
        }

        [HttpGet("accounts")]
        public async Task<IActionResult> GetAccounts()
        {
            var userId = GetUserId();
            if (userId == Guid.Empty)
            {
                return Unauthorized();
            }

            var result = await _bankingService.GetAccountsAsync(userId);
            return Ok(result.Data);
        }

        [HttpGet("transactions/{accountId}")]
        public async Task<IActionResult> GetTransactions(Guid accountId)
        {
            var userId = GetUserId();
            if (userId == Guid.Empty)
            {
                return Unauthorized();
            }

            var result = await _bankingService.GetTransactionsAsync(accountId, userId);

            if (!result.IsSuccess)
            {
                return BadRequest(new { result.IsSuccess, result.ErrorKey, result.Message });
            }

            return Ok(result.Data);
        }

        [HttpPost("transfer")]
        public async Task<IActionResult> Transfer([FromBody] TransferRequestDto transferRequest)
        {
            var validationResult = await _transferValidator.ValidateAsync(transferRequest);
            if (!validationResult.IsValid)
            {
                return BadRequest(new { 
                    IsSuccess = false, 
                    ErrorKey = "ValidationError", 
                    Message = string.Join(" ", validationResult.Errors.Select(e => e.ErrorMessage)) 
                });
            }

            var userId = GetUserId();
            if (userId == Guid.Empty)
            {
                return Unauthorized();
            }

            var result = await _bankingService.TransferMoneyAsync(userId, transferRequest);

            if (!result.IsSuccess)
            {
                return BadRequest(new { result.IsSuccess, result.ErrorKey, result.Message });
            }

            return Ok(result.Data);
        }

        [HttpPost("accounts")]
        public async Task<IActionResult> CreateAccount([FromQuery] string currency = "TRY", [FromQuery] string accountType = "DemandDeposit")
        {
            var userId = GetUserId();
            if (userId == Guid.Empty) return Unauthorized();

            var result = await _bankingService.CreateAccountAsync(userId, currency, accountType);
            if (!result.IsSuccess)
            {
                return BadRequest(new { result.IsSuccess, result.ErrorKey, result.Message });
            }
            return Ok(result.Data);
        }

        [HttpGet("credit-cards")]
        public async Task<IActionResult> GetCreditCards()
        {
            var userId = GetUserId();
            if (userId == Guid.Empty) return Unauthorized();

            var result = await _bankingService.GetCreditCardsAsync(userId);
            return Ok(result.Data);
        }

        [HttpPost("credit-cards")]
        public async Task<IActionResult> CreateCreditCard()
        {
            var userId = GetUserId();
            if (userId == Guid.Empty) return Unauthorized();

            var result = await _bankingService.CreateCreditCardAsync(userId);
            if (!result.IsSuccess)
            {
                return BadRequest(new { result.IsSuccess, result.ErrorKey, result.Message });
            }
            return Ok(result.Data);
        }

        [HttpGet("credit-cards/{cardId}/statements")]
        public async Task<IActionResult> GetStatements(Guid cardId)
        {
            var userId = GetUserId();
            if (userId == Guid.Empty) return Unauthorized();

            var result = await _bankingService.GetStatementsAsync(cardId, userId);
            if (!result.IsSuccess)
            {
                return BadRequest(new { result.IsSuccess, result.ErrorKey, result.Message });
            }
            return Ok(result.Data);
        }

        [HttpPost("credit-cards/{cardId}/pay")]
        public async Task<IActionResult> PayCreditCardDebt(Guid cardId, [FromBody] PayCreditCardDebtDto payRequest)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var userId = GetUserId();
            if (userId == Guid.Empty) return Unauthorized();

            var result = await _bankingService.PayCreditCardDebtAsync(userId, cardId, payRequest);
            if (!result.IsSuccess)
            {
                return BadRequest(new { result.IsSuccess, result.ErrorKey, result.Message });
            }
            return Ok(new { success = result.Data });
        }

        [HttpPost("credit-cards/{cardId}/charge")]
        public async Task<IActionResult> ChargeCreditCard(Guid cardId, [FromQuery] decimal amount, [FromQuery] string description = "")
        {
            var userId = GetUserId();
            if (userId == Guid.Empty) return Unauthorized();

            var result = await _bankingService.ChargeCreditCardAsync(userId, cardId, amount, description);
            if (!result.IsSuccess)
            {
                return BadRequest(new { result.IsSuccess, result.ErrorKey, result.Message });
            }
            return Ok(result.Data);
        }

        [HttpPost("credit-cards/{cardId}/advance-period")]
        public async Task<IActionResult> AdvancePeriod(Guid cardId)
        {
            var userId = GetUserId();
            if (userId == Guid.Empty) return Unauthorized();

            var result = await _bankingService.AdvanceStatementPeriodAsync(userId, cardId);
            if (!result.IsSuccess)
            {
                return BadRequest(new { result.IsSuccess, result.ErrorKey, result.Message });
            }
            return Ok(result.Data);
        }

        [HttpDelete("accounts/{accountId}")]
        public async Task<IActionResult> DeleteAccount(Guid accountId, [FromQuery] Guid? targetAccountId = null)
        {
            var userId = GetUserId();
            if (userId == Guid.Empty) return Unauthorized();

            var result = await _bankingService.DeleteAccountAsync(userId, accountId, targetAccountId);
            if (!result.IsSuccess)
            {
                return BadRequest(new { result.IsSuccess, result.ErrorKey, result.Message });
            }
            return Ok(new { success = result.Data });
        }

        [HttpGet("contacts")]
        public async Task<IActionResult> GetSavedContacts()
        {
            var userId = GetUserId();
            if (userId == Guid.Empty) return Unauthorized();

            var result = await _bankingService.GetSavedContactsAsync(userId);
            if (!result.IsSuccess)
            {
                return BadRequest(new { result.IsSuccess, result.ErrorKey, result.Message });
            }
            return Ok(result.Data);
        }

        [HttpPost("contacts")]
        public async Task<IActionResult> SaveContact([FromBody] CreateSavedContactDto contactDto)
        {
            var userId = GetUserId();
            if (userId == Guid.Empty) return Unauthorized();

            var result = await _bankingService.SaveContactAsync(userId, contactDto);
            if (!result.IsSuccess)
            {
                return BadRequest(new { result.IsSuccess, result.ErrorKey, result.Message });
            }
            return Ok(result.Data);
        }

        [HttpDelete("contacts/{contactId}")]
        public async Task<IActionResult> DeleteContact(Guid contactId)
        {
            var userId = GetUserId();
            if (userId == Guid.Empty) return Unauthorized();

            var result = await _bankingService.DeleteContactAsync(userId, contactId);
            if (!result.IsSuccess)
            {
                return BadRequest(new { result.IsSuccess, result.ErrorKey, result.Message });
            }
            return Ok(new { success = result.Data });
        }

        [HttpGet("standing-orders")]
        public async Task<IActionResult> GetStandingOrders()
        {
            var userId = GetUserId();
            if (userId == Guid.Empty) return Unauthorized();

            var result = await _bankingService.GetStandingOrdersAsync(userId);
            if (!result.IsSuccess)
            {
                return BadRequest(new { result.IsSuccess, result.ErrorKey, result.Message });
            }
            return Ok(result.Data);
        }

        [HttpPost("standing-orders")]
        public async Task<IActionResult> CreateStandingOrder([FromBody] CreateStandingOrderDto orderDto)
        {
            var userId = GetUserId();
            if (userId == Guid.Empty) return Unauthorized();

            var result = await _bankingService.CreateStandingOrderAsync(userId, orderDto);
            if (!result.IsSuccess)
            {
                return BadRequest(new { result.IsSuccess, result.ErrorKey, result.Message });
            }
            return Ok(result.Data);
        }

        [HttpDelete("standing-orders/{orderId}")]
        public async Task<IActionResult> DeleteStandingOrder(Guid orderId)
        {
            var userId = GetUserId();
            if (userId == Guid.Empty) return Unauthorized();

            var result = await _bankingService.DeleteStandingOrderAsync(userId, orderId);
            if (!result.IsSuccess)
            {
                return BadRequest(new { result.IsSuccess, result.ErrorKey, result.Message });
            }
            return Ok(new { success = result.Data });
        }

        [HttpPost("exchange")]
        public async Task<IActionResult> ExchangeMoney([FromBody] ExchangeDto exchangeDto)
        {
            var userId = GetUserId();
            if (userId == Guid.Empty) return Unauthorized();

            var result = await _bankingService.ExchangeMoneyAsync(userId, exchangeDto);
            if (!result.IsSuccess)
            {
                return BadRequest(new { result.IsSuccess, result.ErrorKey, result.Message });
            }
            return Ok(result.Data);
        }

        [HttpPost("deposit")]
        public async Task<IActionResult> DepositMoney([FromBody] DepositRequestDto depositRequest)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var userId = GetUserId();
            if (userId == Guid.Empty) return Unauthorized();

            var result = await _bankingService.DepositMoneyAsync(userId, depositRequest.AccountNumber, depositRequest.Amount);
            if (!result.IsSuccess)
            {
                return BadRequest(new { result.IsSuccess, result.ErrorKey, result.Message });
            }
            return Ok(result.Data);
        }

        private Guid GetUserId()
        {
            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return Guid.TryParse(userIdStr, out var userId) ? userId : Guid.Empty;
        }
    }
}
