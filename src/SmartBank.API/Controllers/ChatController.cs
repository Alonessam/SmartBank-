using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using SmartBank.API.Security;
using SmartBank.API.Hubs;
using SmartBank.Core.Common;
using SmartBank.Core.DTOs;
using SmartBank.Core.Entities;
using SmartBank.Core.Interfaces;

namespace SmartBank.API.Controllers
{
    /// <summary>
    /// The support chat over HTTP: the agent dashboard (queue, metrics, co-pilot, hand-over) and reading a conversation.
    /// A customer reaches only their own conversations; the agent endpoints need the Agent role (a claim in the token that
    /// only an administrator can grant). The live conversation itself runs over SignalR (see SupportHub).
    /// </summary>
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    [EnableRateLimiting(RateLimitPolicies.Chat)]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public class ChatController : SmartBankControllerBase
    {
        private static readonly Regex ActionTag = new(@"\[ACTION:[^\]]*\]", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

        private readonly IChatService _chatService;
        private readonly IAIChatbotService _aiChatbotService;
        private readonly IHubContext<SupportHub> _hubContext;

        public ChatController(IChatService chatService, IAIChatbotService aiChatbotService, IHubContext<SupportHub> hubContext)
        {
            _chatService = chatService;
            _aiChatbotService = aiChatbotService;
            _hubContext = hubContext;
        }

        /// <summary>The open conversations, newest first (at most 200). Agents only.</summary>
        [Authorize(Roles = RoleNames.Agent)]
        [HttpGet("active-sessions")]
        [ProducesResponseType(typeof(List<ChatSessionDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> GetActiveSessions(CancellationToken cancellationToken)
            => ToActionResult(await _chatService.GetActiveSessionsAsync(cancellationToken: cancellationToken));

        /// <summary>
        /// The messages of a conversation (the newest 500). Customers may read only their own conversations, support agents
        /// any. This used to be "the username contains 'agent'", which anyone could satisfy by registering such a name.
        /// </summary>
        [HttpGet("messages/{sessionId}")]
        [ProducesResponseType(typeof(List<ChatMessageDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetSessionMessages(Guid sessionId, CancellationToken cancellationToken)
        {
            var result = User.IsInRole(RoleNames.Agent)
                ? await _chatService.GetSessionMessagesForAgentAsync(sessionId, cancellationToken: cancellationToken)
                : await _chatService.GetSessionMessagesForOwnerAsync(sessionId, GetUserId(), cancellationToken: cancellationToken);

            return ToActionResult(result);
        }

        /// <summary>A suggested reply for the agent, written by the AI from the conversation. Agents only.</summary>
        [Authorize(Roles = RoleNames.Agent)]
        [HttpGet("suggest-response/{sessionId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> SuggestResponse(Guid sessionId, CancellationToken cancellationToken)
        {
            var messagesResult = await _chatService.GetSessionMessagesForAgentAsync(sessionId, cancellationToken: cancellationToken);
            if (!messagesResult.IsSuccess || messagesResult.Data == null)
            {
                return ToActionResult(messagesResult);
            }

            // Stored system rows are bookkeeping, not conversation.
            var history = messagesResult.Data.Where(m => m.Sender != ChatSenders.System).ToList();
            if (history.Count == 0)
            {
                return Ok(new { Suggestion = "Hello! How can I help you today?" });
            }

            history.Add(new ChatMessageDto
            {
                Sender = ChatSenders.User,
                Content = "SYSTEM INSTRUCTION: You are a support agent's AI Co-Pilot assistant. " +
                          "Analyze the conversation above and suggest a single, concise response in the customer's language " +
                          "that the human support representative can send. Do NOT include action tags like [ACTION:...]. " +
                          "Provide ONLY the response text itself, ready to be copied and pasted."
            });

            var suggestion = await _aiChatbotService.GetResponseAsync(history, cancellationToken);

            // Clean the suggestion from action tags if any leaked.
            suggestion = ActionTag.Replace(suggestion, string.Empty).Trim();

            return Ok(new { Suggestion = suggestion });
        }

        /// <summary>Dashboard numbers: conversations resolved, and the mean time to the first agent reply (null while nothing has been answered). Agents only.</summary>
        [Authorize(Roles = RoleNames.Agent)]
        [HttpGet("agent-metrics")]
        [ProducesResponseType(typeof(AgentMetricsDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> GetAgentMetrics(CancellationToken cancellationToken)
            => ToActionResult(await _chatService.GetAgentMetricsAsync(cancellationToken));

        /// <summary>Hands a conversation to another department (a notice is added and pushed to the conversation). Agents only.</summary>
        [Authorize(Roles = RoleNames.Agent)]
        [HttpPost("transfer-session/{sessionId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> TransferSession(Guid sessionId, [FromBody] TransferSessionDto transferDto)
        {
            var result = await _chatService.TransferSessionAsync(sessionId, transferDto.Department);
            if (!result.IsSuccess)
            {
                return ToActionResult(result);
            }

            // The hand-over notice is the newest message: push it to everybody in the conversation.
            var latest = await _chatService.GetSessionMessagesForAgentAsync(sessionId, take: 1);
            if (latest.IsSuccess && latest.Data is { Count: > 0 })
            {
                await _hubContext.Clients.Group(sessionId.ToString()).SendAsync("ReceiveMessage", latest.Data[^1]);
            }

            return Ok(new { Success = true });
        }
    }
}
