using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using SmartBank.Core.Common;
using SmartBank.Core.Entities;
using SmartBank.Infrastructure.Data;

namespace SmartBank.Infrastructure.Services
{
    public partial class BankingService
    {
        public Task<ServiceResult<bool>> DeleteAccountAsync(Guid userId, Guid accountId, Guid? transferTargetAccountId = null) =>
            RunWithConcurrencyRetryAsync(() => DeleteAccountCoreAsync(userId, accountId, transferTargetAccountId));

        /// <summary>
        /// Closes an account. A remaining balance is moved to another account of the same customer first (converted at live
        /// rates when the currencies differ). Everything happens in ONE database transaction: the closing ledger row, the
        /// credit, detaching the account from the history of every transaction (the rows stay, they just lose the link),
        /// switching off standing orders and removing saved recipients that point at the account, the delete itself and the
        /// audit entry. Either all of it happens or none.
        /// </summary>
        private async Task<ServiceResult<bool>> DeleteAccountCoreAsync(Guid userId, Guid accountId, Guid? transferTargetAccountId)
        {
            var account = await _context.Accounts.FirstOrDefaultAsync(a => a.Id == accountId && a.UserId == userId);
            if (account == null)
            {
                return Fail<bool>("AccountNotFound", "Hesap bulunamadı.");
            }

            // The customer's row is read BEFORE the accounts are counted, and its version is checked when this close is saved.
            // Read after the count (as it used to be), a request could count two accounts, then find the other close already
            // committed, read the row at its new version and close the second account too: the customer ended up with none.
            // Read first, a close that commits in between changes the version this one saved against and this one starts over.
            var owner = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);

            if (await _context.Accounts.CountAsync(a => a.UserId == userId) <= 1)
            {
                return Fail<bool>("CannotDeleteLastAccount", "Daima en az bir aktif hesabınız bulunmalıdır.");
            }

            Account? target = null;
            var credited = 0m;

            if (account.Balance > 0m)
            {
                if (transferTargetAccountId == null || transferTargetAccountId == Guid.Empty)
                {
                    return Fail<bool>("TargetAccountRequired", "Hesapta bakiye bulunmaktadır. Silmeden önce bakiyenizi aktarmak istediğiniz hesabı seçmelisiniz.");
                }

                target = await _context.Accounts.FirstOrDefaultAsync(a => a.Id == transferTargetAccountId && a.UserId == userId && a.Id != accountId);
                if (target == null)
                {
                    return Fail<bool>("TargetAccountNotFound", "Hedef hesap bulunamadı.");
                }

                var (converted, errorKey) = await ConvertAsync(account.Balance, account.Currency, target.Currency);
                if (converted == null)
                {
                    return Fail<bool>(errorKey ?? "RateUnavailable", "Güncel kur bilgisine şu anda ulaşılamıyor. Hesabı kapatmak için lütfen biraz sonra tekrar deneyin.");
                }

                if (converted <= 0m)
                {
                    return Fail<bool>("AmountTooSmall", "Hesaptaki bakiye hedef hesaba aktarılamayacak kadar küçük.");
                }

                credited = converted.Value;
            }

            var accountNumber = account.AccountNumber;
            var bulk = _context.SupportsBulkOperations();

            await using var dbTransaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // "A customer keeps at least one account" is checked by counting, and two parallel closes of two different accounts
                // would both count two. The customer's own row (read above, before the count) is bumped in the same transaction, so the
                // second of two simultaneous closes collides on it (a version conflict, retried from fresh reads, where the count is right).
                if (owner != null)
                {
                    _context.Entry(owner).Property(u => u.Version).IsModified = true;
                }

                if (target != null)
                {
                    target.Balance += credited;

                    var closing = new Transaction
                    {
                        SourceAccountId = account.Id,
                        DestinationAccountId = target.Id,
                        Amount = credited, // what the receiving account was credited, in its own currency
                        Description = Text($"Hesap Kapatma Bakiye Aktarımı ({account.Currency} -> {target.Currency}): {Money.Format(account.Balance)} {account.Currency}"),
                        Type = TransactionType.Transfer,
                        Category = TransactionCategories.Investment,
                        CreatedAt = UtcNow
                    };
                    _context.Transactions.Add(closing);

                    // The credit and the closing row first. Only then are the links to the account removed from every row
                    // (including this one), because a transaction that still points at the account blocks its deletion.
                    await _context.SaveChangesAsync();
                }

                var closingAccountId = account.Id;
                if (bulk)
                {
                    await _context.Transactions.Where(t => t.SourceAccountId == closingAccountId)
                        .ExecuteUpdateAsync(s => s.SetProperty(t => t.SourceAccountId, (Guid?)null));
                    await _context.Transactions.Where(t => t.DestinationAccountId == closingAccountId)
                        .ExecuteUpdateAsync(s => s.SetProperty(t => t.DestinationAccountId, (Guid?)null));
                    await _context.StandingOrders.Where(o => o.IsActive && (o.SourceAccountNumber == accountNumber || o.DestinationAccountNumber == accountNumber))
                        .ExecuteUpdateAsync(s => s.SetProperty(o => o.IsActive, false));
                    await _context.SavedContacts.Where(c => c.AccountNumber == accountNumber).ExecuteDeleteAsync();

                    // The bulk statements changed rows behind the tracker's back: forget the transaction rows it still holds.
                    foreach (var entry in _context.ChangeTracker.Entries<Transaction>().ToList())
                    {
                        entry.State = EntityState.Detached;
                    }
                }
                else
                {
                    // The in-memory provider used by unit tests has no bulk updates: the same changes, one tracked row at a time.
                    foreach (var t in await _context.Transactions.Where(t => t.SourceAccountId == closingAccountId).ToListAsync()) t.SourceAccountId = null;
                    foreach (var t in await _context.Transactions.Where(t => t.DestinationAccountId == closingAccountId).ToListAsync()) t.DestinationAccountId = null;
                    foreach (var o in await _context.StandingOrders
                                 .Where(o => o.IsActive && (o.SourceAccountNumber == accountNumber || o.DestinationAccountNumber == accountNumber)).ToListAsync())
                    {
                        o.IsActive = false;
                    }

                    _context.SavedContacts.RemoveRange(await _context.SavedContacts.Where(c => c.AccountNumber == accountNumber).ToListAsync());
                }

                _context.Accounts.Remove(account);
                _context.AuditLogs.Add(NewAudit(userId, "DeleteAccount",
                    Text($"Deleted account ID: {accountId}. AccountNumber: {accountNumber}. Moved {Money.Format(credited)} {target?.Currency ?? account.Currency} to {target?.AccountNumber ?? "-"}.")));

                await _context.SaveChangesAsync();
                await dbTransaction.CommitAsync();

                return ServiceResult<bool>.Success(true);
            }
            catch (Exception ex) when (DatabaseConflict.IsRetryable(ex))
            {
                await dbTransaction.RollbackAsync();
                throw;
            }
            catch (Exception ex)
            {
                await dbTransaction.RollbackAsync();
                _logger.LogError(ex, "Closing account {AccountId} failed.", accountId);
                return Fail<bool>("AccountCloseFailed", "Hesap kapatılamadı. Lütfen tekrar deneyin.");
            }
        }
    }
}
