using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using SmartBank.API.Middlewares;
using SmartBank.Infrastructure.Data;

namespace SmartBank.Tests
{
    public class StartupAndMiddlewareTests
    {
        // ---- which database ----------------------------------------------------------------------------------

        [Theory]
        [InlineData(@"Server=(localdb)\mssqllocaldb;Database=SmartBankDb;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True", DatabaseKind.SqlServer)]
        [InlineData("Server=tcp:db.example.com,1433;Initial Catalog=SmartBank;User ID=sa;Password=x;Encrypt=True", DatabaseKind.SqlServer)]
        [InlineData("Data Source=.;Initial Catalog=SmartBank;Integrated Security=True", DatabaseKind.SqlServer)]
        [InlineData("Host=localhost;Port=5432;Database=smartbank;Username=postgres;Password=x", DatabaseKind.PostgreSql)]
        [InlineData("host=db.supabase.co;database=postgres;username=u;password=p", DatabaseKind.PostgreSql)]
        [InlineData("HOST=db.supabase.co;Database=postgres;User ID=u;Password=p", DatabaseKind.PostgreSql)]
        [InlineData("Server=db.supabase.co;Port=6543;Database=postgres;User Id=u;Password=p;SSL Mode=Require", DatabaseKind.PostgreSql)]
        [InlineData("Server=db.supabase.co;Database=postgres;User Id=u;Password=p;sslmode=require", DatabaseKind.PostgreSql)]
        [InlineData("postgresql://u:p@db.supabase.co:6543/postgres", DatabaseKind.PostgreSql)]
        [InlineData("POSTGRES://u:p@db.supabase.co/postgres?sslmode=require", DatabaseKind.PostgreSql)]
        public void The_database_kind_is_read_from_the_connection_string_without_regard_to_case(string connectionString, DatabaseKind expected)
        {
            Assert.Equal(expected, DatabaseProviderSelector.Detect(connectionString));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void A_missing_connection_string_stops_the_start_with_a_clear_message(string? connectionString)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => DatabaseProviderSelector.Detect(connectionString));

            Assert.Contains("ConnectionStrings:DefaultConnection", ex.Message);
        }

        [Fact]
        public void A_connection_string_that_cannot_be_read_is_reported_as_such()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => DatabaseProviderSelector.Detect("this=is=not;valid=\"quotes"));

            Assert.Contains("not a valid connection string", ex.Message);
        }

        [Fact]
        public void A_postgres_uri_is_converted_to_the_form_npgsql_reads()
        {
            var converted = DatabaseProviderSelector.ToNpgsqlConnectionString("postgresql://app%40user:p%40ss%3Aword@db.supabase.co:6543/smartbank?sslmode=require");

            var builder = new NpgsqlConnectionStringBuilder(converted);
            Assert.Equal("db.supabase.co", builder.Host);
            Assert.Equal(6543, builder.Port);
            Assert.Equal("smartbank", builder.Database);
            Assert.Equal("app@user", builder.Username);
            Assert.Equal("p@ss:word", builder.Password);
            Assert.Equal(SslMode.Require, builder.SslMode);
        }

        [Fact]
        public void A_uri_without_a_port_uses_5432_and_a_key_value_string_is_left_alone()
        {
            var builder = new NpgsqlConnectionStringBuilder(DatabaseProviderSelector.ToNpgsqlConnectionString("postgres://u:p@localhost/db"));
            Assert.Equal(5432, builder.Port);
            Assert.Equal("db", builder.Database);

            const string keyValue = "Host=localhost;Database=db;Username=u;Password=p";
            Assert.Equal(keyValue, DatabaseProviderSelector.ToNpgsqlConnectionString(keyValue));
        }

        [Theory]
        [InlineData("postgresql://u:p@/db")]
        [InlineData("postgresql://u:p@host")]
        [InlineData("postgresql://u:p@host/")]
        public void A_broken_uri_is_rejected_with_a_message_that_says_what_is_missing(string uri)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => DatabaseProviderSelector.ToNpgsqlConnectionString(uri));

            Assert.Contains("postgresql://user:password@host:5432/database", ex.Message);
        }

        // ---- the error middleware ----------------------------------------------------------------------------

        private static GlobalExceptionMiddleware Middleware(RequestDelegate next, string environment = "Production")
        {
            var env = new Mock<IHostEnvironment>();
            env.SetupGet(e => e.EnvironmentName).Returns(environment);
            return new GlobalExceptionMiddleware(next, NullLogger<GlobalExceptionMiddleware>.Instance, env.Object);
        }

        [Fact]
        public async Task A_client_that_went_away_gets_499_without_a_body_and_is_not_logged_as_a_server_error()
        {
            using var aborted = new CancellationTokenSource();
            var context = new DefaultHttpContext { RequestAborted = aborted.Token };
            context.Response.Body = new MemoryStream();
            var middleware = Middleware(_ =>
            {
                aborted.Cancel();
                throw new OperationCanceledException(aborted.Token);
            });

            await middleware.InvokeAsync(context);

            Assert.Equal(GlobalExceptionMiddleware.ClientClosedRequest, context.Response.StatusCode);
            Assert.Equal(0, context.Response.Body.Length);
        }

        [Fact]
        public async Task A_cancellation_that_was_not_the_clients_is_still_a_server_error()
        {
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();
            var middleware = Middleware(_ => throw new OperationCanceledException("a timeout inside the server"));

            await middleware.InvokeAsync(context);

            Assert.Equal(500, context.Response.StatusCode);
            Assert.True(context.Response.Body.Length > 0);
        }

        private sealed class StartedResponseFeature : Microsoft.AspNetCore.Http.Features.IHttpResponseFeature
        {
            public int StatusCode { get; set; } = 200;
            public string? ReasonPhrase { get; set; }
            public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
            public Stream Body { get; set; } = new MemoryStream();
            public bool HasStarted => true;
            public void OnStarting(Func<object, Task> callback, object state) { }
            public void OnCompleted(Func<object, Task> callback, object state) { }
        }

        [Fact]
        public async Task When_the_answer_has_already_started_the_middleware_neither_rewrites_it_nor_throws()
        {
            var context = new DefaultHttpContext();
            var feature = new StartedResponseFeature();
            context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>(feature);
            var middleware = Middleware(_ => throw new InvalidOperationException("failed halfway through the response"));

            await middleware.InvokeAsync(context); // must not throw "headers are read-only"

            Assert.Equal(200, feature.StatusCode);
            Assert.Equal(0, feature.Body.Length);
        }

        [Fact]
        public async Task The_problem_answer_clears_what_was_written_before_the_failure()
        {
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();
            var middleware = Middleware(async ctx =>
            {
                ctx.Response.Headers["X-Partial"] = "yes";
                await Task.CompletedTask;
                throw new InvalidOperationException("secret");
            });

            await middleware.InvokeAsync(context);

            Assert.Equal(500, context.Response.StatusCode);
            Assert.False(context.Response.Headers.ContainsKey("X-Partial"));
            context.Response.Body.Position = 0;
            using var json = await JsonDocument.ParseAsync(context.Response.Body);
            Assert.DoesNotContain("secret", json.RootElement.GetRawText());
        }
    }
}
