using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Baird.Services;

public class TvHeadendService : IMediaProvider, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _serverUrl;
    private readonly string _username;
    private readonly string _password;

    // Cache fields
    private IEnumerable<MediaItem>? _cachedListing;
    private DateTime _cacheExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);
    private readonly TimeSpan _cacheTimeout = TimeSpan.FromSeconds(60);

    public TvHeadendService(Microsoft.Extensions.Configuration.IConfiguration config)
    {
        // Load configuration
        string serverUrl = config["TVH_URL"] ?? "http://localhost:9981";
        string username = config["TVH_USER"] ?? "unknown";
        string password = config["TVH_PASS"] ?? "unknown";

        _serverUrl = serverUrl.TrimEnd('/');
        _username = username;
        _password = password;

        Console.WriteLine($"[TvHeadendService] Attempting connection to: {_serverUrl} as {_username}");

        // Configure Handler with Custom Digest Auth
        // TvHeadend often requires standard Digest processing which HttpClientHandler can struggle with
        // if headers aren't perfect, so we use a custom implementation.
        var handler = new DigestAuthHandler(_username, _password);

        _httpClient = new HttpClient(handler) { BaseAddress = new Uri(_serverUrl + "/") };

        Console.WriteLine($"[TvHeadendService] Initialized TVHeadend Service at {_serverUrl} with Custom Digest Auth support");
    }


    public async Task<MediaItem?> GetItemAsync(string id)
    {
        // TVHeadend doesn't have a direct lookup API easily exposed without grid
        // But we can fetch the grid and filter?
        // Or rely on search?
        // Let's fetch grid and find it. It's cached by TVH usually.
        IEnumerable<MediaItem> all = await GetListingAsync();
        return all.FirstOrDefault(x => x.Id == id);
    }

    public async Task<IEnumerable<MediaItem>> GetListingAsync()
    {
        // Check if cache is valid
        if (_cachedListing != null && DateTime.UtcNow < _cacheExpiry)
        {
            return _cachedListing;
        }

        // Wait for the semaphore to ensure only one thread populates the cache
        await _cacheLock.WaitAsync();
        try
        {
            // Double-check after acquiring lock (another thread might have populated it)
            if (_cachedListing != null && DateTime.UtcNow < _cacheExpiry)
            {
                Console.WriteLine($"[TvHeadendService] Returning cached channels (acquired after lock)");
                return _cachedListing;
            }

            // Populate the cache
            Console.WriteLine($"[TvHeadendService] Populating cache");

            try
            {
                // API to get channel grid: /api/channel/grid
                string url = "api/channel/grid?start=0&limit=9999";

                Console.WriteLine($"[TvHeadendService] Fetching channels from: {url}");

                HttpResponseMessage response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();

                string json = await response.Content.ReadAsStringAsync();

                // Using Source Generator context
                TvHeadendGrid? grid = JsonSerializer.Deserialize(json, TvHeadendJsonContext.Default.TvHeadendGrid);

                if (grid?.Entries != null)
                {
                    Console.WriteLine($"[TvHeadendService] Fetched {grid.Entries.Count()} channels");
                    _cachedListing = grid.Entries
                        .Select(c => new MediaItem
                        {
                            Id = c.Uuid,
                            Name = c.Name,
                            Details = "", // Channel number moved to ChannelNumber
                            ChannelNumber = c.Number > 0 ? c.Number.ToString() : "",
                            // TVHeadend icon URL: /imagecache/{id}
                            ImageUrl = !string.IsNullOrEmpty(c.IconUrl) ? c.IconUrl : $"{_serverUrl}/imagecache/{c.IconId}",
                            IsLive = true,
                            StreamUrl = GetStreamUrlInternal(c.Uuid),
                            Source = "Live TV",
                            Type = MediaType.Channel,
                            Subtitle = "",
                            Synopsis = "",
                        })
                        .OrderBy(c => c.Name)
                        .ToList(); // Materialize to avoid re-execution

                    _cacheExpiry = DateTime.UtcNow.Add(_cacheTimeout);
                    return _cachedListing;
                }

                _cachedListing = Enumerable.Empty<MediaItem>();
                _cacheExpiry = DateTime.UtcNow.Add(_cacheTimeout);
                return _cachedListing;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TvHeadendService] Failed to fetch TV channels: {ex.Message}");
                Console.WriteLine($"[TvHeadendService] StackTrace: {ex.StackTrace}");
                return Enumerable.Empty<MediaItem>();
            }
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public async Task<IEnumerable<MediaItem>> SearchAsync(string query, System.Threading.CancellationToken cancellationToken = default)
    {
        IEnumerable<MediaItem> all = await GetListingAsync();
        if (string.IsNullOrWhiteSpace(query))
        {
            return all;
        }

        string q = query.Trim();

        // Use the same logic as before but inside the provider
        // Search by channel number first
        IOrderedEnumerable<MediaItem> channelMatches = all
            .Where(i => i.ChannelNumber != null && i.ChannelNumber.StartsWith(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.ChannelNumber?.Length)
            .ThenBy(i => i.ChannelNumber);

        IEnumerable<MediaItem> nameMatches = all
            .Where(i => i.Name != null && i.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
            .Where(i => i.ChannelNumber == null || !i.ChannelNumber.StartsWith(q, StringComparison.OrdinalIgnoreCase));

        return channelMatches.Concat(nameMatches);
    }

    public Task<IEnumerable<MediaItem>> GetChildrenAsync(string id)
    {
        return Task.FromResult(Enumerable.Empty<MediaItem>());
    }

    private string GetStreamUrlInternal(string itemId)
    {
        // Stream URL format: http://user:pass@host:port/stream/channel/{uuid}
        // We embed credentials in URL for mpv to handle auth easily

        // Parse host/port from serverUrl
        var uri = new Uri(_serverUrl);
        string host = uri.Host;
        int port = uri.Port;
        string scheme = uri.Scheme;

        return $"{scheme}://{host}:{port}/stream/channel/{itemId}?auth={_username}:{_password}&profile=pass";
    }

    // Custom Digest Auth Handler
    private class DigestAuthHandler : DelegatingHandler
    {
        private readonly string _username;
        private readonly string _password;
        private string _realm = null!;
        private string _nonce = null!;
        private string _opaque = null!;
        private string _algorithm = null!;
        private string _qop = null!;
        private int _nc = 0;
        private string _cnonce = null!;
        private string _lastNonce = null!;

        public DigestAuthHandler(string username, string password) : base(new HttpClientHandler())
        {
            _username = username;
            _password = password;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
        {
            // Check if we have cached info to send pre-emptive auth
            if (!string.IsNullOrEmpty(_nonce) && !string.IsNullOrEmpty(_realm))
            {
                string headerValue = GetDigestHeader(request.Method.Method, request.RequestUri?.PathAndQuery ?? "/");
                request.Headers.Authorization = new AuthenticationHeaderValue("Digest", headerValue);
            }

            // Send initial request
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);

            // Check for 401 Unauthorized with Digest challenge
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && response.Headers.WwwAuthenticate.Any(h => h.Scheme.Equals("Digest", StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine($"[TvHeadendService] 401 Unauthorized with Digest challenge");
                AuthenticationHeaderValue authHeader = response.Headers.WwwAuthenticate.First(h => h.Scheme.Equals("Digest", StringComparison.OrdinalIgnoreCase));
                ParseHeader(authHeader.Parameter ?? "");

                string headerValue = GetDigestHeader(request.Method.Method, request.RequestUri?.PathAndQuery ?? "/");

                request.Headers.Authorization = new AuthenticationHeaderValue("Digest", headerValue);

                // Retry with auth
                response = await base.SendAsync(request, cancellationToken);
                Console.WriteLine($"[TvHeadendService] Digest retry response received ({response.StatusCode})");
            }

            return response;
        }

        private void ParseHeader(string parameter)
        {
            string[] parts = parameter.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                string p = part.Trim();
                if (p.StartsWith("realm="))
                {
                    _realm = p.Substring(6).Trim('"');
                }
                else if (p.StartsWith("nonce="))
                {
                    _nonce = p.Substring(6).Trim('"');
                }
                else if (p.StartsWith("opaque="))
                {
                    _opaque = p.Substring(7).Trim('"');
                }
                else if (p.StartsWith("algorithm="))
                {
                    _algorithm = p.Substring(10).Trim('"');
                }
                else if (p.StartsWith("qop="))
                {
                    _qop = p.Substring(4).Trim('"');
                }
            }
        }

        private string GetDigestHeader(string method, string uri)
        {
            if (_nonce != _lastNonce)
            {
                _nc = 0; // Reset nonce count if nonce has changed
                _lastNonce = _nonce;
            }
            _nc++;

            _cnonce = GenerateCNonce();

            string ha1 = CalculateMd5($"{_username}:{_realm}:{_password}");
            string ha2 = CalculateMd5($"{method}:{uri}");
            string response;

            if (!string.IsNullOrEmpty(_qop))
            {
                // qop=auth
                response = CalculateMd5($"{ha1}:{_nonce}:{_nc:x8}:{_cnonce}:{_qop}:{ha2}");
            }
            else
            {
                // Legacy Digest
                response = CalculateMd5($"{ha1}:{_nonce}:{ha2}");
            }

            string header = $"username=\"{_username}\", realm=\"{_realm}\", nonce=\"{_nonce}\", uri=\"{uri}\", response=\"{response}\"";

            if (!string.IsNullOrEmpty(_opaque))
            {
                header += $", opaque=\"{_opaque}\"";
            }

            if (!string.IsNullOrEmpty(_algorithm))
            {
                header += $", algorithm=\"{_algorithm}\"";
            }

            if (!string.IsNullOrEmpty(_qop))
            {
                header += $", qop=\"{_qop}\", nc={_nc:x8}, cnonce=\"{_cnonce}\"";
            }

            return header;
        }

        private string GenerateCNonce()
        {
            return Convert.ToBase64String(Guid.NewGuid().ToByteArray()); // Simple cnonce
        }

        private string CalculateMd5(string input)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] bytes = System.Text.Encoding.ASCII.GetBytes(input);
                byte[] hash = md5.ComputeHash(bytes);
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < hash.Length; i++)
                {
                    sb.Append(hash[i].ToString("x2"));
                }
                return sb.ToString();
            }
        }
    }

    public void Dispose()
    {
        throw new NotImplementedException();
    }
}

// Data models for TVHeadend JSON
public class TvHeadendGrid
{
    [JsonPropertyName("entries")]
    public TvHeadendEntry[] Entries { get; set; } = null!;

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }
}

public class TvHeadendEntry
{
    [JsonPropertyName("uuid")]
    public string Uuid { get; set; } = null!;

    [JsonPropertyName("name")]
    public string Name { get; set; } = null!;

    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("icon")]
    public string IconUrl { get; set; } = null!;

    [JsonPropertyName("icon_public_url")]
    public string IconId { get; set; } = null!; // Sometimes useful if IconUrl is relative
}

[JsonSerializable(typeof(TvHeadendGrid))]
internal partial class TvHeadendJsonContext : JsonSerializerContext
{
}
