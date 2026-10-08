using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SmartBank.Core.Common;
using SmartBank.Core.DTOs;

namespace SmartBank.Core.Interfaces
{
    public interface IChatService
    {
        Task<ServiceResult<ChatSessionDto>> CreateSessionAsync(Guid? userId, string title);

        /// <summary>Stores a message. A closed session accepts nothing more (error key SessionClosed).</summary>
        Task<ServiceResult<ChatMessageDto>> AddMessageAsync(Guid sessionId, string sender, string content);

        /// <summary>The newest <paramref name="take"/> open sessions, newest first. For support agents.</summary>
        Task<ServiceResult<List<ChatSessionDto>>> GetActiveSessionsAsync(int take = 200, CancellationToken cancellationToken = default);

        /// <summary>The user who started the session; null when it does not exist or has no owner (for the server's own AI replies).</summary>
        Task<Guid?> GetSessionOwnerIdAsync(Guid sessionId, CancellationToken cancellationToken = default);

        /// <summary>True only when the session exists and was started by this user. Sessions without an owner belong to nobody.</summary>
        Task<bool> IsSessionOwnerAsync(Guid sessionId, Guid userId, CancellationToken cancellationToken = default);

        /// <summary>True while the session exists and has not been closed (a closed conversation accepts no messages and no transfer confirmations).</summary>
        Task<bool> IsSessionOpenAsync(Guid sessionId, CancellationToken cancellationToken = default);

        /// <summary>Messages of a session the caller owns (SessionNotFound or UnauthorizedSessionAccess otherwise). Newest <paramref name="take"/>, oldest first.</summary>
        Task<ServiceResult<List<ChatMessageDto>>> GetSessionMessagesForOwnerAsync(Guid sessionId, Guid userId, int take = 500, CancellationToken cancellationToken = default);

        /// <summary>Messages of any session. Only for callers that were already checked to be support agents (or the server itself).</summary>
        Task<ServiceResult<List<ChatMessageDto>>> GetSessionMessagesForAgentAsync(Guid sessionId, int take = 500, CancellationToken cancellationToken = default);

        Task<ServiceResult<bool>> CloseSessionAsync(Guid sessionId);
        Task<ServiceResult<AgentMetricsDto>> GetAgentMetricsAsync(CancellationToken cancellationToken = default);

        /// <summary>Hands a session to another department: a system message says so. The customer's own title is kept.</summary>
        Task<ServiceResult<bool>> TransferSessionAsync(Guid sessionId, string department);
    }
}
