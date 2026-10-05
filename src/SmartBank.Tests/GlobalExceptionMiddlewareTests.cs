using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartBank.API.Middlewares;

namespace SmartBank.Tests
{
    public class GlobalExceptionMiddlewareTests
    {
        private const string Secret = "Server=prod-db;Password=hunter2 is not valid";

        private static async Task<(DefaultHttpContext Context, JsonElement Body)> RunAsync(string environmentName)
        {
            var env = new Mock<IHostEnvironment>();
            env.SetupGet(e => e.EnvironmentName).Returns(environmentName);

            var middleware = new GlobalExceptionMiddleware(
                _ => throw new InvalidOperationException(Secret),
                NullLogger<GlobalExceptionMiddleware>.Instance,
                env.Object);

            var context = new DefaultHttpContext();
            context.Request.Path = "/api/banking/transfer";
            context.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(context);

            context.Response.Body.Position = 0;
            var body = await JsonDocument.ParseAsync(context.Response.Body);
            return (context, body.RootElement.Clone());
        }

        [Fact]
        public async Task In_production_the_client_gets_a_generic_message_and_a_trace_id_not_the_exception()
        {
            var (context, body) = await RunAsync(Environments.Production);

            Assert.Equal(500, context.Response.StatusCode);
            Assert.Equal("application/problem+json", context.Response.ContentType);

            var raw = body.GetRawText();
            Assert.DoesNotContain("hunter2", raw);
            Assert.DoesNotContain("prod-db", raw);
            Assert.DoesNotContain(nameof(InvalidOperationException), raw);

            Assert.Equal(context.TraceIdentifier, body.GetProperty("traceId").GetString());
            Assert.Equal("/api/banking/transfer", body.GetProperty("instance").GetString());
        }

        [Fact]
        public async Task On_a_developer_machine_the_full_exception_is_shown()
        {
            var (_, body) = await RunAsync(Environments.Development);

            Assert.Contains("hunter2", body.GetProperty("detail").GetString());
        }

        [Fact]
        public async Task A_request_that_does_not_throw_passes_through_untouched()
        {
            var env = new Mock<IHostEnvironment>();
            env.SetupGet(e => e.EnvironmentName).Returns(Environments.Production);
            var middleware = new GlobalExceptionMiddleware(
                ctx => { ctx.Response.StatusCode = 204; return Task.CompletedTask; },
                NullLogger<GlobalExceptionMiddleware>.Instance,
                env.Object);

            var context = new DefaultHttpContext();
            await middleware.InvokeAsync(context);

            Assert.Equal(204, context.Response.StatusCode);
        }
    }
}
