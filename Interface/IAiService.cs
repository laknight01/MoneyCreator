using MoneyCreator.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MoneyCreator.Service
{
    public interface IAiService
    {
        Task<List<AiResult>> ExecuteAsync(string modelFilter, string systemPrompt, string userPrompt);
    }
}