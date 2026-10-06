using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using SmartBank.Core.DTOs;
using SmartBank.Tests.Api;

namespace SmartBank.Tests.Support
{
    /// <summary>One signed-in SignalR connection and everything the server has pushed to it.</summary>
    public sealed class HubTestClient : IAsyncDisposable
    {
        public HubConnection Connection { get; }
        public List<string> Errors { get; } = new();
        public List<ChatMessageDto> Messages { get; } = new();

        public HubTestClient(ApiFactory factory, ApiFactory.TestUser user)
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

        public static async Task<HubTestClient> ConnectAsync(ApiFactory factory, ApiFactory.TestUser user)
        {
            var client = new HubTestClient(factory, user);
            await client.Connection.StartAsync();
            return client;
        }

        public List<ChatMessageDto> MessagesSnapshot() { lock (Messages) return Messages.ToList(); }

        public List<string> ErrorsSnapshot() { lock (Errors) return Errors.ToList(); }

        /// <summary>
        /// A round trip to the server. Everything the server pushed to this connection BEFORE the call has been delivered when it
        /// returns, so "nothing arrived" can be asserted without waiting a fixed time.
        /// </summary>
        public Task BarrierAsync() => Connection.InvokeAsync("PingAsync");

        public ValueTask DisposeAsync() => Connection.DisposeAsync();

        public static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
        {
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < until)
            {
                if (condition()) return true;
                await Task.Delay(20);
            }

            return condition();
        }
    }
}
