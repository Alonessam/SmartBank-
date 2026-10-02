using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartBank.Core.Entities;
using SmartBank.Infrastructure.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SmartBank.Infrastructure.BackgroundServices
{
    public class StandingOrderExecutionWorker : BackgroundService
    {
        // The UI and BankingService create automatic card payments as "CreditCardAutoPay"; "CreditCardDebt" is the
        // name this worker used to look for. Both are accepted so existing rows keep working.
        private static readonly string[] CreditCardOrderTypes = { "CreditCardAutoPay", "CreditCardDebt" };

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<StandingOrderExecutionWorker> _logger;
        private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

        public StandingOrderExecutionWorker(IServiceScopeFactory scopeFactory, ILogger<StandingOrderExecutionWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Standing Order Execution Worker is starting.");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "An error occurred while executing standing orders.");
                }

                await Task.Delay(CheckInterval, stoppingToken);
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
                var now = DateTime.UtcNow;
                dueOrderIds = await db.StandingOrders
                    .Where(o => o.IsActive && o.NextExecutionDate <= now)
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

        // Every order gets its own scope and its own DbContext. A failure or a collision with another request on one
        // order therefore cannot leave stale or half-modified entities behind for the next order.
        private async Task ProcessOrderAsync(Guid orderId, CancellationToken stoppingToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SmartBankDbContext>();

            // Re-read: it may already have been executed (by another worker instance) or removed since the list was built.
            var order = await db.StandingOrders.FirstOrDefaultAsync(o => o.Id == orderId, stoppingToken);
            if (order == null || !order.IsActive || order.NextExecutionDate > DateTime.UtcNow) return;

            using var transaction = await db.Database.BeginTransactionAsync(stoppingToken);
            try
            {
                // 1. Source Account check
                var sourceAcc = await db.Accounts.FirstOrDefaultAsync(a => a.AccountNumber == order.SourceAccountNumber, stoppingToken);
                if (sourceAcc == null)
                {
                    throw new Exception($"Source account {order.SourceAccountNumber} not found.");
                }

                decimal executionAmount = 0;
                CreditCard? associatedCard = null;
                var isCreditCardOrder = CreditCardOrderTypes.Contains(order.OrderType);

                // 2. If it's a Credit Card Debt Payment
                if (isCreditCardOrder && order.CreditCardId.HasValue)
                {
                    associatedCard = await db.CreditCards.FirstOrDefaultAsync(c => c.Id == order.CreditCardId.Value, stoppingToken);
                    if (associatedCard == null)
                    {
                        throw new Exception($"Credit card not found for ID: {order.CreditCardId}");
                    }

                    // Calculate statement debt using PeriodDebt
                    var unpaidStatements = await db.CreditCardStatements
                        .Where(s => s.CreditCardId == associatedCard.Id && !s.IsPaid)
                        .ToListAsync(stoppingToken);

                    executionAmount = unpaidStatements.Sum(s => s.PeriodDebt - s.PaidAmount);

                    if (executionAmount <= 0)
                    {
                        // No debt to pay, shift to next period
                        ShiftNextExecutionDate(order);
                        await db.SaveChangesAsync(stoppingToken);
                        await transaction.CommitAsync(stoppingToken);
                        return;
                    }
                }
                else
                {
                    executionAmount = order.Amount ?? 0;
                }

                if (executionAmount <= 0)
                {
                    throw new Exception("Invalid transfer amount.");
                }

                // 3. Balance Check
                if (sourceAcc.Balance < executionAmount)
                {
                    throw new Exception($"Insufficient balance in source account. Needed: {executionAmount}, Available: {sourceAcc.Balance}");
                }

                // 4. Perform Transfer
                sourceAcc.Balance -= executionAmount;

                Account? destinationAcc = null;
                if (!string.IsNullOrEmpty(order.DestinationAccountNumber))
                {
                    destinationAcc = await db.Accounts.FirstOrDefaultAsync(a => a.AccountNumber == order.DestinationAccountNumber, stoppingToken);
                }

                // Add outgoing transaction
                var outTx = new Transaction
                {
                    Id = Guid.NewGuid(),
                    SourceAccountId = sourceAcc.Id,
                    DestinationAccountId = destinationAcc?.Id,
                    Amount = executionAmount,
                    Type = TransactionType.Transfer,
                    Description = $"Otomatik Talimat: {order.OrderType} Ödemesi",
                    Category = "Fatura",
                    CreatedAt = DateTime.UtcNow
                };
                db.Transactions.Add(outTx);

                if (isCreditCardOrder && associatedCard != null)
                {
                    associatedCard.CurrentDebt = Math.Max(0, associatedCard.CurrentDebt - executionAmount); // Reduce debt
                    var statements = await db.CreditCardStatements
                        .Where(s => s.CreditCardId == associatedCard.Id && !s.IsPaid)
                        .ToListAsync(stoppingToken);
                    foreach (var stmt in statements)
                    {
                        stmt.PaidAmount += stmt.PeriodDebt - stmt.PaidAmount; // Mark as paid fully
                        stmt.IsPaid = true;
                    }
                }
                else if (destinationAcc != null)
                {
                    destinationAcc.Balance += executionAmount;
                    // Add incoming transaction
                    var inTx = new Transaction
                    {
                        Id = Guid.NewGuid(),
                        SourceAccountId = sourceAcc.Id,
                        DestinationAccountId = destinationAcc.Id,
                        Amount = executionAmount,
                        Type = TransactionType.Deposit,
                        Description = $"Gelen Otomatik Talimat Ödemesi",
                        Category = "Yatırım",
                        CreatedAt = DateTime.UtcNow
                    };
                    db.Transactions.Add(inTx);
                }

                // 5. Shift Dates. NextExecutionDate is the order's concurrency token: if another worker instance (or a
                // retry of this one) already ran this order, the row's date has moved and this save is rejected, so the
                // order cannot be executed twice.
                ShiftNextExecutionDate(order);

                // Write Audit Log
                var audit = new AuditLog
                {
                    Id = Guid.NewGuid(),
                    UserId = order.UserId,
                    Action = "StandingOrderExecuted",
                    Details = $"Executed standing order ID: {order.Id}. Amount: {executionAmount} TRY. Type: {order.OrderType}",
                    IpAddress = "system:standing-order-worker",
                    CreatedAt = DateTime.UtcNow
                };
                db.AuditLogs.Add(audit);

                await db.SaveChangesAsync(stoppingToken);
                await transaction.CommitAsync(stoppingToken);
                _logger.LogInformation("Successfully executed standing order ID: {Id}", order.Id);
            }
            catch (Exception ex) when (DatabaseConflict.IsRetryable(ex))
            {
                // Someone else touched the same rows (a customer transfer, another worker) or this transaction was a
                // deadlock victim. That is not the order's fault: it stays active and is picked up again next cycle.
                await transaction.RollbackAsync(CancellationToken.None);
                _logger.LogInformation("Standing order {Id} collided with another change and stays active; it will run on the next cycle.", orderId);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                _logger.LogWarning("Failed to execute standing order ID {Id}: {Error}. Deactivating order.", orderId, ex.Message);

                // Deactivate order to prevent infinite retry loops. Discard everything the context still tracks first:
                // balances and transactions modified in memory before the failure must not be saved by accident.
                db.ChangeTracker.Clear();
                await db.StandingOrders
                    .Where(o => o.Id == orderId)
                    .ExecuteUpdateAsync(s => s.SetProperty(o => o.IsActive, false), CancellationToken.None);
            }
        }

        private void ShiftNextExecutionDate(StandingOrder order)
        {
            order.NextExecutionDate = order.Frequency switch
            {
                "Daily" => order.NextExecutionDate.AddDays(1),
                "Weekly" => order.NextExecutionDate.AddDays(7),
                "Monthly" => order.NextExecutionDate.AddMonths(1),
                _ => order.NextExecutionDate.AddMonths(1)
            };
        }
    }
}
