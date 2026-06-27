using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
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

            // Simple check: If the username contains "agent", they are considered a Support Agent
            // and can bypass ownership checks to inspect any session.
            var username = User.FindFirst(ClaimTypes.Name)?.Value;
            var isAgent = username != null && username.Contains("agent", StringComparison.OrdinalIgnoreCase);

            var result = await _chatService.GetSessionMessagesAsync(sessionId, isAgent ? null : userId);

            if (!result.IsSuccess)
            {
                return BadRequest(new { result.IsSuccess, result.ErrorKey, result.Message });
            }

            return Ok(result.Data);
        }

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

        [HttpPost("test-setup")]
        public async Task<IActionResult> TestSetup()
        {
            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            Guid? userId = Guid.TryParse(userIdStr, out var uId) ? uId : null;
            var username = User.FindFirst(ClaimTypes.Name)?.Value ?? "TestUser";

            var result = await _chatService.CreateSessionAsync(userId, "Test Session Setup");
            if (!result.IsSuccess || result.Data == null)
            {
                return BadRequest(new { Message = "Failed to create session." });
            }

            return Ok(new { id = result.Data.Id, username = username, title = result.Data.Title });
        }

        [HttpPost("test-ai/{sessionId}")]
        public async Task<IActionResult> TestAi(Guid sessionId, [FromBody] string query)
        {
            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            Guid? userId = Guid.TryParse(userIdStr, out var uId) ? uId : null;

            // Fetch history
            var historyResult = await _chatService.GetSessionMessagesAsync(sessionId, userId);
            if (!historyResult.IsSuccess || historyResult.Data == null)
            {
                return BadRequest(new { Message = "Failed to get history." });
            }

            var history = historyResult.Data;
            
            // Add user query to history
            var userMsg = new SmartBank.Core.DTOs.ChatMessageDto
            {
                Sender = "User",
                Content = query,
                CreatedAt = DateTime.UtcNow
            };
            history.Add(userMsg);

            // Trigger AI with failover
            var serviceProvider = HttpContext.RequestServices;
            var ollamaService = serviceProvider.GetRequiredService<SmartBank.Infrastructure.Services.OllamaService>();
            var geminiService = serviceProvider.GetRequiredService<SmartBank.Infrastructure.Services.GeminiService>();
            var ragService = serviceProvider.GetRequiredService<SmartBank.Core.Interfaces.IRAGService>();

            // Apply RAG
            string? faqAnswer = null;
            try
            {
                faqAnswer = await ragService.SearchFAQAsync(query);
                if (!string.IsNullOrWhiteSpace(faqAnswer))
                {
                    history.Add(new SmartBank.Core.DTOs.ChatMessageDto
                    {
                        Sender = "System",
                        Content = $"Relevant FAQ Context:\n{faqAnswer}",
                        CreatedAt = DateTime.UtcNow
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TestAi RAG Error] {ex.Message}");
            }

            string aiResponseText = "";
            try
            {
                aiResponseText = await ollamaService.GetResponseAsync(history).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TestAi Failover] Ollama failed/timeout. Trying Gemini... {ex.Message}");
                try
                {
                    aiResponseText = await geminiService.GetResponseAsync(history);
                }
                catch (Exception geminiEx)
                {
                    Console.WriteLine($"[TestAi Failover] Gemini failed. {geminiEx.Message}");
                    // Local fallback
                    var text = query.ToLowerInvariant();
                    bool isTurkish = text.Contains("merhaba") || 
                                     text.Contains("selam") || 
                                     text.Contains("nasıl") || 
                                     text.Contains("nasil") || 
                                     text.Contains("yardım") || 
                                     text.Contains("yardim") || 
                                     text.Contains("kredi") || 
                                     text.Contains("hesap") || 
                                     text.Contains("hesab") || 
                                     text.Contains("para") || 
                                     text.Contains("cek") || 
                                     text.Contains("çek") || 
                                     text.Contains("kart") || 
                                     text.Contains("bakiye") || 
                                     text.Contains("gönder") || 
                                     text.Contains("gonder") || 
                                     text.Contains("işlem") || 
                                     text.Contains("islem") || 
                                     text.Contains("destek") || 
                                     text.Contains("bağla") || 
                                     text.Contains("bagla") || 
                                     text.Contains("istiyorum");

                    aiResponseText = isTurkish 
                        ? "Şu anda yapay zeka servisimiz çevrimdışı. Sizi en kısa sürede canlı destek temsilcimize bağlayacağız."
                        : "Our AI support is currently offline. We will connect you to a live support representative shortly.";
                }
            }

            // Save user and AI message to db
            await _chatService.AddMessageAsync(sessionId, "User", query);
            var aiMsgResult = await _chatService.AddMessageAsync(sessionId, "AI", aiResponseText);

            return Ok(new { 
                aiResponse = new {
                    content = aiResponseText,
                    sender = "AI"
                }
            });
        }

        [HttpPost("messages/test-send-message/{sessionId}")]
        public async Task<IActionResult> TestSendMessage(Guid sessionId, [FromBody] string content)
        {
            return await TestAi(sessionId, content);
        }

        [HttpGet("test-rag")]
        public async Task<IActionResult> TestRag([FromQuery] string query)
        {
            var ragService = HttpContext.RequestServices.GetRequiredService<SmartBank.Core.Interfaces.IRAGService>();
            var result = await ragService.SearchFAQAsync(query);
            return Ok(new { Query = query, Match = result });
        }
    }
}
