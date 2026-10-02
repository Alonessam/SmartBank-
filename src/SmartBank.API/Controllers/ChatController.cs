using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using SmartBank.Core.Entities;
using SmartBank.Core.Interfaces;

namespace SmartBank.API.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class ChatController : ControllerBase
    {
        private readonly IChatService _chatService;
        private readonly IAIChatbotService _aiChatbotService;

        public ChatController(IChatService chatService, IAIChatbotService aiChatbotService)
        {
            _chatService = chatService;
            _aiChatbotService = aiChatbotService;
        }

        [Authorize(Roles = RoleNames.Agent)]
        [HttpGet("active-sessions")]
        public async Task<IActionResult> GetActiveSessions()
        {
            // Lists all active chat sessions (called by support agents)
            var result = await _chatService.GetActiveSessionsAsync();
            return Ok(result.Data);
        }

        [HttpGet("messages/{sessionId}")]
        public async Task<IActionResult> GetSessionMessages(Guid sessionId)
        {
            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            Guid? userId = Guid.TryParse(userIdStr, out var uId) ? uId : null;

            // Customers may read only their own sessions; support agents (a role in the token that only an
            // administrator can grant) may read any. This used to be "the username contains 'agent'", which anyone
            // could satisfy by registering such a name.
            var isAgent = User.IsInRole(RoleNames.Agent);

            var result = await _chatService.GetSessionMessagesAsync(sessionId, isAgent ? null : userId);

            if (!result.IsSuccess)
            {
                return BadRequest(new { result.IsSuccess, result.ErrorKey, result.Message });
            }

            return Ok(result.Data);
        }

        [Authorize(Roles = RoleNames.Agent)]
        [HttpGet("suggest-response/{sessionId}")]
        public async Task<IActionResult> SuggestResponse(Guid sessionId)
        {
            var messagesResult = await _chatService.GetSessionMessagesAsync(sessionId, null);
            if (!messagesResult.IsSuccess || messagesResult.Data == null)
            {
                return BadRequest(new { messagesResult.IsSuccess, messagesResult.ErrorKey, messagesResult.Message });
            }

            var history = messagesResult.Data;
            if (history.Count == 0)
            {
                return Ok(new { Suggestion = "Hello! How can I help you today?" });
            }

            var copilotHistory = new System.Collections.Generic.List<SmartBank.Core.DTOs.ChatMessageDto>();
            foreach (var m in history)
            {
                copilotHistory.Add(new SmartBank.Core.DTOs.ChatMessageDto
                {
                    Sender = m.Sender,
                    Content = m.Content
                });
            }

            copilotHistory.Add(new SmartBank.Core.DTOs.ChatMessageDto
            {
                Sender = "User",
                Content = "SYSTEM INSTRUCTION: You are a support agent's AI Co-Pilot assistant. " +
                          "Analyze the conversation above and suggest a single, concise response in the customer's language " +
                          "that the human support representative can send. Do NOT include action tags like [ACTION:...]. " +
                          "Provide ONLY the response text itself, ready to be copied and pasted."
            });

            var suggestionText = await _aiChatbotService.GetResponseAsync(copilotHistory);
            
            // Clean suggestion from action tags if any leaked
            suggestionText = System.Text.RegularExpressions.Regex.Replace(suggestionText, @"\[ACTION:[^\]]*\]", "").Trim();

            return Ok(new { Suggestion = suggestionText });
        }

        [Authorize(Roles = RoleNames.Agent)]
        [HttpGet("agent-metrics")]
        public async Task<IActionResult> GetAgentMetrics()
        {
            var result = await _chatService.GetAgentMetricsAsync();
            if (!result.IsSuccess)
            {
                return BadRequest(new { result.IsSuccess, result.ErrorKey, result.Message });
            }
            return Ok(result.Data);
        }

        [Authorize(Roles = RoleNames.Agent)]
        [HttpPost("transfer-session/{sessionId}")]
        public async Task<IActionResult> TransferSession(Guid sessionId, [FromBody] SmartBank.Core.DTOs.TransferSessionDto transferDto)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var result = await _chatService.TransferSessionAsync(sessionId, transferDto.Department);
            if (!result.IsSuccess)
            {
                return BadRequest(new { result.IsSuccess, result.ErrorKey, result.Message });
            }

            // Broadcast the transfer notification to the session group via SignalR
            var hubContext = HttpContext.RequestServices.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<SmartBank.API.Hubs.SupportHub>>();
            
            // The transfer system message is the last message in history, fetch it to broadcast
            var messages = await _chatService.GetSessionMessagesAsync(sessionId, null);
            if (messages.IsSuccess && messages.Data != null && messages.Data.Count > 0)
            {
                var systemMsg = messages.Data[^1];
                await hubContext.Clients.Group(sessionId.ToString()).SendAsync("ReceiveMessage", systemMsg);
            }

            return Ok(new { Success = true });
        }
    }
}
