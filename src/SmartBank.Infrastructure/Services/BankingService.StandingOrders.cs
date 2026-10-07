using Microsoft.EntityFrameworkCore;
using SmartBank.Core.Common;
using SmartBank.Core.DTOs;
using SmartBank.Core.Entities;
using SmartBank.Infrastructure.Data;

namespace SmartBank.Infrastructure.Services
{
    public partial class BankingService
    {
        public const int MaxActiveStandingOrders = 20;
        public const int MaxContacts = 100;

        // ---- standing orders ----------------------------------------------------------------------------------

        public async Task<ServiceResult<List<StandingOrderDto>>> GetStandingOrdersAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            // Decryption cannot be translated to SQL, so load the encrypted value and map in memory.
            var rows = await _context.StandingOrders
                .AsNoTracking()
                .Where(so => so.UserId == userId)
                .OrderByDescending(so => so.CreatedAt)
                .Take(200)
                .Select(so => new
                {
                    Order = so,
                    EncryptedCardNumber = so.CreditCard != null ? so.CreditCard.EncryptedCardNumber : null
                })
                .ToListAsync(cancellationToken);

            var orders = rows
                .Select(r => BankingMappers.ToDto(r.Order, r.EncryptedCardNumber == null ? null : CardMasking.LastFour(SafeDecrypt(r.EncryptedCardNumber))))
                .ToList();

            return ServiceResult<List<StandingOrderDto>>.Success(orders);
        }

        public async Task<ServiceResult<StandingOrderDto>> CreateStandingOrderAsync(Guid userId, CreateStandingOrderDto orderDto)
        {
            var orderType = OrderTypes.Normalize(orderDto.OrderType);
            if (orderType == null)
            {
                return Fail<StandingOrderDto>("InvalidOrderType", "Order type must be Transfer or CreditCardAutoPay.");
            }

            var frequency = Frequencies.Normalize(orderDto.Frequency);
            if (frequency == null)
            {
                return Fail<StandingOrderDto>("InvalidFrequency", "Frequency must be Daily, Weekly or Monthly.");
            }

            var sourceNumber = NormalizeAccountNumber(orderDto.SourceAccountNumber);
            var source = await _context.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.AccountNumber == sourceNumber && a.UserId == userId);
            if (source == null)
            {
                return Fail<StandingOrderDto>("SourceAccountNotFound", "Kaynak hesap bulunamadı.");
            }

            if (await _context.StandingOrders.CountAsync(o => o.UserId == userId && o.IsActive) >= MaxActiveStandingOrders)
            {
                return Fail<StandingOrderDto>("StandingOrderLimitReached", $"You can have at most {MaxActiveStandingOrders} active standing orders.");
            }

            string? destinationNumber = null;
            decimal? amount = null;
            Guid? creditCardId = null;

            if (orderType == OrderTypes.Transfer)
            {
                if (orderDto.Amount == null)
                {
                    return Fail<StandingOrderDto>("InvalidAmount", "A transfer order needs an amount.");
                }

                var invalid = ValidateAmount(orderDto.Amount.Value, Money.MaxStandingOrderAmount);
                if (invalid != null) return Fail<StandingOrderDto>(invalid.Value.Key, invalid.Value.Message);
                amount = orderDto.Amount.Value;

                destinationNumber = NormalizeAccountNumber(orderDto.DestinationAccountNumber);
                if (string.IsNullOrEmpty(destinationNumber))
                {
                    return Fail<StandingOrderDto>("DestinationAccountNotFound", "A transfer order needs a destination account.");
                }

                if (string.Equals(destinationNumber, sourceNumber, StringComparison.Ordinal))
                {
                    return Fail<StandingOrderDto>("CannotTransferToSelf", "Cannot transfer money to the same account.");
                }

                var destination = await _context.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.AccountNumber == destinationNumber);
                if (destination == null)
                {
                    return Fail<StandingOrderDto>("DestinationAccountNotFound", "Destination account was not found.");
                }

                if (destination.Currency != source.Currency)
                {
                    return Fail<StandingOrderDto>("CurrencyMismatch", "A standing order can only move money between accounts of the same currency.");
                }
            }
            else
            {
                // An automatic card payment always pays the open statement; the amount and destination of the request are ignored.
                if (orderDto.CreditCardId == null ||
                    !await _context.CreditCards.AsNoTracking().AnyAsync(c => c.Id == orderDto.CreditCardId && c.UserId == userId))
                {
                    return Fail<StandingOrderDto>("CreditCardNotFound", "Credit card not found.");
                }

                if (source.Currency != Currencies.Try)
                {
                    return Fail<StandingOrderDto>("CurrencyMismatch", "Only TRY accounts can be used to pay credit card debt.");
                }

                creditCardId = orderDto.CreditCardId;
            }

            var now = UtcNow;
            var order = new StandingOrder
            {
                UserId = userId,
                SourceAccountNumber = sourceNumber,
                DestinationAccountNumber = destinationNumber,
                Amount = amount,
                Frequency = frequency,
                OrderType = orderType,
                CreditCardId = creditCardId,
                CreatedAt = now,
                MaturityDate = now.AddYears(1),
                NextExecutionDate = now
            };

            _context.StandingOrders.Add(order);
            await _context.SaveChangesAsync();

            return ServiceResult<StandingOrderDto>.Success(BankingMappers.ToDto(order));
        }

        public Task<ServiceResult<bool>> DeleteStandingOrderAsync(Guid userId, Guid orderId) =>
            // The worker moves NextExecutionDate (the row's concurrency token) while it runs an order: a delete that collides with it starts over.
            RunWithConcurrencyRetryAsync(async () =>
            {
                var order = await _context.StandingOrders.FirstOrDefaultAsync(so => so.Id == orderId && so.UserId == userId);
                if (order == null)
                {
                    return Fail<bool>("OrderNotFound", "Talimat bulunamadı.");
                }

                _context.StandingOrders.Remove(order);
                await _context.SaveChangesAsync();
                return ServiceResult<bool>.Success(true);
            });

        // ---- saved contacts -----------------------------------------------------------------------------------

        public async Task<ServiceResult<List<SavedContactDto>>> GetSavedContactsAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            var contacts = await _context.SavedContacts
                .AsNoTracking()
                .Where(sc => sc.UserId == userId)
                .OrderByDescending(sc => sc.CreatedAt)
                .Take(MaxContacts)
                .ToListAsync(cancellationToken);

            return ServiceResult<List<SavedContactDto>>.Success(contacts.Select(BankingMappers.ToDto).ToList());
        }

        public async Task<ServiceResult<SavedContactDto>> SaveContactAsync(Guid userId, CreateSavedContactDto contactDto)
        {
            var accountNumber = NormalizeAccountNumber(contactDto.AccountNumber);
            var alias = contactDto.Alias?.Trim() ?? string.Empty;
            if (accountNumber.Length < 10 || accountNumber.Length > 30)
            {
                return Fail<SavedContactDto>("InvalidAccountNumber", "Account number must be between 10 and 30 characters.");
            }

            if (alias.Length == 0 || alias.Length > 100)
            {
                return Fail<SavedContactDto>("InvalidAlias", "Alias must be between 1 and 100 characters.");
            }

            var existing = await _context.SavedContacts.FirstOrDefaultAsync(sc => sc.UserId == userId && sc.AccountNumber == accountNumber);
            if (existing != null)
            {
                existing.Alias = alias;
                await _context.SaveChangesAsync();
                return ServiceResult<SavedContactDto>.Success(BankingMappers.ToDto(existing));
            }

            if (await _context.SavedContacts.CountAsync(sc => sc.UserId == userId) >= MaxContacts)
            {
                return Fail<SavedContactDto>("ContactLimitReached", $"You can save at most {MaxContacts} recipients.");
            }

            var contact = new SavedContact { UserId = userId, AccountNumber = accountNumber, Alias = alias, CreatedAt = UtcNow };
            _context.SavedContacts.Add(contact);

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (DatabaseConflict.IsUniqueViolation(ex))
            {
                // The same recipient was saved by a parallel request: this one only updates the alias.
                _context.Entry(contact).State = EntityState.Detached;
                var winner = await _context.SavedContacts.FirstAsync(sc => sc.UserId == userId && sc.AccountNumber == accountNumber);
                winner.Alias = alias;
                await _context.SaveChangesAsync();
                return ServiceResult<SavedContactDto>.Success(BankingMappers.ToDto(winner));
            }

            return ServiceResult<SavedContactDto>.Success(BankingMappers.ToDto(contact));
        }

        public async Task<ServiceResult<bool>> DeleteContactAsync(Guid userId, Guid contactId)
        {
            var contact = await _context.SavedContacts.FirstOrDefaultAsync(sc => sc.Id == contactId && sc.UserId == userId);
            if (contact == null)
            {
                return Fail<bool>("ContactNotFound", "Kayıtlı alıcı bulunamadı.");
            }

            _context.SavedContacts.Remove(contact);
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                // A double click or a second tab removed it first: the wanted state (it is gone) is reached either way.
                _context.ChangeTracker.Clear();
            }

            return ServiceResult<bool>.Success(true);
        }
    }
}
