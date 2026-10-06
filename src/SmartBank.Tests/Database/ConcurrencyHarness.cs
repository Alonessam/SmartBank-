using Moq;
using SmartBank.Core.Interfaces;
using SmartBank.Infrastructure.Services;

namespace SmartBank.Tests.Database
{
    /// <summary>Helpers shared by the real-database concurrency tests.</summary>
    public static class ConcurrencyHarness
    {
        /// <summary>A new context and service, like the ones a single web request gets.</summary>
        public static Task<T> InNewRequestAsync<T>(TestDb db, Func<BankingService, Task<T>> action) =>
            InNewRequestAsync(db, new Mock<IMarketRateService>().Object, new FakeOtpDelivery(), action);

        /// <summary>The same, with the market rates (and the one-time-code delivery) the test chooses.</summary>
        public static async Task<T> InNewRequestAsync<T>(TestDb db, IMarketRateService rates, FakeOtpDelivery otp, Func<BankingService, Task<T>> action)
        {
            await using var context = db.NewContext();
            var service = new BankingService(context, otp, rates);
            return await action(service);
        }

        /// <summary>Starts every operation on its own thread and releases them all at the same moment.</summary>
        public static async Task<T[]> RunTogetherAsync<T>(IEnumerable<Func<Task<T>>> operations)
        {
            var gate = new TaskCompletionSource();
            var tasks = operations.Select(operation => Task.Run(async () =>
            {
                await gate.Task;
                return await operation();
            })).ToList();

            gate.SetResult();
            return await Task.WhenAll(tasks);
        }
    }
}
