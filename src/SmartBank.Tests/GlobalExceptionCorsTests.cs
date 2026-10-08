using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartBank.API.Middlewares;

namespace SmartBank.Tests
{
    /// <summary>
    /// The CORS middleware adds its headers before the endpoint runs. The exception middleware wraps it from the outside and
    /// used to call Response.Clear(), which dropped them: the browser then showed a server error as a CORS failure.
    /// </summary>
    public class GlobalExceptionCorsTests
    {
        private static async Task<DefaultHttpContext> RunAsync(Action<HttpContext> beforeThrowing)
        {
            var env = new Mock<IHostEnvironment>();
            env.SetupGet(e => e.EnvironmentName).Returns(Environments.Production);

            var middleware = new GlobalExceptionMiddleware(
                context =>
                {
                    beforeThrowing(context);
                    throw new InvalidOperationException("boom");
                },
                NullLogger<GlobalExceptionMiddleware>.Instance,
                env.Object);

            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();
            await middleware.InvokeAsync(context);
            return context;
        }

        [Fact]
        public async Task A_server_error_still_carries_the_cors_headers_added_earlier_in_the_pipeline()
        {
            var context = await RunAsync(c =>
            {
                c.Response.Headers["Access-Control-Allow-Origin"] = "https://alonessam.github.io";
                c.Response.Headers["Access-Control-Allow-Credentials"] = "true";
                c.Response.Headers["Vary"] = "Origin";
            });

            Assert.Equal(500, context.Response.StatusCode);
            Assert.Equal("https://alonessam.github.io", context.Response.Headers["Access-Control-Allow-Origin"].ToString());
            Assert.Equal("true", context.Response.Headers["Access-Control-Allow-Credentials"].ToString());
            Assert.Equal("Origin", context.Response.Headers["Vary"].ToString());
        }

        [Fact]
        public async Task Other_headers_set_by_the_failed_endpoint_are_still_discarded()
        {
            var context = await RunAsync(c =>
            {
                c.Response.Headers["Access-Control-Allow-Origin"] = "https://alonessam.github.io";
                c.Response.Headers["X-Half-Built"] = "should disappear";
                c.Response.Headers["Cache-Control"] = "public, max-age=600";
            });

            Assert.False(context.Response.Headers.ContainsKey("X-Half-Built"));
            Assert.False(context.Response.Headers.ContainsKey("Cache-Control"));
            Assert.True(context.Response.Headers.ContainsKey("Access-Control-Allow-Origin"));
            Assert.Equal("application/problem+json", context.Response.ContentType);
        }

        [Fact]
        public async Task Without_cors_headers_nothing_extra_appears()
        {
            var context = await RunAsync(_ => { });

            Assert.False(context.Response.Headers.ContainsKey("Access-Control-Allow-Origin"));
            Assert.Equal(500, context.Response.StatusCode);
        }
    }
}
