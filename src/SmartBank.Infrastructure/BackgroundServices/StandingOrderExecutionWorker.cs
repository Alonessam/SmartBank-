using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartBank.Core.Common;
using SmartBank.Core.Entities;
using SmartBank.Infrastructure.Data;

namespace SmartBank.Infrastructure.BackgroundServices
{
    /// <summary>
    /// Runs the standing orders that are due, every 30 seconds. Each order gets its own scope, DbContext and database
    /// transaction. Failures are sorted into two kinds:
    ///  - permanent (the source or destination is gone, the currencies differ, the balance is too low, the order has
    ///    reached its maturity date): the order is switched off and an audit entry says why;
    ///  - everything else (the database is unreachable, a timeout, a collision with a customer's own transfer): nothing is
    ///    changed and the order is simply tried again in the next cycle.
    /// Money is never taken from the source before the destination has been found and checked.
    /// </summary>
    public class StandingOrderExecutionWorker : BackgroundService
    {
        private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

        /// <summary>A reason an order can never succeed as it stands. Not an infrastructure failure: the order is deactivated.</summary>
        private sealed class OrderRejectedException : Exception
        {
            public string Reason { get; }
            public OrderRejectedException(string reason) : base(reason) => Reason = reason;
        }

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<StandingOrderExecutionWorker> _logger;
        private readonly TimeProvider _time;

        public StandingOrderExecutionWorker(IServiceScopeFactory scopeFactory, ILogger<StandingOrderExecutionWorker> logger, TimeProvider? timeProvider = null)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _time = timeProvider ?? TimeProvider.System;
        }

        private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Standing Order Execution Worker is starting.");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "An error occurred while executing standing orders.");
                }

                try
                {
                    await Task.Delay(CheckInterval, _time, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            _logger.LogInformation("Standing Order Execution Worker is stopping.");
        }

        /// <summary>One pass over all due orders. Internal so tests can run a pass without waiting for the timer.</summary>
        internal async Task RunOnceAsync(CancellationToken stoppingToken)
        {
            List<Guid> dueOrderIds;
            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SmartBankDbContext>();
                var now = UtcNow;
                dueOrderIds = await db.StandingOrders
                    .AsNoTracking()
                    .Where(o => o.IsActive && o.NextExecutionDate <= now)
                    .OrderBy(o => o.NextExecutionDate)
                    .Select(o => o.Id)
                    .ToListAsync(stoppingToken);
            }

            if (dueOrderIds.Count == 0) return;

            _logger.LogInformation("Found {Count} pending standing orders to execute.", dueOrderIds.Count);

            foreach (var orderId in dueOrderIds)
            {
                if (stoppingToken.IsCancellationRequested) break;
                await ProcessOrderAsync(orderId, stoppingToken);
            }
        }

        private async Task ProcessOrderAsync(Guid orderId, CancellationToken stoppingToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SmartBankDbContext>();

            // Re-read: it may already have been executed (by another worker instance) or removed since the list was built.
            var order = await db.StandingOrders.FirstOrDefaultAsync(o => o.Id == orderId, stoppingToken);
            var now = UtcNow;
            if (order == null || !order.IsActive || order.NextExecutionDate > now) return;

            await using var transaction = await db.Database.BeginTransactionAsync(stoppingToken);
            try
            {
                await RunOrderAsync(db, order, now, stoppingToken);
                await transaction.CommitAsync(stoppingToken);
                _logger.LogInformation("Successfully executed standing order ID: {Id}", order.Id);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                await RollbackQuietlyAsync(transaction);
            }
            catch (OrderRejectedException rejected)
            {
                await RollbackQuietlyAsync(transaction);
                _logger.LogWarning("Standing order {Id} cannot be executed ({Reason}). Deactivating it.", orderId, rejected.Reason);
                await DeactivateAsync(db, orderId, rejected.Reason);
            }
            catch (Exception ex) when (DatabaseConflict.IsRetryable(ex))
            {
                // Someone else touched the same rows (a customer transfer, another worker) or this transaction was a
                // deadlock victim. That is not the order's fault: it stays active and is picked up again next cycle.
                await RollbackQuietlyAsync(transaction);
                _logger.LogInformation("Standing order {Id} collided with another change and stays active; it will run on the next cycle.", orderId);
            }
            catch (Exception ex)
            {
                // Not the order's fault either (the database was unreachable, a timeout, a bug): keep it active, try again later.
                await RollbackQuietlyAsync(transaction);
                _logger.LogError(ex, "Standing order {Id} failed unexpectedly and stays active; it will be tried again on the next cycle.", orderId);
            }
        }

        private async Task RunOrderAsync(SmartBankDbContext db, StandingOrder order, DateTime now, CancellationToken ct)
        {
            // An order is valid for a limited time (one year from its creation).
            if (now > order.MaturityDate)
            {
                throw new OrderRejectedException("the order has reached its maturity date");
            }

            // 1. The source must exist and still belong to the customer who set the order up.
            var source = await db.Accounts.FirstOrDefaultAsync(a => a.AccountNumber == order.SourceAccountNumber, ct);
            if (source == null)
            {
                throw new OrderRejectedException("the source account no longer exists");
            }

            if (source.UserId != order.UserId)
            {
                throw new OrderRejectedException("the source account does not belong to the customer who set the order up");
            }

            Account? destination = null;
            CreditCard? card = null;
            List<CreditCardStatement> openStatements = new();
            decimal amount;

            if (OrderTypes.IsCreditCard(order.OrderType) && order.CreditCardId.HasValue)
            {
                // 2a. Pay the open statements of a card of the same customer, from a TRY account.
                card = await db.CreditCards.FirstOrDefaultAsync(c => c.Id == order.CreditCardId.Value && c.UserId == order.UserId, ct);
                if (card == null)
                {
                    throw new OrderRejectedException("the credit card no longer exists or does not belong to the customer");
                }

                if (source.Currency != Currencies.Try)
                {
                    throw new OrderRejectedException("only TRY accounts can pay credit card debt");
                }

                openStatements = await db.CreditCardStatements.Where(s => s.CreditCardId == card.Id && !s.IsPaid).ToListAsync(ct);
                amount = openStatements.Sum(s => s.PeriodDebt - s.PaidAmount);

                if (amount <= 0m)
                {
                    // Nothing to pay this time: just move to the next period.
                    ShiftNextExecutionDate(order);
                    await db.SaveChangesAsync(ct);
                    return;
                }
            }
            else
            {
                // 2b. A transfer: the destination is looked up and checked BEFORE any balance is touched.
                amount = order.Amount ?? 0m;
                if (amount <= 0m || !Money.HasValidScale(amount))
                {
                    throw new OrderRejectedException("the amount of the order is not valid");
                }

                if (string.IsNullOrEmpty(order.DestinationAccountNumber))
                {
                    throw new OrderRejectedException("the order has no destination account");
                }

                destination = await db.Accounts.FirstOrDefaultAsync(a => a.AccountNumber == order.DestinationAccountNumber, ct);
                if (destination == null)
                {
                    throw new OrderRejectedException("the destination account does not exist");
                }

                if (destination.Id == source.Id)
                {
                    throw new OrderRejectedException("the source and destination are the same account");
                }

                if (destination.Currency != source.Currency)
                {
                    throw new OrderRejectedException("the accounts are in different currencies");
                }
            }

            // 3. Balance check.
            if (source.Balance < amount)
            {
                throw new OrderRejectedException("the balance of the source account is too low");
            }

            // 4. Perform the transfer: ONE ledger row per movement.
            source.Balance -= amount;

            if (card != null)
            {
                card.CurrentDebt = Math.Max(0m, card.CurrentDebt - amount);
                foreach (var statement in openStatements)
                {
                    statement.PaidAmount = statement.PeriodDebt;
                    statement.IsPaid = true;
                }

                db.Transactions.Add(new Transaction
                {
                    Id = Guid.NewGuid(),
                    SourceAccountId = source.Id,
                    DestinationAccountId = null,
                    Amount = amount,
                    Type = TransactionType.Withdrawal,
                    Description = $"Otomatik Talimat: {order.OrderType} Ödemesi",
                    Category = TransactionCategories.Bills,
                    CreatedAt = now
                });
            }
            else
            {
                destination!.Balance += amount;

                db.Transactions.Add(new Transaction
                {
                    Id = Guid.NewGuid(),
                    SourceAccountId = source.Id,
                    DestinationAccountId = destination.Id,
                    Amount = amount,
                    Type = TransactionType.Transfer,
                    Description = $"Otomatik Talimat: {order.OrderType} Ödemesi",
                    Category = TransactionCategories.Bills,
                    CreatedAt = now
                });
            }

            // 5. Shift dates. NextExecutionDate is the order's concurrency token: if another worker instance (or a
            // retry of this one) already ran this order, the row's date has moved and this save is rejected, so the
            // order cannot be executed twice.
            ShiftNextExecutionDate(order);

            db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                UserId = order.UserId,
                Action = "StandingOrderExecuted",
                Details = string.Create(CultureInfo.InvariantCulture, $"Executed standing order ID: {order.Id}. Amount: {Money.Format(amount)} {source.Currency}. Type: {order.OrderType}"),
                IpAddress = "system:standing-order-worker",
                CreatedAt = now
            });

            await db.SaveChangesAsync(ct);
        }

        /// <summary>
        /// Switches an order off and says why in the audit trail. Starts from a clean context, so balances changed in memory
        /// before the failure cannot be saved by accident. If even this fails, the order is simply picked up (and rejected) again.
        /// </summary>
        private async Task DeactivateAsync(SmartBankDbContext db, Guid orderId, string reason)
        {
            try
            {
                db.ChangeTracker.Clear();
                var order = await db.StandingOrders.FirstOrDefaultAsync(o => o.Id == orderId, CancellationToken.None);
                if (order == null || !order.IsActive) return;

                order.IsActive = false;
                db.AuditLogs.Add(new AuditLog
                {
                    Id = Guid.NewGuid(),
                    UserId = order.UserId,
                    Action = "StandingOrderDeactivated",
                    Details = $"Standing order {order.Id} was switched off: {reason}.",
                    IpAddress = "system:standing-order-worker",
                    CreatedAt = UtcNow
                });
                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Standing order {Id} could not be deactivated; it will be examined again on the next cycle.", orderId);
            }
        }

        private async Task RollbackQuietlyAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction)
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Rolling back a standing-order transaction failed.");
            }
        }

        private static void ShiftNextExecutionDate(StandingOrder order)
        {
            order.NextExecutionDate = order.Frequency switch
            {
                Frequencies.Daily => order.NextExecutionDate.AddDays(1),
                Frequencies.Weekly => order.NextExecutionDate.AddDays(7),
                _ => order.NextExecutionDate.AddMonths(1)
            };
        }
    }
}
