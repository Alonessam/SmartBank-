using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SmartBank.Core.Common;
using SmartBank.Core.DTOs;
using SmartBank.Core.Entities;
using SmartBank.Core.Interfaces;
using SmartBank.Infrastructure.Data;

namespace SmartBank.Infrastructure.Services
{
    public class ChatService : IChatService
    {
        public const int MaxSessionsListed = 500;
        public const int MaxMessagesReturned = 1000;
        private const int SessionsForMetrics = 200;

        // A department name goes into a message the web app parses ("[SESSION_TRANSFERRED: to=...]"): no brackets or line breaks.
        private static readonly Regex DepartmentPattern = new(@"^[^\[\]\r\n]{1,100}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly string[] KnownSenders = { ChatSenders.User, ChatSenders.Ai, ChatSenders.Agent, ChatSenders.System };

        private readonly SmartBankDbContext _context;
        private readonly TimeProvider _time;

        public ChatService(SmartBankDbContext context, TimeProvider? timeProvider = null)
        {
            _context = context;
            _time = timeProvider ?? TimeProvider.System;
        }

        private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

        public async Task<ServiceResult<ChatSessionDto>> CreateSessionAsync(Guid? userId, string title)
        {
            title = (title ?? string.Empty).Trim();
            if (title.Length > ChatLimits.MaxTitleLength) title = title[..ChatLimits.MaxTitleLength];

            var session = new ChatSession
            {
                UserId = userId,
                Title = string.IsNullOrEmpty(title) ? "Destek Sohbeti" : title,
                IsActive = true,
                CreatedAt = UtcNow
            };

            _context.ChatSessions.Add(session);
            await _context.SaveChangesAsync();

            var username = "Guest";
            if (userId.HasValue)
            {
                username = await _context.Users.AsNoTracking().Where(u => u.Id == userId.Value).Select(u => u.Username).FirstOrDefaultAsync() ?? username;
            }

            return ServiceResult<ChatSessionDto>.Success(new ChatSessionDto
            {
                Id = session.Id,
                UserId = session.UserId,
                Username = username,
                Title = session.Title,
                IsActive = session.IsActive,
                CreatedAt = session.CreatedAt
            });
        }

        public async Task<ServiceResult<ChatMessageDto>> AddMessageAsync(Guid sessionId, string sender, string content)
        {
            if (!KnownSenders.Contains(sender))
            {
                return ServiceResult<ChatMessageDto>.Failure("InvalidSender", "Unknown message sender.");
            }

            var session = await _context.ChatSessions.AsNoTracking()
                .Where(s => s.Id == sessionId)
                .Select(s => new { s.IsActive })
                .FirstOrDefaultAsync();

            if (session == null)
            {
                return ServiceResult<ChatMessageDto>.Failure("SessionNotFound", "Chat session was not found.");
            }

            // A closed conversation takes no more messages (and so triggers no more AI calls).
            if (!session.IsActive)
            {
                return ServiceResult<ChatMessageDto>.Failure("SessionClosed", "This chat session has been closed.");
            }

            // A person must not be able to type a machine marker (a fake "transfer confirmation" card, for instance).
            if (sender is ChatSenders.User or ChatSenders.Agent)
            {
                content = ChatMarkers.Neutralize(content);
            }

            var message = new ChatMessage
            {
                SessionId = sessionId,
                Sender = sender,
                Content = content,
                CreatedAt = UtcNow
            };

            _context.ChatMessages.Add(message);
            await _context.SaveChangesAsync();

            return ServiceResult<ChatMessageDto>.Success(ToDto(message));
        }

        public async Task<ServiceResult<List<ChatSessionDto>>> GetActiveSessionsAsync(int take = 200, CancellationToken cancellationToken = default)
        {
            take = Math.Clamp(take, 1, MaxSessionsListed);

            var activeSessions = await _context.ChatSessions
                .AsNoTracking()
                .Where(s => s.IsActive)
                .OrderByDescending(s => s.CreatedAt)
                .Take(take)
                .Select(s => new ChatSessionDto
                {
                    Id = s.Id,
                    UserId = s.UserId,
                    Username = s.User != null ? s.User.Username : "Guest",
                    Title = s.Title,
                    IsActive = s.IsActive,
                    CreatedAt = s.CreatedAt
                })
                .ToListAsync(cancellationToken);

            return ServiceResult<List<ChatSessionDto>>.Success(activeSessions);
        }

        public async Task<Guid?> GetSessionOwnerIdAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            await _context.ChatSessions.AsNoTracking().Where(s => s.Id == sessionId).Select(s => s.UserId).FirstOrDefaultAsync(cancellationToken);

        public Task<bool> IsSessionOwnerAsync(Guid sessionId, Guid userId, CancellationToken cancellationToken = default) =>
            _context.ChatSessions.AsNoTracking().AnyAsync(s => s.Id == sessionId && s.UserId == userId, cancellationToken);

        public async Task<ServiceResult<List<ChatMessageDto>>> GetSessionMessagesForOwnerAsync(Guid sessionId, Guid userId, int take = 500, CancellationToken cancellationToken = default)
        {
            var session = await _context.ChatSessions.AsNoTracking()
                .Where(s => s.Id == sessionId)
                .Select(s => new { s.UserId })
                .FirstOrDefaultAsync(cancellationToken);

            if (session == null)
            {
                return ServiceResult<List<ChatMessageDto>>.Failure("SessionNotFound", "Chat session was not found.");
            }

            // A session without an owner belongs to nobody: a customer may not read it either.
            if (session.UserId != userId)
            {
                return ServiceResult<List<ChatMessageDto>>.Failure("UnauthorizedSessionAccess", "Access denied to this chat session.");
            }

            return ServiceResult<List<ChatMessageDto>>.Success(await ReadMessagesAsync(sessionId, take, cancellationToken));
        }

        public async Task<ServiceResult<List<ChatMessageDto>>> GetSessionMessagesForAgentAsync(Guid sessionId, int take = 500, CancellationToken cancellationToken = default)
        {
            if (!await _context.ChatSessions.AsNoTracking().AnyAsync(s => s.Id == sessionId, cancellationToken))
            {
                return ServiceResult<List<ChatMessageDto>>.Failure("SessionNotFound", "Chat session was not found.");
            }

            return ServiceResult<List<ChatMessageDto>>.Success(await ReadMessagesAsync(sessionId, take, cancellationToken));
        }

        /// <summary>The newest <paramref name="take"/> messages, in the order they were written.</summary>
        private async Task<List<ChatMessageDto>> ReadMessagesAsync(Guid sessionId, int take, CancellationToken cancellationToken)
        {
            take = Math.Clamp(take, 1, MaxMessagesReturned);

            var newestFirst = await _context.ChatMessages
                .AsNoTracking()
                .Where(m => m.SessionId == sessionId)
                .OrderByDescending(m => m.CreatedAt)
                .Take(take)
                .Select(m => new ChatMessageDto { Id = m.Id, Sender = m.Sender, Content = m.Content, CreatedAt = m.CreatedAt })
                .ToListAsync(cancellationToken);

            newestFirst.Reverse();
            return newestFirst;
        }

        public async Task<ServiceResult<bool>> CloseSessionAsync(Guid sessionId)
        {
            var session = await _context.ChatSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
            if (session == null)
            {
                return ServiceResult<bool>.Failure("SessionNotFound", "Chat session was not found.");
            }

            session.IsActive = false;
            await _context.SaveChangesAsync();

            return ServiceResult<bool>.Success(true);
        }

        public async Task<ServiceResult<AgentMetricsDto>> GetAgentMetricsAsync(CancellationToken cancellationToken = default)
        {
            var resolvedCount = await _context.ChatSessions.AsNoTracking().CountAsync(s => !s.IsActive, cancellationToken);

            // Response time: from a customer message that waits for an answer to the next agent message in the same
            // session (further customer messages in between do not restart the clock). Looked at over the newest sessions.
            var sessionIds = await _context.ChatSessions.AsNoTracking()
                .OrderByDescending(s => s.CreatedAt)
                .Take(SessionsForMetrics)
                .Select(s => s.Id)
                .ToListAsync(cancellationToken);

            var messages = await _context.ChatMessages.AsNoTracking()
                .Where(m => sessionIds.Contains(m.SessionId) && (m.Sender == ChatSenders.User || m.Sender == ChatSenders.Agent))
                .OrderBy(m => m.SessionId).ThenBy(m => m.CreatedAt)
                .Select(m => new { m.SessionId, m.Sender, m.CreatedAt })
                .ToListAsync(cancellationToken);

            var waits = new List<double>();
            Guid? currentSession = null;
            DateTime? waitingSince = null;
            foreach (var m in messages)
            {
                if (currentSession != m.SessionId)
                {
                    currentSession = m.SessionId;
                    waitingSince = null;
                }

                if (m.Sender == ChatSenders.User)
                {
                    waitingSince ??= m.CreatedAt;
                }
                else if (waitingSince.HasValue)
                {
                    waits.Add((m.CreatedAt - waitingSince.Value).TotalSeconds);
                    waitingSince = null;
                }
            }

            return ServiceResult<AgentMetricsDto>.Success(new AgentMetricsDto
            {
                ResolvedCount = resolvedCount,
                AvgResponseTime = waits.Count == 0 ? null : Math.Round(waits.Average()).ToString("0", CultureInfo.InvariantCulture) + "s",
                CsatScore = null // there is no satisfaction survey, so there is nothing to report
            });
        }

        public async Task<ServiceResult<bool>> TransferSessionAsync(Guid sessionId, string department)
        {
            department = (department ?? string.Empty).Trim();
            if (!DepartmentPattern.IsMatch(department))
            {
                return ServiceResult<bool>.Failure("InvalidDepartment", "Department must be 1 to 100 characters without brackets or line breaks.");
            }

            var session = await _context.ChatSessions.AsNoTracking().Where(s => s.Id == sessionId).Select(s => new { s.IsActive }).FirstOrDefaultAsync();
            if (session == null)
            {
                return ServiceResult<bool>.Failure("SessionNotFound", "Chat session was not found.");
            }

            if (!session.IsActive)
            {
                return ServiceResult<bool>.Failure("SessionClosed", "This chat session has been closed.");
            }

            // The customer's own title stays; a system message tells everyone where the conversation went.
            _context.ChatMessages.Add(new ChatMessage
            {
                SessionId = sessionId,
                Sender = ChatSenders.System,
                Content = $"[SESSION_TRANSFERRED: to={department}]",
                CreatedAt = UtcNow
            });

            await _context.SaveChangesAsync();

            return ServiceResult<bool>.Success(true);
        }

        private static ChatMessageDto ToDto(ChatMessage message) => new()
        {
            Id = message.Id,
            Sender = message.Sender,
            Content = message.Content,
            CreatedAt = message.CreatedAt
        };
    }
}
