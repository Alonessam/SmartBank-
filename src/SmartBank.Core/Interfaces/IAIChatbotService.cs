using System.Collections.Generic;
using System.Threading.Tasks;
using SmartBank.Core.DTOs;

namespace SmartBank.Core.Interfaces
{
    public interface IAIChatbotService
    {
        Task<string> GetResponseAsync(List<ChatMessageDto> history);
    }
}
