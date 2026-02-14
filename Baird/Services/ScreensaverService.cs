using System.Text.Json;
using Baird.Models;

namespace Baird.Services;

public class ScreensaverService
{
    private const string JsonUrl = "https://bzamayo.com/extras/apple-tv-screensavers.json";
    private List<ScreensaverAsset> _allScreensavers = new();
    private readonly Random _random = new();
    private bool _isInitialized = false;

    public async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        try
        {
            using var client = new HttpClient();
            string json = await client.GetStringAsync(JsonUrl);
            ScreensaverResponse? response = JsonSerializer.Deserialize<ScreensaverResponse>(json);

            if (response?.Success == true && response.Data != null)
            {
                _allScreensavers = response.Data
                    .Where(c => c.Screensavers != null)
                    .SelectMany(c => c.Screensavers!.Select(s =>
                    {
                        s.CollectionName = c.Name;
                        return s;
                    }))
                    .ToList();

                _isInitialized = true;
                Console.WriteLine($"[ScreensaverService] Loaded {_allScreensavers.Count} screensavers.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ScreensaverService] Failed to load screensavers: {ex.Message}");
        }
    }

    public ScreensaverAsset? GetRandomScreensaver()
    {
        if (!_isInitialized || _allScreensavers.Count == 0)
        {
            // Try strictly synchronous backup using a known URL if list is empty?
            // Or just return null and let the VM handle it.
            return null;
        }

        int index = _random.Next(_allScreensavers.Count);
        return _allScreensavers[index];
    }

    // Optional: Force refresh
    public async Task RefreshAsync()
    {
        _isInitialized = false;
        await InitializeAsync();
    }
}
