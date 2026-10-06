using System.ComponentModel.DataAnnotations;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartBank.API.Security;
using SmartBank.Core.Common;
using SmartBank.Core.DTOs;
using SmartBank.Core.Interfaces;
using SmartBank.Infrastructure.Services;

namespace SmartBank.API.Controllers
{
    /// <summary>
    /// The customer's accounts, cards, contacts, standing orders and money movements. Every action works on the signed-in
    /// user's own data only: ids and account numbers of other customers answer "not found".
    /// All errors have the body { isSuccess, errorKey, message } (see <see cref="SmartBankControllerBase.StatusFor"/> for the status codes).
    /// </summary>
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    [EnableRateLimiting(RateLimitPolicies.Banking)]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public class BankingController : SmartBankControllerBase
    {
        private readonly IBankingService _bankingService;
        private readonly IValidator<TransferRequestDto> _transferValidator;
        private readonly IConfiguration _configuration;

        public BankingController(IBankingService bankingService, IValidator<TransferRequestDto> transferValidator, IConfiguration configuration)
        {
            _bankingService = bankingService;
            _transferValidator = transferValidator;
            _configuration = configuration;
        }

        /// <summary>Is the credit-card charge / advance-period simulation switched on? (Demo:EnableSimulationEndpoints, default true.)</summary>
        private bool SimulationEnabled => _configuration.GetValue("Demo:EnableSimulationEndpoints", true);

        /// <summary>The caller's accounts, oldest first, with the (decrypted) debit card numbers.</summary>
        [HttpGet("accounts")]
        [ProducesResponseType(typeof(List<AccountDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetAccounts(CancellationToken cancellationToken)
            => ToActionResult(await _bankingService.GetAccountsAsync(GetUserId(), cancellationToken));

        /// <summary>The newest transactions of one of the caller's accounts, newest first. <paramref name="take"/> is 1..500 (default 200).</summary>
        [HttpGet("transactions/{accountId}")]
        [ProducesResponseType(typeof(List<TransactionDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetTransactions(Guid accountId, [FromQuery] int take = BankingService.DefaultTransactionsPerRequest, CancellationToken cancellationToken = default)
            => ToActionResult(await _bankingService.GetTransactionsAsync(accountId, GetUserId(), take, cancellationToken));

        /// <summary>
        /// Transfers money between two accounts of the same currency. A transfer that needs a one-time code answers 400 with
        /// errorKey Requires2FA, SuspectedFraudDuplicate or SuspectedFraudHighValue; the same request with the code in
        /// <c>otpCode</c> completes it.
        /// </summary>
        [HttpPost("transfer")]
        [EnableRateLimiting(RateLimitPolicies.Transfer)]
        [ProducesResponseType(typeof(TransactionDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Transfer([FromBody] TransferRequestDto transferRequest)
        {
            var validation = await _transferValidator.ValidateAsync(transferRequest);
            if (!validation.IsValid)
            {
                return ErrorResult("ValidationError", string.Join(" ", validation.Errors.Select(e => e.ErrorMessage)));
            }

            return ToActionResult(await _bankingService.TransferMoneyAsync(GetUserId(), transferRequest));
        }

        /// <summary>Opens an account in TRY, USD, EUR, XAU or XAG (demand or time deposit). At most 10 per customer.</summary>
        [HttpPost("accounts")]
        [ProducesResponseType(typeof(AccountDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> CreateAccount(
            [FromQuery, StringLength(10)] string currency = Currencies.Try,
            [FromQuery, StringLength(30)] string accountType = AccountTypes.DemandDeposit)
            => ToActionResult(await _bankingService.CreateAccountAsync(GetUserId(), currency, accountType));

        /// <summary>Closes an account. A balance must be moved to <paramref name="targetAccountId"/> (converted at live rates if the currency differs).</summary>
        [HttpDelete("accounts/{accountId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> DeleteAccount(Guid accountId, [FromQuery] Guid? targetAccountId = null)
            => ToActionResult(await _bankingService.DeleteAccountAsync(GetUserId(), accountId, targetAccountId), data => new { success = data });

        /// <summary>The caller's credit card (at most one).</summary>
        [HttpGet("credit-cards")]
        [ProducesResponseType(typeof(List<CreditCardDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetCreditCards(CancellationToken cancellationToken)
            => ToActionResult(await _bankingService.GetCreditCardsAsync(GetUserId(), cancellationToken));

        /// <summary>Issues the caller's credit card (the CVV is shown once, in this answer).</summary>
        [HttpPost("credit-cards")]
        [ProducesResponseType(typeof(CreditCardDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> CreateCreditCard()
            => ToActionResult(await _bankingService.CreateCreditCardAsync(GetUserId()));

        /// <summary>The newest 24 statements of a card with their transactions.</summary>
        [HttpGet("credit-cards/{cardId}/statements")]
        [ProducesResponseType(typeof(List<CreditCardStatementDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetStatements(Guid cardId, CancellationToken cancellationToken)
            => ToActionResult(await _bankingService.GetStatementsAsync(cardId, GetUserId(), cancellationToken));

        /// <summary>Pays card debt from a TRY account. The amount cannot be more than the current debt (errorKey PaymentExceedsDebt).</summary>
        [HttpPost("credit-cards/{cardId}/pay")]
        [EnableRateLimiting(RateLimitPolicies.Transfer)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> PayCreditCardDebt(Guid cardId, [FromBody] PayCreditCardDebtDto payRequest)
            => ToActionResult(await _bankingService.PayCreditCardDebtAsync(GetUserId(), cardId, payRequest), data => new { success = data });

        /// <summary>
        /// Simulation: a shop charges the card. Switched off (404) when Demo:EnableSimulationEndpoints is false.
        /// </summary>
        [HttpPost("credit-cards/{cardId}/charge")]
        [ProducesResponseType(typeof(CreditCardDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> ChargeCreditCard(
            Guid cardId,
            [FromQuery, Range(0.01, 10000000.00), MoneyScale] decimal amount,
            [FromQuery, StringLength(200)] string description = "")
        {
            if (!SimulationEnabled) return NotFound();

            return ToActionResult(await _bankingService.ChargeCreditCardAsync(GetUserId(), cardId, amount, description));
        }

        /// <summary>
        /// Simulation: closes the current statement and opens the next one (interest on what is unpaid) without waiting a month.
        /// Switched off (404) when Demo:EnableSimulationEndpoints is false.
        /// </summary>
        [HttpPost("credit-cards/{cardId}/advance-period")]
        [ProducesResponseType(typeof(CreditCardStatementDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> AdvancePeriod(Guid cardId)
        {
            if (!SimulationEnabled) return NotFound();

            return ToActionResult(await _bankingService.AdvanceStatementPeriodAsync(GetUserId(), cardId));
        }

        /// <summary>The caller's saved recipients (at most 100).</summary>
        [HttpGet("contacts")]
        [ProducesResponseType(typeof(List<SavedContactDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetSavedContacts(CancellationToken cancellationToken)
            => ToActionResult(await _bankingService.GetSavedContactsAsync(GetUserId(), cancellationToken));

        /// <summary>Saves a recipient, or renames it when the account number is already saved.</summary>
        [HttpPost("contacts")]
        [ProducesResponseType(typeof(SavedContactDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> SaveContact([FromBody] CreateSavedContactDto contactDto)
            => ToActionResult(await _bankingService.SaveContactAsync(GetUserId(), contactDto));

        [HttpDelete("contacts/{contactId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> DeleteContact(Guid contactId)
            => ToActionResult(await _bankingService.DeleteContactAsync(GetUserId(), contactId), data => new { success = data });

        /// <summary>The caller's standing orders, newest first.</summary>
        [HttpGet("standing-orders")]
        [ProducesResponseType(typeof(List<StandingOrderDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetStandingOrders(CancellationToken cancellationToken)
            => ToActionResult(await _bankingService.GetStandingOrdersAsync(GetUserId(), cancellationToken));

        /// <summary>
        /// Creates a standing order: a Transfer between two accounts of the same currency (amount up to 1,000,000), or a
        /// CreditCardAutoPay of the caller's card from a TRY account. At most 20 active orders.
        /// </summary>
        [HttpPost("standing-orders")]
        [ProducesResponseType(typeof(StandingOrderDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> CreateStandingOrder([FromBody] CreateStandingOrderDto orderDto)
            => ToActionResult(await _bankingService.CreateStandingOrderAsync(GetUserId(), orderDto));

        [HttpDelete("standing-orders/{orderId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> DeleteStandingOrder(Guid orderId)
            => ToActionResult(await _bankingService.DeleteStandingOrderAsync(GetUserId(), orderId), data => new { success = data });

        /// <summary>Buys or sells USD, EUR, XAU or XAG against TRY at the live rate. Refused (RateUnavailable) while only stand-in prices exist.</summary>
        [HttpPost("exchange")]
        [EnableRateLimiting(RateLimitPolicies.Transfer)]
        [ProducesResponseType(typeof(TransactionDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> ExchangeMoney([FromBody] ExchangeDto exchangeDto)
            => ToActionResult(await _bankingService.ExchangeMoneyAsync(GetUserId(), exchangeDto));

        /// <summary>Demo faucet: adds money to the caller's own account (up to 10,000,000 per call).</summary>
        [HttpPost("deposit")]
        [EnableRateLimiting(RateLimitPolicies.Transfer)]
        [ProducesResponseType(typeof(TransactionDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> DepositMoney([FromBody] DepositRequestDto depositRequest)
            => ToActionResult(await _bankingService.DepositMoneyAsync(GetUserId(), depositRequest.AccountNumber, depositRequest.Amount));
    }

}
