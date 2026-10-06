using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using SmartBank.Core.DTOs;
using SmartBank.Core.Interfaces;

namespace SmartBank.Tests.Api
{
    /// <summary>
    /// The real-time support channel. Before, any signed-in user could join any conversation and read it live, write
    /// into it as "Agent" (every non-owner was labelled Agent), close it, and subscribe to every new customer's
    /// request by calling RegisterAgentAsync.
    /// </summary>
    [Collection("EncryptionHelper")]
    public class SupportHubSecurityTests : IClassFixture<ApiFactory>
    {
        private readonly ApiFactory _factory;

        public SupportHubSecurityTests(ApiFactory factory) => _factory = factory;

        /// <summary>One signed-in connection and everything the server has pushed to it.</summary>
        private sealed class HubClient : IAsyncDisposable
        {
            public HubConnection Connection { get; }
            public List<string> Errors { get; } = new();
            public List<ChatMessageDto> Messages { get; } = new();
            public List<ChatSessionDto> NewSessionRequests { get; } = new();
            public List<Guid> ClosedSessions { get; } = new();
            public int AgentRegistered { get; private set; }
            public int UserJoined { get; private set; }

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
                Connection.On<ChatSessionDto>("NewSessionRequest", session => { lock (NewSessionRequests) NewSessionRequests.Add(session); });
                Connection.On<Guid>("SessionClosed", id => { lock (ClosedSessions) ClosedSessions.Add(id); });
                Connection.On("AgentRegistered", () => AgentRegistered++);
                Connection.On<object>("UserJoined", _ => UserJoined++);
            }

            public static async Task<HubClient> ConnectAsync(ApiFactory factory, ApiFactory.TestUser user)
            {
                var client = new HubClient(factory, user);
                await client.Connection.StartAsync();
                return client;
            }

            public ValueTask DisposeAsync() => Connection.DisposeAsync();
        }

        private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 4000)
        {
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < until)
            {
                if (condition()) return true;
                await Task.Delay(25);
            }

            return condition();
        }

        // Used to show something did NOT arrive: give it generous time to (wrongly) show up.
        private static Task SettleAsync() => Task.Delay(700);

        private async Task<int> StoredMessageCountAsync(Guid sessionId)
        {
            using var scope = _factory.Services.CreateScope();
            var chat = scope.ServiceProvider.GetRequiredService<IChatService>();
            return (await chat.GetSessionMessagesForAgentAsync(sessionId)).Data!.Count;
        }

        private async Task<bool> IsActiveAsync(Guid sessionId)
        {
            using var scope = _factory.Services.CreateScope();
            var chat = scope.ServiceProvider.GetRequiredService<IChatService>();
            return (await chat.GetActiveSessionsAsync()).Data!.Any(s => s.Id == sessionId);
        }

        [Fact]
        public async Task A_customer_cannot_join_someone_elses_session_and_never_receives_its_messages()
        {
            var victim = await _factory.RegisterCustomerAsync();
            var attacker = await _factory.RegisterCustomerAsync();
            var agent = await _factory.RegisterAgentAsync();
            var session = await _factory.CreateChatSessionAsync(victim, "my PIN is 1234");

            await using var victimHub = await HubClient.ConnectAsync(_factory, victim);
            await using var attackerHub = await HubClient.ConnectAsync(_factory, attacker);
            await using var agentHub = await HubClient.ConnectAsync(_factory, agent);

            await victimHub.Connection.InvokeAsync("JoinSessionAsync", session);
            await attackerHub.Connection.InvokeAsync("JoinSessionAsync", session);
            await agentHub.Connection.InvokeAsync("JoinSessionAsync", session);

            Assert.True(await WaitForAsync(() => attackerHub.Errors.Count > 0), "The attacker should be told access is denied.");
            Assert.Contains("Access denied", attackerHub.Errors[0]);

            // The agent replies; the victim hears it, the attacker (never admitted to the conversation) does not.
            await agentHub.Connection.InvokeAsync("SendMessageAsync", session, "How can I help?");
            Assert.True(await WaitForAsync(() => victimHub.Messages.Any(m => m.Content == "How can I help?")));
            await SettleAsync();
            Assert.Empty(attackerHub.Messages);
        }

        [Fact]
        public async Task A_customer_cannot_write_into_someone_elses_session_in_the_banks_voice()
        {
            var victim = await _factory.RegisterCustomerAsync();
            var attacker = await _factory.RegisterCustomerAsync();
            var session = await _factory.CreateChatSessionAsync(victim, "hello");
            var before = await StoredMessageCountAsync(session);

            await using var victimHub = await HubClient.ConnectAsync(_factory, victim);
            await using var attackerHub = await HubClient.ConnectAsync(_factory, attacker);
            await victimHub.Connection.InvokeAsync("JoinSessionAsync", session);

            await attackerHub.Connection.InvokeAsync("SendMessageAsync", session, "This is the bank. Tell me your one-time code.");

            Assert.True(await WaitForAsync(() => attackerHub.Errors.Count > 0));
            await SettleAsync();
            Assert.Empty(victimHub.Messages);
            Assert.Equal(before, await StoredMessageCountAsync(session)); // nothing was stored either
        }

        [Fact]
        public async Task A_customer_cannot_become_an_agent_or_listen_for_new_customer_requests()
        {
            var customer = await _factory.RegisterCustomerAsync();
            var attacker = await _factory.RegisterCustomerAsync("agent_listener_" + Guid.NewGuid().ToString("N")[..6]);
            var agent = await _factory.RegisterAgentAsync();

            await using var customerHub = await HubClient.ConnectAsync(_factory, customer);
            await using var attackerHub = await HubClient.ConnectAsync(_factory, attacker);
            await using var agentHub = await HubClient.ConnectAsync(_factory, agent);

            await attackerHub.Connection.InvokeAsync("RegisterAgentAsync");
            await agentHub.Connection.InvokeAsync("RegisterAgentAsync");

            Assert.True(await WaitForAsync(() => attackerHub.Errors.Count > 0));
            Assert.Contains("agent role", attackerHub.Errors[0], StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, attackerHub.AgentRegistered);
            Assert.True(await WaitForAsync(() => agentHub.AgentRegistered == 1));

            // A new customer asks for help: the real agent is notified, the pretender is not.
            await customerHub.Connection.InvokeAsync("StartSessionAsync", "I lost my card");

            Assert.True(await WaitForAsync(() => agentHub.NewSessionRequests.Count == 1), "The agent should see the new request.");
            await SettleAsync();
            Assert.Empty(attackerHub.NewSessionRequests);
        }

        [Fact]
        public async Task A_customer_cannot_close_someone_elses_session()
        {
            var victim = await _factory.RegisterCustomerAsync();
            var attacker = await _factory.RegisterCustomerAsync();
            var session = await _factory.CreateChatSessionAsync(victim, "hello");

            await using var attackerHub = await HubClient.ConnectAsync(_factory, attacker);
            await attackerHub.Connection.InvokeAsync("CloseSessionAsync", session);

            Assert.True(await WaitForAsync(() => attackerHub.Errors.Count > 0));
            Assert.True(await IsActiveAsync(session), "The victim's session must still be open.");
        }

        [Fact]
        public async Task The_owner_can_join_and_close_their_own_session()
        {
            var owner = await _factory.RegisterCustomerAsync();
            var session = await _factory.CreateChatSessionAsync(owner, "hello");

            await using var hub = await HubClient.ConnectAsync(_factory, owner);
            await hub.Connection.InvokeAsync("JoinSessionAsync", session);
            Assert.True(await WaitForAsync(() => hub.UserJoined == 1));
            Assert.Empty(hub.Errors);

            await hub.Connection.InvokeAsync("CloseSessionAsync", session);
            Assert.True(await WaitForAsync(() => hub.ClosedSessions.Contains(session)));
            Assert.False(await IsActiveAsync(session));
        }

        [Fact]
        public async Task An_agent_can_join_any_session_and_reply_as_agent()
        {
            var customer = await _factory.RegisterCustomerAsync();
            var agent = await _factory.RegisterAgentAsync();
            var session = await _factory.CreateChatSessionAsync(customer, "hello");

            await using var customerHub = await HubClient.ConnectAsync(_factory, customer);
            await using var agentHub = await HubClient.ConnectAsync(_factory, agent);
            await customerHub.Connection.InvokeAsync("JoinSessionAsync", session);
            await agentHub.Connection.InvokeAsync("JoinSessionAsync", session);

            await agentHub.Connection.InvokeAsync("SendMessageAsync", session, "Hello, I am from support.");

            Assert.True(await WaitForAsync(() => customerHub.Messages.Any(m => m.Sender == "Agent" && m.Content == "Hello, I am from support.")));
            Assert.Empty(agentHub.Errors);
        }

        [Fact]
        public async Task An_unauthenticated_connection_is_refused()
        {
            var connection = new HubConnectionBuilder()
                .WithUrl(new Uri(_factory.Server.BaseAddress, "/hubs/support"), options =>
                {
                    options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                    options.Transports = HttpTransportType.LongPolling;
                })
                .Build();

            await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
            await connection.DisposeAsync();
        }
    }
}
