using Microsoft.AspNetCore.SignalR;

namespace SmartBank.API.Hubs
{
    /// <summary>
    /// A SignalR connection is authenticated once, when it opens. Without this filter it would keep working after the access
    /// token has expired (15 minutes), after the user has signed out elsewhere or been locked. Every hub call is therefore
    /// checked against the token's "exp" claim: after that moment the call fails with a <see cref="HubException"/> saying
    /// "Session expired", and the web app reconnects with a fresh token (it refreshes tokens anyway).
    /// </summary>
    public sealed class HubTokenExpiryFilter : IHubFilter
    {
        public const string ExpiredMessage = "Session expired";

        private readonly TimeProvider _time;

        public HubTokenExpiryFilter(TimeProvider time) => _time = time;

        public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
        {
            var exp = invocationContext.Context.User?.FindFirst("exp")?.Value;
            if (long.TryParse(exp, out var seconds) && DateTimeOffset.FromUnixTimeSeconds(seconds) <= _time.GetUtcNow())
            {
                throw new HubException(ExpiredMessage);
            }

            return await next(invocationContext);
        }
    }
}
