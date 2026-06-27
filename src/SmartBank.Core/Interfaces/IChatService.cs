using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SmartBank.Core.Common;
using SmartBank.Core.DTOs;

namespace SmartBank.Core.Interfaces
{
    public interface IChatService
    {
        Task<ServiceResult<ChatSessionDto>> CreateSessionAsync(Guid? userId, string title);
        Task<ServiceResult<ChatMessageDto>> AddMessageAsync(Guid sessionId, string sender, string content);
        Task<ServiceResult<List<ChatSessionDto>>> GetActiveSessionsAsync();
        Task<ServiceResult<List<ChatMessageDto>>> GetSessionMessagesAsync(Guid sessionId, Guid? userId);
        Task<ServiceResult<bool>> CloseSessionAsync(Guid sessionId);
        Task<ServiceResult<AgentMetricsDto>> GetAgentMetricsAsync();
        Task<ServiceResult<bool>> TransferSessionAsync(Guid sessionId, string department);
    }
}
