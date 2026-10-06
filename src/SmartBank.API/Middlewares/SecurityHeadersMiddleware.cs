namespace SmartBank.API.Middlewares
{
    /// <summary>
    /// Adds the response headers that tell browsers to treat the API as what it is: JSON for another site's script, never a
    /// page. (The web app's own headers cannot be set this way: GitHub Pages does not allow custom headers, which is why its
    /// pages carry a CSP meta tag instead.)
    /// </summary>
    public sealed class SecurityHeadersMiddleware
    {
        private readonly RequestDelegate _next;

        public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

        public Task InvokeAsync(HttpContext context)
        {
            // OnStarting runs right before the headers are sent, so error responses written further down get them too.
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;

                // Do not let a browser guess that JSON is HTML or a script.
                headers["X-Content-Type-Options"] = "nosniff";
                // The API is never meant to be shown inside a frame or load anything: it only answers fetch calls.
                headers["X-Frame-Options"] = "DENY";
                headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
                // A URL on this API (an account number, a session id) should not leak through the Referer header.
                headers["Referrer-Policy"] = "no-referrer";

                // Balances and transactions must not be kept by a browser or an intermediary cache.
                if (!headers.ContainsKey("Cache-Control") &&
                    (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/hubs")))
                {
                    headers["Cache-Control"] = "no-store";
                }

                return Task.CompletedTask;
            });

            return _next(context);
        }
    }
}
