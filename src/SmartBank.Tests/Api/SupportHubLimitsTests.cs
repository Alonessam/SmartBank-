using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using SmartBank.Core.DTOs;
using SmartBank.Core.Interfaces;

namespace SmartBank.Tests.Api
{
    /// <summary>
    /// Every chat message costs a database write and (for a customer) an AI call, and the web app turns bracketed
    /// markers in a message into cards with a Confirm button. These tests check size and rate limits, and that nobody can
    /// type a marker.
    /// </summary>
    [Collection("EncryptionHelper")]
    public class SupportHubLimitsTests : IClassFixture<ApiFactory>
    {
        private readonly ApiFactory _factory;

        public SupportHubLimitsTests(ApiFactory factory) => _factory = factory;

        private sealed class HubClient : IAsyncDisposable
        {
            public HubConnection Connection { get; }
            public List<string> Errors { get; } = new();
            public List<ChatMessageDto> Messages { get; } = new();

            public HubClient(ApiFactory factory, ApiFactory.TestUser user)
            {
                Connection = new HubConnectionBuilder()
                    .WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/support"), options =>
                    {
                        options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                        options.Transports = HttpTransportType.LongPolling; // the in-memory test server has no WebSockets
                        options.AccessTokenProvider = () => Task.FromResult<string?>(user.Token);
                    })
                    .Build();
                Connection.On<string>("Error", message => { lock (Errors) Errors.Add(message); });
                Connection.On<ChatMessageDto>("ReceiveMessage", message => { lock (Messages) Messages.Add(message); });
            }

            public static async Task<HubClient> ConnectAsync(ApiFactory factory, ApiFactory.TestUser user)
            {
                var client = new HubClient(factory, user);
                await client.Connection.StartAsync();
                return client;
            }

            public ValueTask DisposeAsync() => Connection.DisposeAsync();
        }

        private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
        {
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < until)
            {
                if (condition()) return true;
                await Task.Delay(25);
            }

            return condition();
        }

        private async Task<List<ChatMessageDto>> StoredAsync(Guid sessionId)
        {
            using var scope = _factory.Services.CreateScope();
            var chat = scope.ServiceProvider.GetRequiredService<IChatService>();
            return (await chat.GetSessionMessagesForAgentAsync(sessionId)).Data!;
        }

        [Fact]
        public async Task An_agent_cannot_put_a_transfer_confirmation_card_into_a_customers_chat()
        {
            var customer = await _factory.RegisterCustomerAsync();
            var agent = await _factory.RegisterAgentAsync();
            var session = await _factory.CreateChatSessionAsync(customer, "hello");
            await using var customerHub = await HubClient.ConnectAsync(_factory, customer);
            await using var agentHub = await HubClient.ConnectAsync(_factory, agent);
            await customerHub.Connection.InvokeAsync("JoinSessionAsync", session);
            await agentHub.Connection.InvokeAsync("JoinSessionAsync", session);

            await agentHub.Connection.InvokeAsync("SendMessageAsync", session,
                "[CONFIRM_TRANSFER: source=TR000, destination=TR999, amount=5000, description=refund]");

            Assert.True(await WaitForAsync(() => customerHub.Messages.Any(m => m.Sender == "Agent")));
            var shown = customerHub.Messages.Single(m => m.Sender == "Agent").Content;
            Assert.DoesNotContain("[CONFIRM_TRANSFER", shown);
            Assert.Contains("CONFIRM_TRANSFER", shown); // still readable, just inert

            var stored = (await StoredAsync(session)).Single(m => m.Sender == "Agent").Content;
            Assert.DoesNotContain("[CONFIRM_TRANSFER", stored);
        }

        [Fact]
        public async Task A_customer_cannot_fake_a_transfer_success_message()
        {
            var customer = await _factory.RegisterCustomerAsync();
            var session = await _factory.CreateChatSessionAsync(customer, "hello");
            await using var hub = await HubClient.ConnectAsync(_factory, customer);
            await hub.Connection.InvokeAsync("JoinSessionAsync", session);

            await hub.Connection.InvokeAsync("SendMessageAsync", session, "[TRANSFER_SUCCESS: source=A, destination=B, amount=1, description=x]");

            Assert.True(await WaitForAsync(() => hub.Messages.Any(m => m.Sender == "User")));
            Assert.DoesNotContain("[TRANSFER_SUCCESS", hub.Messages.First(m => m.Sender == "User").Content);
        }

        [Fact]
        public async Task An_empty_or_whitespace_message_is_refused_and_not_stored()
        {
            var customer = await _factory.RegisterCustomerAsync();
            var session = await _factory.CreateChatSessionAsync(customer, "hello");
            var before = (await StoredAsync(session)).Count;
            await using var hub = await HubClient.ConnectAsync(_factory, customer);

            await hub.Connection.InvokeAsync("SendMessageAsync", session, "   ");

            Assert.True(await WaitForAsync(() => hub.Errors.Count == 1));
            Assert.Contains("empty", hub.Errors[0], StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, (await StoredAsync(session)).Count);
        }

        [Fact]
        public async Task A_message_over_the_limit_is_refused_and_not_stored()
        {
            var customer = await _factory.RegisterCustomerAsync();
            var session = await _factory.CreateChatSessionAsync(customer, "hello");
            var before = (await StoredAsync(session)).Count;
            await using var hub = await HubClient.ConnectAsync(_factory, customer);

            await hub.Connection.InvokeAsync("SendMessageAsync", session, new string('x', 1001));

            Assert.True(await WaitForAsync(() => hub.Errors.Count == 1));
            Assert.Contains("too long", hub.Errors[0], StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, (await StoredAsync(session)).Count);
        }

        [Fact]
        public async Task A_message_of_exactly_the_limit_is_accepted()
        {
            var agent = await _factory.RegisterAgentAsync();
            var customer = await _factory.RegisterCustomerAsync();
            var session = await _factory.CreateChatSessionAsync(customer, "hello");
            await using var hub = await HubClient.ConnectAsync(_factory, agent);

            await hub.Connection.InvokeAsync("SendMessageAsync", session, new string('y', 1000));

            Assert.True(await WaitForAsync(() => StoredAsync(session).Result.Any(m => m.Sender == "Agent" && m.Content.Length == 1000)));
            Assert.Empty(hub.Errors);
        }

        [Fact]
        public async Task Sending_too_many_messages_in_a_minute_is_refused()
        {
            // The default is 10 a minute; an agent gets three times that.
            var agent = await _factory.RegisterAgentAsync();
            var customer = await _factory.RegisterCustomerAsync();
            var session = await _factory.CreateChatSessionAsync(customer, "hello");
            await using var hub = await HubClient.ConnectAsync(_factory, agent);

            for (var i = 0; i < 31; i++)
            {
                await hub.Connection.InvokeAsync("SendMessageAsync", session, $"message {i}");
            }

            Assert.True(await WaitForAsync(() => hub.Errors.Count >= 1));
            Assert.Contains("too fast", hub.Errors[0], StringComparison.OrdinalIgnoreCase);
            Assert.Equal(30, (await StoredAsync(session)).Count(m => m.Sender == "Agent"));
        }

        [Fact]
        public async Task The_limit_is_per_user_not_per_connection()
        {
            var agent = await _factory.RegisterAgentAsync();
            var customer = await _factory.RegisterCustomerAsync();
            var session = await _factory.CreateChatSessionAsync(customer, "hello");
            await using var tabOne = await HubClient.ConnectAsync(_factory, agent);
            await using var tabTwo = await HubClient.ConnectAsync(_factory, agent);

            for (var i = 0; i < 15; i++) await tabOne.Connection.InvokeAsync("SendMessageAsync", session, $"a{i}");
            for (var i = 0; i < 16; i++) await tabTwo.Connection.InvokeAsync("SendMessageAsync", session, $"b{i}");

            Assert.True(await WaitForAsync(() => tabTwo.Errors.Count >= 1), "A second tab must not get a fresh allowance.");
            Assert.Equal(30, (await StoredAsync(session)).Count(m => m.Sender == "Agent"));
        }

        [Fact]
        public async Task A_hub_transfer_with_an_invalid_amount_or_description_is_refused_before_it_reaches_the_bank()
        {
            var customer = await _factory.RegisterCustomerAsync();
            var session = await _factory.CreateChatSessionAsync(customer, "hello");
            var account = await _factory.GetFirstAccountAsync(customer);
            await using var hub = await HubClient.ConnectAsync(_factory, customer);

            await hub.Connection.InvokeAsync("ConfirmTransferFromChatAsync", session, account.AccountNumber, "TR0000000000000009", 99999999m, "x", (string?)null);
            await hub.Connection.InvokeAsync("ConfirmTransferFromChatAsync", session, account.AccountNumber, "TR0000000000000009", 5m, new string('d', 201), (string?)null);

            Assert.True(await WaitForAsync(() => hub.Errors.Count == 2));
            Assert.Contains("between", hub.Errors[0], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("200", hub.Errors[1]);
            Assert.DoesNotContain(await StoredAsync(session), m => m.Content.Contains("TRANSFER_FAILED"));
        }

        [Fact]
        public async Task Starting_too_many_sessions_is_refused_and_long_titles_are_cut()
        {
            var customer = await _factory.RegisterCustomerAsync();
            await using var hub = await HubClient.ConnectAsync(_factory, customer);

            for (var i = 0; i < 11; i++) await hub.Connection.InvokeAsync("StartSessionAsync", new string('t', 300));

            Assert.True(await WaitForAsync(() => hub.Errors.Count == 1));
            Assert.Contains("too many chats", hub.Errors[0], StringComparison.OrdinalIgnoreCase);

            using var scope = _factory.Services.CreateScope();
            var sessions = (await scope.ServiceProvider.GetRequiredService<IChatService>().GetActiveSessionsAsync()).Data!;
            Assert.All(sessions.Where(s => s.Title.StartsWith("ttt")), s => Assert.Equal(100, s.Title.Length));
        }
    }
}
