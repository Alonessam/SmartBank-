using System.Text.Json;

namespace SmartBank.API.Middlewares
{
    public class GlobalExceptionMiddleware
    {
        /// <summary>The status nginx and others use when the client closed the connection before the answer was ready.</summary>
        public const int ClientClosedRequest = 499;

        private readonly RequestDelegate _next;
        private readonly ILogger<GlobalExceptionMiddleware> _logger;
        private readonly IHostEnvironment _env;

        public GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger, IHostEnvironment env)
        {
            _next = next;
            _logger = logger;
            _env = env;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                await _next(context);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // The client went away (closed the tab, timed out): nothing is wrong on our side and nobody is listening.
                _logger.LogDebug("Request {TraceId} was cancelled by the client.", context.TraceIdentifier);
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = ClientClosedRequest;
                }
            }
            catch (BadHttpRequestException bad)
            {
                // The client sent something the server refuses to read (body too large, malformed framing): its own status
                // (400, 413, 408...), not a 500 and not an error in the log.
                _logger.LogWarning("Request {TraceId} was refused: {Reason}", context.TraceIdentifier, bad.Message);
                if (context.Response.HasStarted)
                {
                    context.Abort();
                    return;
                }

                context.Response.StatusCode = bad.StatusCode;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"isSuccess\":false,\"errorKey\":\"BadRequest\",\"message\":\"The request could not be read.\"}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception for request {TraceId}.", context.TraceIdentifier);

                if (context.Response.HasStarted)
                {
                    // Part of an answer is already on its way: a status and a body can no longer be written. End the connection
                    // instead of corrupting the stream (or throwing a second exception from here).
                    context.Abort();
                    return;
                }

                await HandleExceptionAsync(context, ex);
            }
        }

        private async Task HandleExceptionAsync(HttpContext context, Exception exception)
        {
            context.Response.Clear();
            context.Response.ContentType = "application/problem+json";
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;

            var response = new
            {
                type = "https://tools.ietf.org/html/rfc7231#section-6.6.1",
                title = "An unexpected error occurred on the server.",
                status = StatusCodes.Status500InternalServerError,
                // The full exception (type, message, stack, SQL text) only on a developer machine. Everywhere else the
                // client gets a generic message plus an id to quote, and the details stay in the server log.
                detail = _env.IsDevelopment() ? exception.ToString() : "An internal error occurred. Please try again later.",
                instance = context.Request.Path.Value,
                traceId = context.TraceIdentifier
            };

            var json = JsonSerializer.Serialize(response, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            await context.Response.WriteAsync(json);
        }
    }
}
