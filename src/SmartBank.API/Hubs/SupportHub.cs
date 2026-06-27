using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using SmartBank.Core.Interfaces;
using System.Linq;
using System.Text.RegularExpressions;
using SmartBank.Core.DTOs;
using SmartBank.Infrastructure.Data;
using SmartBank.Infrastructure.Services;

namespace SmartBank.API.Hubs
{
    [Authorize]
    public class SupportHub : Hub
    {
        private readonly IChatService _chatService;
        private readonly IAIChatbotService _aiChatbotService;
        private readonly IHubContext<SupportHub> _hubContext;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly IBankingService _bankingService;

        public SupportHub(
            IChatService chatService, 
            IAIChatbotService aiChatbotService, 
            IHubContext<SupportHub> _hubContext,
            IServiceScopeFactory serviceScopeFactory,
            IBankingService bankingService)
        {
            _chatService = chatService;
            _aiChatbotService = aiChatbotService;
            this._hubContext = _hubContext;
            _serviceScopeFactory = serviceScopeFactory;
            _bankingService = bankingService;
        }

        public async Task StartSessionAsync(string title)
        {
            var userId = GetUserId();
            
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
            // Add connection to the session group
            await Groups.AddToGroupAsync(Context.ConnectionId, sessionId.ToString());
            
            // If the connector is an agent, we can also register them to the group
            // For now, we announce they joined the room
            var username = Context.User?.FindFirst(ClaimTypes.Name)?.Value ?? "Support Agent";
            await Clients.Group(sessionId.ToString()).SendAsync("UserJoined", new { username, sessionId });
        }

        public async Task SendMessageAsync(Guid sessionId, string content)
        {
            var userId = GetUserId();
            var username = Context.User?.FindFirst(ClaimTypes.Name)?.Value ?? "Guest";

            // Determine if the sender is the original user or an agent
            // We retrieve messages to check the owner of the session, or simply check the session creator ID
            var messages = await _chatService.GetSessionMessagesAsync(sessionId, userId);
            
            string senderRole = "Agent";
            if (messages.IsSuccess)
            {
                // Access allowed for owner -> sender is the "User"
                senderRole = "User";
            }

            var result = await _chatService.AddMessageAsync(sessionId, senderRole, content);

            if (result.IsSuccess && result.Data != null)
            {
                var messageDto = result.Data;
                // Broadcast message to everyone in the session group (User + Agent)
                await Clients.Group(sessionId.ToString()).SendAsync("ReceiveMessage", messageDto);

                // If the message was sent by the User, trigger the AI response in a background thread
                if (senderRole == "User")
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using (var scope = _serviceScopeFactory.CreateScope())
                            {
                                var scopedChatService = scope.ServiceProvider.GetRequiredService<IChatService>();
                                var scopedAiService = scope.ServiceProvider.GetRequiredService<IAIChatbotService>();
                                var dbContext = scope.ServiceProvider.GetRequiredService<SmartBankDbContext>();

                                // Fetch the session from the DB to get the UserId
                                var session = await dbContext.ChatSessions.FindAsync(sessionId);
                                Guid? sessionUserId = session?.UserId;

                                // Fetch message history for AI context
                                var historyResult = await scopedChatService.GetSessionMessagesAsync(sessionId, null);
                                if (historyResult.IsSuccess && historyResult.Data != null)
                                {
                                    var history = historyResult.Data;

                                    // Notify group that AI is typing
                                    await _hubContext.Clients.Group(sessionId.ToString()).SendAsync("AgentTyping", "AI");

                                    var aiResponseText = "";
                                    try
                                    {
                                        aiResponseText = await GenerateAIResponseWithFailoverAsync(scope.ServiceProvider, history);
                                    }
                                    finally
                                    {
                                        // Notify group that AI stopped typing
                                        await _hubContext.Clients.Group(sessionId.ToString()).SendAsync("AgentStopTyping", "AI");
                                    }

                                    // Parse AI Actions
                                    bool isGetBalances = aiResponseText.Contains("ACTION:GET_BALANCES", StringComparison.OrdinalIgnoreCase);
                                    bool isTransferAction = aiResponseText.Contains("ACTION:TRANSFER", StringComparison.OrdinalIgnoreCase);

                                    if (isGetBalances && sessionUserId.HasValue)
                                    {
                                        var bankingService = scope.ServiceProvider.GetRequiredService<IBankingService>();
                                        var accountsResult = await bankingService.GetAccountsAsync(sessionUserId.Value);
                                        
                                        string balanceContext = "";
                                        if (accountsResult.IsSuccess && accountsResult.Data != null && accountsResult.Data.Count > 0)
                                        {
                                            balanceContext = "SYSTEM UPDATE: User's accounts and balances: " + 
                                                             string.Join(", ", accountsResult.Data.Select(a => $"{a.AccountNumber} ({a.Currency}): {a.Balance}"));
                                        }
                                        else
                                        {
                                            balanceContext = "SYSTEM UPDATE: User has no active bank accounts.";
                                        }

                                        // Append system context to history and call AI again
                                        history.Add(new ChatMessageDto
                                        {
                                            Sender = "AI",
                                            Content = "[ACTION:GET_BALANCES]",
                                            CreatedAt = DateTime.UtcNow
                                        });
                                        history.Add(new ChatMessageDto
                                        {
                                            Sender = "User",
                                            Content = balanceContext,
                                            CreatedAt = DateTime.UtcNow
                                        });

                                        aiResponseText = await GenerateAIResponseWithFailoverAsync(scope.ServiceProvider, history);
                                        
                                        // In case the AI still stubborn or offline, check for raw action
                                        if (aiResponseText.Contains("ACTION:GET_BALANCES", StringComparison.OrdinalIgnoreCase))
                                        {
                                            bool isTurkish = history.Any(h => h.Content.Contains("merhaba", StringComparison.OrdinalIgnoreCase) || h.Content.Contains("hesab", StringComparison.OrdinalIgnoreCase) || h.Content.Contains("bakiye", StringComparison.OrdinalIgnoreCase));
                                            aiResponseText = isTurkish 
                                                ? "Hesap bakiyelerinizi kontrol ettim ancak şu anda bilgilerinize erişilemiyor."
                                                : "I checked your account balances, but your information is currently unavailable.";
                                        }
                                    }
                                    else if (isTransferAction && sessionUserId.HasValue)
                                    {
                                        var sourceMatch = Regex.Match(aiResponseText, @"source\s*:\s*([^,\]]*)", RegexOptions.IgnoreCase);
                                        var destMatch = Regex.Match(aiResponseText, @"destination\s*:\s*([^,\]]*)", RegexOptions.IgnoreCase);
                                        var amountMatch = Regex.Match(aiResponseText, @"amount\s*:\s*([^,\]]*)", RegexOptions.IgnoreCase);
                                        var descMatch = Regex.Match(aiResponseText, @"description\s*:\s*([^,\]]*)", RegexOptions.IgnoreCase);

                                        var source = sourceMatch.Success ? sourceMatch.Groups[1].Value.Trim() : "";
                                        var destination = destMatch.Success ? destMatch.Groups[1].Value.Trim() : "";
                                        var amountStr = amountMatch.Success ? amountMatch.Groups[1].Value.Trim() : "";
                                        var description = descMatch.Success ? descMatch.Groups[1].Value.Trim() : "AI Support Transfer";

                                        source = CleanValue(source);
                                        destination = CleanValue(destination);
                                        amountStr = CleanValue(amountStr);
                                        description = CleanValue(description);

                                        if (!string.IsNullOrEmpty(source) && !string.IsNullOrEmpty(destination) && decimal.TryParse(amountStr, out var amount) && amount > 0)
                                        {
                                            aiResponseText = $"[CONFIRM_TRANSFER: source={source}, destination={destination}, amount={amount}, description={description}]";
                                        }
                                        else
                                        {
                                            // Incomplete or invalid details. Re-prompt AI conversationally.
                                            history.Add(new ChatMessageDto
                                            {
                                                Sender = "AI",
                                                Content = aiResponseText,
                                                CreatedAt = DateTime.UtcNow
                                            });
                                            history.Add(new ChatMessageDto
                                            {
                                                Sender = "User",
                                                Content = "SYSTEM CORRECTION: The transfer details you provided are incomplete or invalid (e.g. source, destination, or amount is missing or invalid). Please conversationally ask the user to provide the missing details (Source Account, Destination Account, and Amount) so you can proceed. Do not return [ACTION:TRANSFER] until you have all 3 details clearly.",
                                                CreatedAt = DateTime.UtcNow
                                            });

                                            aiResponseText = await GenerateAIResponseWithFailoverAsync(scope.ServiceProvider, history);

                                            if (aiResponseText.Contains("ACTION:TRANSFER", StringComparison.OrdinalIgnoreCase))
                                            {
                                                bool isTurkish = history.Any(h => h.Content.Contains("merhaba", StringComparison.OrdinalIgnoreCase) || h.Content.Contains("para", StringComparison.OrdinalIgnoreCase) || h.Content.Contains("gönder", StringComparison.OrdinalIgnoreCase));
                                                aiResponseText = isTurkish 
                                                    ? "Para transferini gerçekleştirebilmem için lütfen kaynak hesap, alıcı hesap numarası ve transfer miktarını belirtir misiniz?"
                                                    : "To execute the transfer, please provide the source account, destination account, and amount.";
                                            }
                                        }
                                    }

                                    var aiMsgResult = await scopedChatService.AddMessageAsync(sessionId, "AI", aiResponseText);
                                    if (aiMsgResult.IsSuccess && aiMsgResult.Data != null)
                                    {
                                        // Use IHubContext to safely broadcast the AI reply back to the group
                                        await _hubContext.Clients.Group(sessionId.ToString()).SendAsync("ReceiveMessage", aiMsgResult.Data);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[SupportHub AI Error] {ex}");
                        }
                    });
                }
            }
            else
            {
                await Clients.Caller.SendAsync("Error", "Could not send message.");
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

            // Verify session access
            var sessionCheck = await _chatService.GetSessionMessagesAsync(sessionId, userId);
            if (!sessionCheck.IsSuccess)
            {
                await Clients.Caller.SendAsync("Error", "Access denied to this chat session.");
                return;
            }

            var request = new TransferRequestDto
            {
                SourceAccountNumber = source,
                DestinationAccountNumber = destination,
                Amount = amount,
                Description = description,
                OtpCode = otpCode
            };

            var transferResult = await _bankingService.TransferMoneyAsync(userId.Value, request);

            if (transferResult.IsSuccess && transferResult.Data != null)
            {
                var tx = transferResult.Data;
                // Add a system success message to the chat
                var msgText = $"[TRANSFER_SUCCESS: source={tx.SourceAccountNumber}, destination={tx.DestinationAccountNumber}, amount={tx.Amount}, description={tx.Description}]";
                var addMsgResult = await _chatService.AddMessageAsync(sessionId, "System", msgText);

                if (addMsgResult.IsSuccess && addMsgResult.Data != null)
                {
                    await Clients.Group(sessionId.ToString()).SendAsync("ReceiveMessage", addMsgResult.Data);
                }

                // Call AI to comment on the successful transfer and output it
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using (var scope = _serviceScopeFactory.CreateScope())
                        {
                            var scopedChatService = scope.ServiceProvider.GetRequiredService<IChatService>();
                            var scopedAiService = scope.ServiceProvider.GetRequiredService<IAIChatbotService>();

                            var historyResult = await scopedChatService.GetSessionMessagesAsync(sessionId, null);
                            if (historyResult.IsSuccess && historyResult.Data != null)
                            {
                                var history = historyResult.Data;
                                history.Add(new ChatMessageDto
                                {
                                    Sender = "User",
                                    Content = $"SYSTEM UPDATE: The transfer of {amount} TRY from {source} to {destination} succeeded. Please inform the user politely that the transfer has been completed successfully.",
                                    CreatedAt = DateTime.UtcNow
                                });

                                var aiResponseText = await GenerateAIResponseWithFailoverAsync(scope.ServiceProvider, history);
                                var aiMsgResult = await scopedChatService.AddMessageAsync(sessionId, "AI", aiResponseText);
                                if (aiMsgResult.IsSuccess && aiMsgResult.Data != null)
                                {
                                    await _hubContext.Clients.Group(sessionId.ToString()).SendAsync("ReceiveMessage", aiMsgResult.Data);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SupportHub AI Transfer Success Comment Error] {ex}");
                    }
                });
            }
            else
            {
                // Add a system failure message
                var errorKey = transferResult.ErrorKey ?? "TransferFailed";
                var errorMsg = transferResult.Message ?? "Transfer failed.";
                var msgText = $"[TRANSFER_FAILED: errorKey={errorKey}, message={errorMsg}]";
                
                var addMsgResult = await _chatService.AddMessageAsync(sessionId, "System", msgText);
                if (addMsgResult.IsSuccess && addMsgResult.Data != null)
                {
                    await Clients.Group(sessionId.ToString()).SendAsync("ReceiveMessage", addMsgResult.Data);
                }
            }
        }

        public async Task CloseSessionAsync(Guid sessionId)
        {
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

        private async Task<string> GenerateAIResponseWithFailoverAsync(IServiceProvider serviceProvider, List<ChatMessageDto> history)
        {
            var ollamaService = serviceProvider.GetRequiredService<OllamaService>();
            var geminiService = serviceProvider.GetRequiredService<GeminiService>();
            var ragService = serviceProvider.GetRequiredService<IRAGService>();

            // 1. Run RAG FAQ Search if last message is from User
            var lastUserMessage = history.LastOrDefault(m => m.Sender.Equals("User", StringComparison.OrdinalIgnoreCase))?.Content;
            if (!string.IsNullOrWhiteSpace(lastUserMessage))
            {
                try
                {
                    var faqAnswer = await ragService.SearchFAQAsync(lastUserMessage);
                    if (!string.IsNullOrWhiteSpace(faqAnswer))
                    {
                        history.Add(new ChatMessageDto
                        {
                            Sender = "System",
                            Content = $"Relevant FAQ Context:\n{faqAnswer}",
                            CreatedAt = DateTime.UtcNow
                        });
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SupportHub RAG Error] {ex.Message}");
                }
            }

            // 2. Call Ollama with 5-second timeout, falling back to Gemini
            try
            {
                if (await ollamaService.IsAvailableAsync())
                {
                    return await ollamaService.GetResponseAsync(history).WaitAsync(TimeSpan.FromSeconds(5));
                }
                else
                {
                    Console.WriteLine("[SupportHub Failover] Ollama is offline. Falling back to Gemini API immediately.");
                    return await geminiService.GetResponseAsync(history);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SupportHub Failover] Ollama failed or timed out during generation. Trying Gemini API... Error: {ex.Message}");
                try
                {
                    return await geminiService.GetResponseAsync(history);
                }
                catch (Exception geminiEx)
                {
                    Console.WriteLine($"[SupportHub Failover] Gemini API also failed. Error: {geminiEx.Message}");
                    
                    // Final localized fallback
                    var text = (lastUserMessage ?? "").ToLowerInvariant();
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

                    return isTurkish 
                        ? "Şu anda yapay zeka servisimiz çevrimdışı. Sizi en kısa sürede canlı destek temsilcimize bağlayacağız."
                        : "Our AI support is currently offline. We will connect you to a live support representative shortly.";
                }
            }
        }

        // Agents call this method to connect to the general agents feed
        public async Task RegisterAgentAsync()
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, "Agents");
            await Clients.Caller.SendAsync("AgentRegistered");
        }

        private static string CleanValue(string val)
        {
            if (string.IsNullOrEmpty(val)) return "";
            val = val.Trim();
            // Remove any trailing commas, semicolons, or brackets
            val = val.TrimEnd(',', ';', ']', '}');
            // Remove any leading/trailing quotes or braces
            val = val.Trim('"', '\'', '{', '}');
            return val.Trim();
        }

        private Guid? GetUserId()
        {
            var userIdStr = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return Guid.TryParse(userIdStr, out var userId) ? userId : null;
        }
    }
}
