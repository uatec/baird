namespace Baird.Services;

public interface ISearchHistoryService
{
    Task AddSearchTermAsync(string term);
    Task<IEnumerable<string>> GetSuggestedTermsAsync(int maxCount);
}
