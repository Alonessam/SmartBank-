using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using SmartBank.API.Security;
using SmartBank.API.Services;
using SmartBank.Core.Common;
using SmartBank.Core.DTOs;
using SmartBank.Core.Entities;
using SmartBank.Core.Interfaces;

namespace SmartBank.API.Hubs
{
    /// <summary>
    /// The live support chat. Methods are called by the web app over SignalR; replies are pushed to the "session group" of a
    /// conversation (its owner, support agents who joined it) and the "Agents" group gets new-conversation requests.
    /// A call made after the access token's expiry is refused by <see cref="HubTokenExpiryFilter"/> ("Session expired").
    /// </summary>
    [Authorize]
    public class SupportHub : Hub
    {
        private readonly IChatService _chatService;
        private readonly IBankingService _bankingService;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ChatRateLimiter _limiter;
        private readonly ChatSettings _limits;
        private readonly ILogger<SupportHub> _logger;

        public SupportHub(
            IChatService chatService,
            IBankingService bankingService,
            IServiceScopeFactory scopeFactory,
            ChatRateLimiter limiter,
            ChatSettings limits,
            ILogger<SupportHub> logger)
        {
            _chatService = chatService;
            _bankingService = bankingService;
            _scopeFactory = scopeFactory;
            _limiter = limiter;
            _limits = limits;
            _logger = logger;
        }

        /// <summary>Does nothing. A client can call it to be sure everything it sent before has been handled (and pushed back).</summary>
        public Task PingAsync() => Task.CompletedTask;

        public async Task StartSessionAsync(string title)
        {
            var userId = GetUserId();

            if (!_limiter.TryAcquire($"session:{LimitKey}", _limits.SessionsPerHour, TimeSpan.FromHours(1)))
            {
                await Clients.Caller.SendAsync("Error", "You have started too many chats. Please try again later.");
                return;
            }

            // The title is shown to every support agent: cap it.
            title = (title ?? string.Empty).Trim();
            if (title.Length > ChatLimits.MaxTitleLength) title = title[..ChatLimits.MaxTitleLength];

            var result = await _chatService.CreateSessionAsync(userId, title);

            if (result.IsSuccess && result.Data != null)
            {
                var session = result.Data;
                await Groups.AddToGroupAsync(Context.ConnectionId, session.Id.ToString());
                await Clients.Caller.SendAsync("SessionStarted", session);

                // Proactively notify customer service agents in the "Agents" group
                await Clients.Group("Agents").SendAsync("NewSessionRequest", session);
            }
            else
            {
                await Clients.Caller.SendAsync("Error", "Could not start support session.");
            }
        }

        public async Task JoinSessionAsync(Guid sessionId)
        {
            // Without this check any signed-in user could join any session's group and read it live.
            if (!await CanAccessSessionAsync(sessionId))
            {
                await Clients.Caller.SendAsync("Error", "Access denied to this chat session.");
                return;
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, sessionId.ToString());

            // Everybody in the room is told who came in.
            var username = Context.User?.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? "Support Agent";
            await Clients.Group(sessionId.ToString()).SendAsync("UserJoined", new { username, sessionId });
        }

        /// <summary>
        /// Stops the pushes of a conversation to this connection (the web app calls it when it switches to another one).
        /// Owner or support agent only; leaving a group the connection is not in is not an error.
        /// </summary>
        public async Task LeaveSessionAsync(Guid sessionId)
        {
            if (!await CanAccessSessionAsync(sessionId))
            {
                await Clients.Caller.SendAsync("Error", "Access denied to this chat session.");
                return;
            }

            await Groups.RemoveFromGroupAsync(Context.ConnectionId, sessionId.ToString());
        }

        public async Task SendMessageAsync(Guid sessionId, string content)
        {
            var userId = GetUserId();

            // Every message costs a database write and, for a customer, an AI call, so size and rate are limited here
            // (ASP.NET's rate-limiting middleware does not see calls made over an open SignalR connection).
            content = (content ?? string.Empty).Trim();
            if (content.Length == 0)
            {
                await Clients.Caller.SendAsync("Error", "Message cannot be empty.");
                return;
            }

            if (content.Length > ChatLimits.MaxMessageLength)
            {
                await Clients.Caller.SendAsync("Error", $"Message is too long (at most {ChatLimits.MaxMessageLength} characters).");
                return;
            }

            var perMinute = IsAgent ? _limits.MessagesPerMinute * ChatSettings.AgentMultiplier : _limits.MessagesPerMinute;
            if (!_limiter.TryAcquire($"msg-min:{LimitKey}", perMinute, TimeSpan.FromMinutes(1)) ||
                (!IsAgent && !_limiter.TryAcquire($"msg-hour:{LimitKey}", _limits.MessagesPerHour, TimeSpan.FromHours(1))))
            {
                await Clients.Caller.SendAsync("Error", "You are sending messages too fast. Please wait a moment.");
                return;
            }

            // The sender is the session's owner ("User") or a support agent ("Agent"). Anyone else is refused: it used
            // to be that every non-owner was labelled "Agent", so any customer could write into someone else's chat
            // in the bank's voice.
            var isOwner = userId.HasValue && await _chatService.IsSessionOwnerAsync(sessionId, userId.Value);
            if (!isOwner && !IsAgent)
            {
                await Clients.Caller.SendAsync("Error", "Access denied to this chat session.");
                return;
            }

            var senderRole = isOwner ? ChatSenders.User : ChatSenders.Agent;
            var result = await _chatService.AddMessageAsync(sessionId, senderRole, content);

            if (!result.IsSuccess || result.Data == null)
            {
                await Clients.Caller.SendAsync("Error", result.ErrorKey == "SessionClosed" ? "This chat session has been closed." : "Could not send message.");
                return;
            }

            // Broadcast the message to everyone in the session group (User + Agent)
            await Clients.Group(sessionId.ToString()).SendAsync("ReceiveMessage", result.Data);

            // A customer's message gets an AI reply, written in the background.
            if (senderRole == ChatSenders.User)
            {
                RunInBackground("reply", responder => responder.RespondToUserMessageAsync(sessionId));
            }
        }

        public async Task ConfirmTransferFromChatAsync(Guid sessionId, string source, string destination, decimal amount, string description, string? otpCode = null)
        {
            var userId = GetUserId();
            if (!userId.HasValue)
            {
                await Clients.Caller.SendAsync("Error", "Unauthorized.");
                return;
            }

            // Only the owner of the conversation confirms a transfer in it (an agent cannot move a customer's money).
            if (!await _chatService.IsSessionOwnerAsync(sessionId, userId.Value))
            {
                await Clients.Caller.SendAsync("Error", "Access denied to this chat session.");
                return;
            }

            // A closed conversation cannot be answered ("transfer done" would have nowhere to go), so nothing is moved from it.
            if (!await _chatService.IsSessionOpenAsync(sessionId))
            {
                await Clients.Caller.SendAsync("Error", "This chat session has been closed.");
                return;
            }

            var request = new TransferRequestDto
            {
                SourceAccountNumber = source ?? string.Empty,
                DestinationAccountNumber = destination ?? string.Empty,
                Amount = amount,
                Description = description ?? string.Empty,
                OtpCode = otpCode
            };

            // The REST endpoint validates the request model; a hub method gets no such check, so do the same here
            // (amount range and scale, description length) before the transfer service sees the values.
            var problems = new List<ValidationResult>();
            if (!Validator.TryValidateObject(request, new ValidationContext(request), problems, validateAllProperties: true))
            {
                await Clients.Caller.SendAsync("Error", problems[0].ErrorMessage ?? "Invalid transfer request.");
                return;
            }

            if (!_limiter.TryAcquire($"transfer:{LimitKey}", _limits.TransfersPerMinute, TimeSpan.FromMinutes(1)))
            {
                await Clients.Caller.SendAsync("Error", "Too many transfer attempts. Please wait a moment.");
                return;
            }

            var transferResult = await _bankingService.TransferMoneyAsync(userId.Value, request);

            if (transferResult.IsSuccess && transferResult.Data != null)
            {
                var tx = transferResult.Data;

                // Only numbers and account numbers: no free text of the customer's in a message the server writes as "System".
                var marker = $"[TRANSFER_SUCCESS: source={tx.SourceAccountNumber}, destination={tx.DestinationAccountNumber}, amount={Money.Format(tx.Amount)}]";
                var stored = await _chatService.AddMessageAsync(sessionId, ChatSenders.System, marker);
                if (stored.IsSuccess && stored.Data != null)
                {
                    await Clients.Group(sessionId.ToString()).SendAsync("ReceiveMessage", stored.Data);
                }

                var sourceNumber = tx.SourceAccountNumber ?? source!;
                var destinationNumber = tx.DestinationAccountNumber ?? destination!;
                RunInBackground("transfer comment", responder => responder.CommentOnTransferAsync(sessionId, tx.Amount, sourceNumber, destinationNumber));
                return;
            }

            await ReportTransferFailureAsync(sessionId, transferResult.ErrorKey ?? "TransferFailed", transferResult.Message ?? "Transfer failed.");
        }

        /// <summary>
        /// Writes "transfer failed / code needed" into the conversation. In demo mode the message carries the one-time code
        /// ("...|OTP:123456"); the stored copy, which agents and later readers see, never does. The code is pushed only to the
        /// customer's own connection, as a copy that is not stored.
        /// </summary>
        private async Task ReportTransferFailureAsync(Guid sessionId, string errorKey, string message)
        {
            const string codeMarker = "|OTP:";
            var hasCode = message.Contains(codeMarker, StringComparison.Ordinal);
            var cleanMessage = hasCode ? message[..message.IndexOf(codeMarker, StringComparison.Ordinal)] : message;

            var stored = await _chatService.AddMessageAsync(sessionId, ChatSenders.System, $"[TRANSFER_FAILED: errorKey={errorKey}, message={cleanMessage}]");
            if (!stored.IsSuccess || stored.Data == null) return;

            if (!hasCode)
            {
                await Clients.Group(sessionId.ToString()).SendAsync("ReceiveMessage", stored.Data);
                return;
            }

            await Clients.GroupExcept(sessionId.ToString(), Context.ConnectionId).SendAsync("ReceiveMessage", stored.Data);
            await Clients.Caller.SendAsync("ReceiveMessage", new ChatMessageDto
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                Sender = ChatSenders.System,
                Content = $"[TRANSFER_FAILED: errorKey={errorKey}, message={message}]",
                CreatedAt = stored.Data.CreatedAt
            });
        }

        public async Task CloseSessionAsync(Guid sessionId)
        {
            if (!await CanAccessSessionAsync(sessionId))
            {
                await Clients.Caller.SendAsync("Error", "Access denied to this chat session.");
                return;
            }

            var result = await _chatService.CloseSessionAsync(sessionId);

            if (result.IsSuccess)
            {
                await Clients.Group(sessionId.ToString()).SendAsync("SessionClosed", sessionId);
            }
            else
            {
                await Clients.Caller.SendAsync("Error", "Could not close session.");
            }
        }

        // Agents call this method to connect to the general agents feed
        public async Task RegisterAgentAsync()
        {
            // Joining the "Agents" group means receiving every new customer's session request: agents only.
            if (!IsAgent)
            {
                await Clients.Caller.SendAsync("Error", "Support agent role required.");
                return;
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, "Agents");
            await Clients.Caller.SendAsync("AgentRegistered");
        }

        /// <summary>
        /// Runs AI work after the hub call has returned (the customer must not wait for the model). It gets its own scope,
        /// because the hub's own services are gone once the call is over. A failure is logged, never thrown.
        /// </summary>
        private void RunInBackground(string what, Func<SupportAiResponder, Task> work)
        {
            var scopeFactory = _scopeFactory;
            var logger = _logger;

            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    await work(scope.ServiceProvider.GetRequiredService<SupportAiResponder>());
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "The AI {What} in the support chat failed.", what);
                }
            });
        }

        private bool IsAgent => Context.User?.IsInRole(RoleNames.Agent) == true;

        // Limits are per signed-in user, not per connection: opening more tabs must not multiply the allowance.
        private string LimitKey => GetUserId()?.ToString() ?? Context.ConnectionId;

        // A session may be used by its owner and by support agents, nobody else.
        private async Task<bool> CanAccessSessionAsync(Guid sessionId)
        {
            if (IsAgent) return true;

            var userId = GetUserId();
            return userId.HasValue && await _chatService.IsSessionOwnerAsync(sessionId, userId.Value);
        }

        private Guid? GetUserId()
        {
            var value = Context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            return Guid.TryParse(value, out var userId) ? userId : null;
        }
    }
}
