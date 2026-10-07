using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using SmartBank.Core.DTOs;
using SmartBank.Core.Interfaces;
using SmartBank.Tests.Support;

namespace SmartBank.Tests.Api
{
    /// <summary>What the web app relies on: every message names its conversation, and a connection can leave one.</summary>
    [Collection("EncryptionHelper")]
    public class SupportHubContractTests : IClassFixture<ApiFactory>
    {
        private readonly ApiFactory _factory;

        public SupportHubContractTests(ApiFactory factory)
        {
            _factory = factory;
            _factory.Ai.Reply = _ => "Fake AI reply.";
        }

        [Fact]
        public async Task Every_message_that_reaches_a_client_names_its_session()
        {
            var customer = await _factory.RegisterCustomerAsync();
            var session = await _factory.CreateChatSessionAsync(customer, "hello");
            await using var hub = await HubTestClient.ConnectAsync(_factory, customer);
            await hub.Connection.InvokeAsync("JoinSessionAsync", session);

            await hub.Connection.InvokeAsync("SendMessageAsync", session, "hi there");
            Assert.True(await HubTestClient.WaitForAsync(() => hub.MessagesSnapshot().Any(m => m.Sender == "AI")));

            Assert.All(hub.MessagesSnapshot(), m => Assert.Equal(session, m.SessionId));

            using var scope = _factory.Services.CreateScope();
            var history = (await scope.ServiceProvider.GetRequiredService<IChatService>().GetSessionMessagesForAgentAsync(session)).Data!;
            Assert.NotEmpty(history);
            Assert.All(history, m => Assert.Equal(session, m.SessionId));

            using var client = _factory.ClientFor(customer);
            var body = await client.GetStringAsync($"/api/chat/messages/{session}");
            Assert.Contains("\"sessionId\":\"" + session.ToString("D") + "\"", body);
        }

        [Fact]
        public async Task A_failed_transfer_card_pushed_to_the_customer_names_the_session_too()
        {
            var customer = await _factory.RegisterCustomerAsync();
            var from = (await _factory.GetFirstAccountAsync(customer)).AccountNumber;
            var session = await _factory.CreateChatSessionAsync(customer, "hello");
            await using var hub = await HubTestClient.ConnectAsync(_factory, customer);
            await hub.Connection.InvokeAsync("JoinSessionAsync", session);

            await hub.Connection.InvokeAsync("ConfirmTransferFromChatAsync", session, from, "TR0000000000000000", 10m, "x", (string?)null);

            Assert.True(await HubTestClient.WaitForAsync(() => hub.MessagesSnapshot().Any(m => m.Content.StartsWith("[TRANSFER_FAILED"))));
            Assert.All(hub.MessagesSnapshot(), m => Assert.Equal(session, m.SessionId));
        }

        [Fact]
        public async Task After_leaving_a_session_its_messages_are_no_longer_pushed_and_leaving_twice_is_fine()
        {
            var customer = await _factory.RegisterCustomerAsync();
            var agent = await _factory.RegisterAgentAsync();
            var session = await _factory.CreateChatSessionAsync(customer, "hello");
            await using var customerHub = await HubTestClient.ConnectAsync(_factory, customer);
            await using var agentHub = await HubTestClient.ConnectAsync(_factory, agent);
            await customerHub.Connection.InvokeAsync("JoinSessionAsync", session);
            await agentHub.Connection.InvokeAsync("JoinSessionAsync", session);

            await customerHub.Connection.InvokeAsync("LeaveSessionAsync", session);
            await customerHub.Connection.InvokeAsync("LeaveSessionAsync", session); // not in the group any more: no error
            await agentHub.Connection.InvokeAsync("SendMessageAsync", session, "hello customer");

            Assert.True(await HubTestClient.WaitForAsync(() => agentHub.MessagesSnapshot().Any(m => m.Sender == "Agent")));
            await customerHub.BarrierAsync();
            Assert.Empty(customerHub.MessagesSnapshot());
            Assert.Empty(customerHub.ErrorsSnapshot());
        }

        [Fact]
        public async Task A_stranger_cannot_use_leave_session_on_somebody_elses_conversation()
        {
            var customer = await _factory.RegisterCustomerAsync();
            var stranger = await _factory.RegisterCustomerAsync();
            var session = await _factory.CreateChatSessionAsync(customer, "hello");
            await using var hub = await HubTestClient.ConnectAsync(_factory, stranger);

            await hub.Connection.InvokeAsync("LeaveSessionAsync", session);

            Assert.True(await HubTestClient.WaitForAsync(() => hub.ErrorsSnapshot().Count == 1));
            Assert.Contains("Access denied", hub.ErrorsSnapshot()[0]);
        }
    }
}
