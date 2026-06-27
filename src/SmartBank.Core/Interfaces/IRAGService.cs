using System.Threading.Tasks;

namespace SmartBank.Core.Interfaces
{
    public interface IRAGService
    {
        Task<string?> SearchFAQAsync(string query);
    }
}
