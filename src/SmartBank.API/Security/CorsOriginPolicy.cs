namespace SmartBank.API.Security
{
    /// <summary>
    /// Which browser origins may call the API. The old policy accepted every origin together with credentials, so any
    /// website a signed-in customer visited could have called the API with the customer's token. Now only the origins
    /// listed in configuration (Cors:AllowedOrigins) are accepted. In Development, local pages are accepted too, so
    /// opening index.html straight from disk (origin "null") or a local web server keeps working.
    /// </summary>
    public static class CorsOriginPolicy
    {
        public static bool IsAllowed(string? origin, IEnumerable<string> allowedOrigins, bool isDevelopment)
        {
            if (string.IsNullOrWhiteSpace(origin))
            {
                return false;
            }

            if (allowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }

            return isDevelopment && IsLocalDevelopmentOrigin(origin);
        }

        private static bool IsLocalDevelopmentOrigin(string origin)
        {
            // A page opened from disk (file://) sends the literal origin "null". Accepting it anywhere but on a
            // developer machine would be a hole: sandboxed iframes and data: URLs send it as well.
            if (origin == "null")
            {
                return true;
            }

            return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                   && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                   && (uri.Host == "localhost" || uri.Host == "127.0.0.1" || uri.Host == "[::1]");
        }
    }
}
