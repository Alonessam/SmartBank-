namespace SmartBank.API.Security
{
    /// <summary>Names of the rate-limit policies (see Program.cs). Per signed-in user where there is one, otherwise per client address.</summary>
    public static class RateLimitPolicies
    {
        /// <summary>Login, 2FA, password reset, register: per address.</summary>
        public const string Auth = "auth";

        /// <summary>Silent token refresh and logout: per address, wider window.</summary>
        public const string Refresh = "refresh";

        /// <summary>Every banking endpoint: 60 a minute by default.</summary>
        public const string Banking = "banking";

        /// <summary>Money-moving endpoints (transfer, exchange, deposit, card payment): 10 a minute by default. Replaces "banking" on those actions.</summary>
        public const string Transfer = "transfer";

        /// <summary>The support-chat HTTP endpoints (history, agent dashboard, AI suggestion): per user, 60 a minute by default.</summary>
        public const string Chat = "chat";

        /// <summary>The public rates list: per address, 60 a minute by default.</summary>
        public const string Market = "market";
    }
}
