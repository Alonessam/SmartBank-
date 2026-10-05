using SmartBank.Core.Interfaces;

namespace SmartBank.API.Services
{
    /// <summary>
    /// Reads the caller's address from the current request. Behind a reverse proxy it is the real client only if
    /// forwarded headers are enabled (the Dockerfile does that); otherwise it would be the proxy's address.
    /// </summary>
    public sealed class HttpContextClientInfo : IClientInfo
    {
        private readonly IHttpContextAccessor _accessor;

        public HttpContextClientInfo(IHttpContextAccessor accessor) => _accessor = accessor;

        public string IpAddress => _accessor.HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
