using System.Globalization;
using Microsoft.AspNetCore.SignalR;
using SmartBank.API.Hubs;
using SmartBank.Core.Common;
using SmartBank.Core.DTOs;
using SmartBank.Core.Interfaces;

namespace SmartBank.API.Services
{
    /// <summary>
    /// Writes the AI's reply to a customer's message in the support chat: asks the model, carries out the two things it may
    /// ask for ("show the balances", "propose a transfer") and pushes the answer to the conversation.
    ///
    /// The model's text is untrusted. It can only (a) make the server read the balances of the conversation's own owner,
    /// which go back into the model and nowhere else, and (b) make the server build a confirmation card from parsed,
    /// validated fields; the customer must press Confirm, and the transfer then goes through every normal check. Any other
    /// bracketed marker in the text is made inert before it is stored.
    /// </summary>
    public class SupportAiResponder
    {
        private readonly IChatService _chat;
        private readonly IBankingService _banking;
        private readonly IAIChatbotService _ai;
        private readonly IHubContext<SupportHub> _hub;
        private readonly ILogger<SupportAiResponder> _logger;

        public SupportAiResponder(IChatService chat, IBankingService banking, IAIChatbotService ai, IHubContext<SupportHub> hub, ILogger<SupportAiResponder> logger)
        {
            _chat = chat;
            _banking = banking;
            _ai = ai;
            _hub = hub;
            _logger = logger;
        }

        /// <summary>Answers the newest customer message of the session.</summary>
        public async Task RespondToUserMessageAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            var ownerId = await _chat.GetSessionOwnerIdAsync(sessionId, cancellationToken);

            var historyResult = await _chat.GetSessionMessagesForAgentAsync(sessionId, cancellationToken: cancellationToken);
            if (!historyResult.IsSuccess || historyResult.Data == null) return;

            // Stored system rows (transfer results, hand-over notes) are bookkeeping for the screen, not conversation.
            var history = historyResult.Data.Where(m => m.Sender != ChatSenders.System).ToList();

            string reply;
            await _hub.Clients.Group(sessionId.ToString()).SendAsync("AgentTyping", "AI", cancellationToken);
            try
            {
                reply = await _ai.GetResponseAsync(history, cancellationToken);
            }
            finally
            {
                await _hub.Clients.Group(sessionId.ToString()).SendAsync("AgentStopTyping", "AI", CancellationToken.None);
            }

            var cardBuilt = false;

            if (AiActions.WantsBalances(reply) && ownerId.HasValue)
            {
                reply = await AnswerWithBalancesAsync(ownerId.Value, history, reply, cancellationToken);
            }
            else if (AiActions.WantsTransfer(reply) && ownerId.HasValue)
            {
                var proposal = AiActions.ParseTransfer(reply);
                if (proposal != null)
                {
                    // The only real marker the AI path can produce: built here from parsed fields.
                    reply = $"[CONFIRM_TRANSFER: source={proposal.Source}, destination={proposal.Destination}, amount={Money.Format(proposal.Amount)}, description={proposal.Description}]";
                    cardBuilt = true;
                }
                else
                {
                    reply = await AskForMissingTransferDetailsAsync(history, reply, cancellationToken);
                }
            }

            if (!cardBuilt) reply = ChatMarkers.Neutralize(reply);

            var stored = await _chat.AddMessageAsync(sessionId, ChatSenders.Ai, reply);
            if (stored.IsSuccess && stored.Data != null)
            {
                await _hub.Clients.Group(sessionId.ToString()).SendAsync("ReceiveMessage", stored.Data, cancellationToken);
            }
            else
            {
                _logger.LogInformation("The AI reply for session {SessionId} was not stored ({ErrorKey}).", sessionId, stored.ErrorKey);
            }
        }

        /// <summary>After a successful transfer from the chat: the AI says a few polite words about it.</summary>
        public async Task CommentOnTransferAsync(Guid sessionId, decimal amount, string source, string destination, CancellationToken cancellationToken = default)
        {
            var historyResult = await _chat.GetSessionMessagesForAgentAsync(sessionId, cancellationToken: cancellationToken);
            if (!historyResult.IsSuccess || historyResult.Data == null) return;

            var history = historyResult.Data.Where(m => m.Sender != ChatSenders.System).ToList();
            history.Add(new ChatMessageDto
            {
                Sender = ChatSenders.User,
                Content = string.Create(CultureInfo.InvariantCulture,
                    $"SYSTEM UPDATE: The transfer of {Money.Format(amount)} from {source} to {destination} succeeded. Please inform the user politely that the transfer has been completed successfully."),
                CreatedAt = DateTime.UtcNow
            });

            var reply = ChatMarkers.Neutralize(await _ai.GetResponseAsync(history, cancellationToken));
            var stored = await _chat.AddMessageAsync(sessionId, ChatSenders.Ai, reply);
            if (stored.IsSuccess && stored.Data != null)
            {
                await _hub.Clients.Group(sessionId.ToString()).SendAsync("ReceiveMessage", stored.Data, cancellationToken);
            }
        }

        private async Task<string> AnswerWithBalancesAsync(Guid ownerId, List<ChatMessageDto> history, string firstReply, CancellationToken cancellationToken)
        {
            var accounts = await _banking.GetAccountsAsync(ownerId, cancellationToken);

            var balanceContext = accounts.IsSuccess && accounts.Data is { Count: > 0 }
                ? "SYSTEM UPDATE: User's accounts and balances: " +
                  string.Join(", ", accounts.Data.Select(a => string.Create(CultureInfo.InvariantCulture, $"{a.AccountNumber} ({a.Currency}): {Money.Format(a.Balance)}")))
                : "SYSTEM UPDATE: User has no active bank accounts.";

            history.Add(new ChatMessageDto { Sender = ChatSenders.Ai, Content = "[ACTION:GET_BALANCES]", CreatedAt = DateTime.UtcNow });
            history.Add(new ChatMessageDto { Sender = ChatSenders.User, Content = balanceContext, CreatedAt = DateTime.UtcNow });

            var answer = await _ai.GetResponseAsync(history, cancellationToken);

            // The model may still be stubborn (or offline): then a canned answer.
            if (AiActions.WantsBalances(answer))
            {
                return history.Any(h => TurkishText.LooksTurkish(h.Content))
                    ? "Hesap bakiyelerinizi kontrol ettim ancak şu anda bilgilerinize erişilemiyor."
                    : "I checked your account balances, but your information is currently unavailable.";
            }

            return answer;
        }

        private async Task<string> AskForMissingTransferDetailsAsync(List<ChatMessageDto> history, string firstReply, CancellationToken cancellationToken)
        {
            history.Add(new ChatMessageDto { Sender = ChatSenders.Ai, Content = ChatMarkers.Neutralize(firstReply), CreatedAt = DateTime.UtcNow });
            history.Add(new ChatMessageDto
            {
                Sender = ChatSenders.User,
                Content = "SYSTEM CORRECTION: The transfer details you provided are incomplete or invalid (e.g. source, destination, or amount is missing or invalid). " +
                          "Please conversationally ask the user to provide the missing details (Source Account, Destination Account, and Amount) so you can proceed. " +
                          "Do not return [ACTION:TRANSFER] until you have all 3 details clearly.",
                CreatedAt = DateTime.UtcNow
            });

            var answer = await _ai.GetResponseAsync(history, cancellationToken);

            if (AiActions.WantsTransfer(answer))
            {
                return history.Any(h => TurkishText.LooksTurkish(h.Content))
                    ? "Para transferini gerçekleştirebilmem için lütfen kaynak hesap, alıcı hesap numarası ve transfer miktarını belirtir misiniz?"
                    : "To execute the transfer, please provide the source account, destination account, and amount.";
            }

            return answer;
        }
    }
}
