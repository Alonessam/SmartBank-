namespace SmartBank.Core.Interfaces
{
    /// <summary>Who is calling: lets services write the real client address into the audit trail.</summary>
    public interface IClientInfo
    {
        /// <summary>The caller's IP address, or "unknown" when there is no HTTP request (e.g. a background job).</summary>
        string IpAddress { get; }
    }
}
