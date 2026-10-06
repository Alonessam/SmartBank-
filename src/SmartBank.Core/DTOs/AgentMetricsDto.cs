namespace SmartBank.Core.DTOs
{
    public class AgentMetricsDto
    {
        public int ResolvedCount { get; set; }

        /// <summary>Mean time from a customer message to the next agent message, like "42s". Null when no message has been answered yet.</summary>
        public string? AvgResponseTime { get; set; }

        /// <summary>Customer satisfaction. There is no data source for it, so it is always null (the field is kept for the dashboard).</summary>
        public string? CsatScore { get; set; }
    }
}
