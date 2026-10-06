namespace SmartBank.API.Security
{
    /// <summary>Limits for the support chat, read from configuration (section "Chat") with safe defaults.</summary>
    public sealed class ChatSettings
    {
        public const string SectionName = "Chat";

        public int MessagesPerMinute { get; init; } = 10;
        public int MessagesPerHour { get; init; } = 100;
        public int SessionsPerHour { get; init; } = 10;
        public int TransfersPerMinute { get; init; } = 5;

        /// <summary>Support agents answer many customers, so their per-minute allowance is a multiple of a customer's.</summary>
        public const int AgentMultiplier = 3;

        public static ChatSettings From(IConfiguration configuration) => new()
        {
            MessagesPerMinute = Read(configuration, nameof(MessagesPerMinute), 10),
            MessagesPerHour = Read(configuration, nameof(MessagesPerHour), 100),
            SessionsPerHour = Read(configuration, nameof(SessionsPerHour), 10),
            TransfersPerMinute = Read(configuration, nameof(TransfersPerMinute), 5)
        };

        private static int Read(IConfiguration configuration, string name, int fallback)
        {
            var raw = configuration[$"{SectionName}:{name}"];
            if (string.IsNullOrWhiteSpace(raw)) return fallback;

            if (!int.TryParse(raw, out var value) || value < 1 || value > 100000)
            {
                throw new InvalidOperationException($"{SectionName}:{name} must be a whole number between 1 and 100000.");
            }

            return value;
        }
    }
}
