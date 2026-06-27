using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SmartBank.Core.Common;
using SmartBank.Core.DTOs;

namespace SmartBank.Core.Interfaces
{
    public interface IBankingService
    {
        Task<ServiceResult<List<AccountDto>>> GetAccountsAsync(Guid userId);
        Task<ServiceResult<List<TransactionDto>>> GetTransactionsAsync(Guid accountId, Guid userId);
        Task<ServiceResult<TransactionDto>> TransferMoneyAsync(Guid userId, TransferRequestDto transferRequest);
        Task<ServiceResult<AccountDto>> CreateAccountAsync(Guid userId, string currency, string accountType = "DemandDeposit");
        Task<ServiceResult<List<CreditCardDto>>> GetCreditCardsAsync(Guid userId);
        Task<ServiceResult<CreditCardDto>> CreateCreditCardAsync(Guid userId);
        Task<ServiceResult<List<CreditCardStatementDto>>> GetStatementsAsync(Guid cardId, Guid userId);
        Task<ServiceResult<bool>> PayCreditCardDebtAsync(Guid userId, Guid cardId, PayCreditCardDebtDto payRequest);
        Task<ServiceResult<CreditCardDto>> ChargeCreditCardAsync(Guid userId, Guid cardId, decimal amount, string description);
        Task<ServiceResult<CreditCardStatementDto>> AdvanceStatementPeriodAsync(Guid userId, Guid cardId);
        Task<ServiceResult<bool>> DeleteAccountAsync(Guid userId, Guid accountId, Guid? transferTargetAccountId = null);
        Task<ServiceResult<List<SavedContactDto>>> GetSavedContactsAsync(Guid userId);
        Task<ServiceResult<SavedContactDto>> SaveContactAsync(Guid userId, CreateSavedContactDto contactDto);
        Task<ServiceResult<bool>> DeleteContactAsync(Guid userId, Guid contactId);
        Task<ServiceResult<List<StandingOrderDto>>> GetStandingOrdersAsync(Guid userId);
        Task<ServiceResult<StandingOrderDto>> CreateStandingOrderAsync(Guid userId, CreateStandingOrderDto orderDto);
        Task<ServiceResult<bool>> DeleteStandingOrderAsync(Guid userId, Guid orderId);
        Task<ServiceResult<TransactionDto>> ExchangeMoneyAsync(Guid userId, ExchangeDto exchangeDto);
        Task<ServiceResult<TransactionDto>> DepositMoneyAsync(Guid userId, string accountNumber, decimal amount);
    }
}
