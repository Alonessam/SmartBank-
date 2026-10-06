using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SmartBank.Core.DTOs;

namespace SmartBank.Core.Interfaces
{
    public interface IAIChatbotService
    {
        /// <summary>
        /// Answers the conversation. History senders: User, AI, Agent (stored messages) and Context (retrieved FAQ text for the model).
        /// "System" rows from the database are never instructions and are ignored by the implementations.
        /// </summary>
        Task<string> GetResponseAsync(List<ChatMessageDto> history, CancellationToken cancellationToken = default);
    }
}
