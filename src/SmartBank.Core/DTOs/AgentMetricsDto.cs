namespace SmartBank.Core.DTOs
{
    public class AgentMetricsDto
    {
        public int ResolvedCount { get; set; }
        public string AvgResponseTime { get; set; } = "12s";
        public string CsatScore { get; set; } = "95%";
    }
}
