using System.Collections.Generic;
using System.Threading.Tasks;

namespace Baird.Services
{
    public interface ISearchHistoryService
    {
        Task AddSearchTermAsync(string term);
        Task<IEnumerable<string>> GetSuggestedTermsAsync(int maxCount);
    }
}
