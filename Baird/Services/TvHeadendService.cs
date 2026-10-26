using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Baird.Models;

namespace Baird.Services
{
    public class TvHeadendService : IMediaProvider, IEpgService
    {
        public string Name => "TvHeadend";
        private readonly HttpClient _httpClient;
        private readonly string _serverUrl;
        private readonly string _username;
        private readonly string _password;

        // Channel listing cache
        private IEnumerable<MediaItemData>? _cachedListing;
        private DateTime _cacheExpiry = DateTime.MinValue;
        private readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);
        private readonly TimeSpan _cacheTimeout = TimeSpan.FromSeconds(60);

        // EPG schedule cache: channelId -> (programs, cache expiry)
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (IReadOnlyList<EpgProgram> Programs, DateTime Expiry, int Limit)> _scheduleCache = new();

        public TvHeadendService(Microsoft.Extensions.Configuration.IConfiguration config)
        {
            // Load configuration
            string serverUrl = config["TVH_URL"] ?? "http://localhost:9981";
            string username = config["TVH_USER"] ?? "unknown";
            string password = config["TVH_PASS"] ?? "unknown";

            _serverUrl = serverUrl.TrimEnd('/');
            _username = username;
            _password = password;
            // Password ends up in stream URLs; keep it out of logs and the on-disk media cache.
            // Also register any credentials embedded in TVH_URL itself (http://user:pass@host).
            SecretRedactor.Register("TVH_PASS", _password);
            try
            {
                var userInfo = new Uri(_serverUrl).UserInfo;
                var colon = userInfo.IndexOf(':');
                if (colon >= 0) SecretRedactor.Register("TVH_URL_PASS", Uri.UnescapeDataString(userInfo[(colon + 1)..]));
            }
            catch (UriFormatException) { }

            Console.WriteLine($"[TvHeadendService] Attempting connection to: {SecretRedactor.Redact(_serverUrl)} as {_username}");

            // Configure Handler with Custom Digest Auth
            // TvHeadend often requires standard Digest processing which HttpClientHandler can struggle with
            // if headers aren't perfect, so we use a custom implementation.
            var handler = new DigestAuthHandler(_username, _password);

            _httpClient = new HttpClient(handler) { BaseAddress = new Uri(_serverUrl + "/") };

            Console.WriteLine($"[TvHeadendService] Initialized TVHeadend Service at {SecretRedactor.Redact(_serverUrl)} with Custom Digest Auth support");
        }


        public async Task<MediaItemData?> GetItemAsync(string id)
        {
            // TVHeadend doesn't have a direct lookup API easily exposed without grid
            // But we can fetch the grid and filter?
            // Or rely on search?
            // Let's fetch grid and find it. It's cached by TVH usually.
            var all = await GetListingAsync();
            return all.FirstOrDefault(x => x.Id == id);
        }

        public async Task<IEnumerable<MediaItemData>> GetListingAsync()
        {
            // Check if cache is valid
            if (_cachedListing != null && DateTime.UtcNow < _cacheExpiry)
            {
                return _cachedListing;
            }

            // Wait for the semaphore to ensure only one thread populates the cache
            await _cacheLock.WaitAsync().ConfigureAwait(false);
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
                    var url = "api/channel/grid?start=0&limit=9999";

                    Console.WriteLine($"[TvHeadendService] Fetching channels from: {url}");

                    var response = await _httpClient.GetAsync(url).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    // Using Source Generator context
                    var grid = JsonSerializer.Deserialize(json, TvHeadendJsonContext.Default.TvHeadendGrid);

                    if (grid?.Entries != null)
                    {
                        Console.WriteLine($"[TvHeadendService] Fetched {grid.Entries.Count()} channels");
                        _cachedListing = grid.Entries
                            .Select(c => new MediaItemData
                            {
                                Id = c.Uuid,
                                Name = c.Name,
                                Details = "", // Channel number moved to ChannelNumber
                                ChannelNumber = c.Number > 0 ? c.Number.ToString() : "",
                                // TVHeadend icon URL: prefer icon_public_url, fallback to imagecache
                                ImageUrl = GetChannelImageUrl(c),
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

                    _cachedListing = Enumerable.Empty<MediaItemData>();
                    _cacheExpiry = DateTime.UtcNow.Add(_cacheTimeout);
                    return _cachedListing;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TvHeadendService] Failed to fetch TV channels: {ex.Message}");
                    Console.WriteLine($"[TvHeadendService] StackTrace: {ex.StackTrace}");
                    return Enumerable.Empty<MediaItemData>();
                }
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        public async Task<IEnumerable<MediaItemData>> SearchAsync(string query, System.Threading.CancellationToken cancellationToken = default)
        {
            var all = await GetListingAsync();
            if (string.IsNullOrWhiteSpace(query)) return all;

            var q = query.Trim();

            // Use the same logic as before but inside the provider
            // Search by channel number first
            var channelMatches = all
                .Where(i => i.ChannelNumber != null && i.ChannelNumber.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                .OrderBy(i => i.ChannelNumber?.Length)
                .ThenBy(i => i.ChannelNumber);

            var nameMatches = all
                .Where(i => i.Name != null && i.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                .Where(i => i.ChannelNumber == null || !i.ChannelNumber.StartsWith(q, StringComparison.OrdinalIgnoreCase));

            return channelMatches.Concat(nameMatches);
        }

        public Task<IEnumerable<MediaItemData>> GetChildrenAsync(string id)
        {
            return Task.FromResult(Enumerable.Empty<MediaItemData>());
        }

        public async Task<string?> GetCurrentProgrammeNameAsync(string channelId)
        {
            var prog = await GetCurrentProgrammeAsync(channelId).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(prog?.Title) ? null : prog.Title;
        }

        public async Task<EpgProgram?> GetCurrentProgrammeAsync(string channelId)
        {
            var schedule = await GetChannelScheduleAsync(channelId, limit: 5).ConfigureAwait(false);
            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return schedule.FirstOrDefault(e => e.StartUnix <= nowUnix && nowUnix < e.StopUnix)
                ?? schedule.FirstOrDefault();
        }

        public async Task<IReadOnlyList<EpgProgram>> GetChannelScheduleAsync(string channelId, int limit = 20)
        {
            // Reuse a cached schedule only if it was fetched with at least this limit (a 5-programme lookup for "what's
            // on now" mustn't truncate a guide's day-long request).
            if (_scheduleCache.TryGetValue(channelId, out var cached) && DateTime.UtcNow < cached.Expiry && cached.Limit >= limit)
                return cached.Programs.Count <= limit ? cached.Programs : cached.Programs.Take(limit).ToList();

            try
            {
                var url = $"api/epg/events/grid?channel={channelId}&limit={limit}";
                var response = await _httpClient.GetAsync(url).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var grid = JsonSerializer.Deserialize(json, TvHeadendJsonContext.Default.TvHeadendEpgGrid);

                var list = grid?.Entries != null
                    ? grid.Entries.Select(e => MapToEpgProgram(e, channelId)).OrderBy(e => e.StartUnix).ToList()
                    : new List<EpgProgram>();

                var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var currentEvent = list.FirstOrDefault(e => e.StartUnix <= nowUnix && nowUnix < e.StopUnix);
                var cacheExpiry = currentEvent != null && currentEvent.StopUnix > nowUnix
                    ? new[] { DateTimeOffset.FromUnixTimeSeconds(currentEvent.StopUnix).UtcDateTime, DateTime.UtcNow.AddSeconds(120) }.Min()
                    : DateTime.UtcNow.AddSeconds(60);

                _scheduleCache[channelId] = (list, cacheExpiry, limit);
                return list;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TvHeadendService] EPG fetch failed for {channelId}: {ex.Message}");
                _scheduleCache[channelId] = (Array.Empty<EpgProgram>(), DateTime.UtcNow.AddSeconds(30), int.MaxValue); // back off whatever the limit
                return Array.Empty<EpgProgram>();
            }
        }

        public async Task<IReadOnlyDictionary<string, IReadOnlyList<EpgProgram>>> GetBulkScheduleAsync(
            IEnumerable<string> channelIds,
            int limitPerChannel = 10,
            CancellationToken cancellationToken = default)
        {
            var dict = new Dictionary<string, IReadOnlyList<EpgProgram>>();
            var tasks = channelIds.Select(async id =>
            {
                var schedule = await GetChannelScheduleAsync(id, limitPerChannel).ConfigureAwait(false);
                return (id, schedule);
            });

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            foreach (var (id, schedule) in results)
            {
                dict[id] = schedule;
            }
            return dict;
        }

        private static EpgProgram MapToEpgProgram(TvHeadendEpgEvent e, string fallbackChannelId)
        {
            return new EpgProgram
            {
                Id = e.EventId > 0 ? e.EventId.ToString() : $"{fallbackChannelId}_{e.Start}",
                ChannelId = !string.IsNullOrEmpty(e.ChannelUuid) ? e.ChannelUuid : fallbackChannelId,
                ChannelName = e.ChannelName ?? string.Empty,
                ChannelNumber = e.ChannelNumber,
                Title = e.Title ?? string.Empty,
                Subtitle = e.Subtitle,
                Summary = e.Summary,
                Description = e.Description,
                StartUnix = e.Start,
                StopUnix = e.Stop
            };
        }

        private string GetChannelImageUrl(TvHeadendEntry channel)
        {
            // Priority:
            // 1. Use icon_public_url if it's a complete URL
            // 2. Use icon field via imagecache endpoint with auth
            // 3. Use imagecache with UUID as fallback

            if (!string.IsNullOrEmpty(channel.IconPublicUrl))
            {
                // If icon_public_url is already a complete URL, use it
                if (Uri.TryCreate(channel.IconPublicUrl, UriKind.Absolute, out _))
                {
                    return channel.IconPublicUrl;
                }
                // If it's a relative URL from this server
                if (channel.IconPublicUrl.StartsWith("imagecache"))
                {
                    return $"{_serverUrl}/{channel.IconPublicUrl}";
                }
            }

            if (!string.IsNullOrEmpty(channel.Icon))
            {
                // Icon field typically contains the imagecache ID
                // TVHeadend serves icons via /imagecache/{id} endpoint
                // This endpoint requires authentication
                var iconPath = channel.Icon.TrimStart('/');
                return $"{_serverUrl}/imagecache/{iconPath}";
            }

            // Fallback: try to use UUID (though this rarely works)
            return $"{_serverUrl}/imagecache/{channel.Uuid}";
        }

        private string GetStreamUrlInternal(string itemId)
        {
            // Stream URL format: http://user:pass@host:port/stream/channel/{uuid}
            // We embed credentials in URL for mpv to handle auth easily

            // Parse host/port from serverUrl
            Uri uri = new Uri(_serverUrl);
            var host = uri.Host;
            var port = uri.Port;
            var scheme = uri.Scheme;

            // Escape credentials so passwords containing &, #, @ or : survive the query string
            var user = Uri.EscapeDataString(_username);
            var pass = Uri.EscapeDataString(_password);
            return $"{scheme}://{host}:{port}/stream/channel/{itemId}?auth={user}:{pass}&profile=pass";
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
            private string? _cachedHa1;
            private string? _cachedHa1Realm;

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
                    var headerValue = GetDigestHeader(request.Method.Method, request.RequestUri?.PathAndQuery ?? "/");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Digest", headerValue);
                }

                // Send initial request
                var response = await base.SendAsync(request, cancellationToken);

                // Check for 401 Unauthorized with Digest challenge
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && response.Headers.WwwAuthenticate.Any(h => h.Scheme.Equals("Digest", StringComparison.OrdinalIgnoreCase)))
                {
                    Console.WriteLine($"[TvHeadendService] 401 Unauthorized with Digest challenge");
                    var authHeader = response.Headers.WwwAuthenticate.First(h => h.Scheme.Equals("Digest", StringComparison.OrdinalIgnoreCase));
                    ParseHeader(authHeader.Parameter ?? "");

                    var headerValue = GetDigestHeader(request.Method.Method, request.RequestUri?.PathAndQuery ?? "/");

                    request.Headers.Authorization = new AuthenticationHeaderValue("Digest", headerValue);

                    // Retry with auth
                    response = await base.SendAsync(request, cancellationToken);
                    Console.WriteLine($"[TvHeadendService] Digest retry response received ({response.StatusCode})");
                }

                return response;
            }

            private void ParseHeader(string parameter)
            {
                var parts = parameter.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    var p = part.Trim();
                    if (p.StartsWith("realm=")) _realm = p.Substring(6).Trim('"');
                    else if (p.StartsWith("nonce=")) _nonce = p.Substring(6).Trim('"');
                    else if (p.StartsWith("opaque=")) _opaque = p.Substring(7).Trim('"');
                    else if (p.StartsWith("algorithm=")) _algorithm = p.Substring(10).Trim('"');
                    else if (p.StartsWith("qop=")) _qop = p.Substring(4).Trim('"');
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

                string ha1 = GetHa1(_username, _realm, _password);
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

                var header = $"username=\"{_username}\", realm=\"{_realm}\", nonce=\"{_nonce}\", uri=\"{uri}\", response=\"{response}\"";

                if (!string.IsNullOrEmpty(_opaque)) header += $", opaque=\"{_opaque}\"";
                if (!string.IsNullOrEmpty(_algorithm)) header += $", algorithm=\"{_algorithm}\"";
                if (!string.IsNullOrEmpty(_qop)) header += $", qop=\"{_qop}\", nc={_nc:x8}, cnonce=\"{_cnonce}\"";

                return header;
            }

            private string GetHa1(string username, string realm, string password)
            {
                if (_cachedHa1 == null || _cachedHa1Realm != realm)
                {
                    _cachedHa1 = CalculateMd5($"{username}:{realm}:{password}");
                    _cachedHa1Realm = realm;
                }
                return _cachedHa1;
            }

            private string GenerateCNonce()
            {
                return Convert.ToBase64String(Guid.NewGuid().ToByteArray()); // Simple cnonce
            }

            private string CalculateMd5(string input)
            {
                using (var md5 = System.Security.Cryptography.MD5.Create())
                {
                    var bytes = System.Text.Encoding.ASCII.GetBytes(input);
                    var hash = md5.ComputeHash(bytes);
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < hash.Length; i++)
                    {
                        sb.Append(hash[i].ToString("x2"));
                    }
                    return sb.ToString();
                }
            }
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
        public string Icon { get; set; } = null!;

        [JsonPropertyName("icon_public_url")]
        public string IconPublicUrl { get; set; } = null!;
    }

    public class TvHeadendEpgGrid
    {
        [JsonPropertyName("entries")]
        public TvHeadendEpgEvent[] Entries { get; set; } = Array.Empty<TvHeadendEpgEvent>();

        [JsonPropertyName("totalCount")]
        public int TotalCount { get; set; }
    }

    public class TvHeadendEpgEvent
    {
        [JsonPropertyName("eventId")]
        public long EventId { get; set; }

        [JsonPropertyName("channelUuid")]
        public string? ChannelUuid { get; set; }

        [JsonPropertyName("channelNumber")]
        public string? ChannelNumber { get; set; }

        [JsonPropertyName("channelName")]
        public string? ChannelName { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        [JsonPropertyName("subtitle")]
        public string? Subtitle { get; set; }

        [JsonPropertyName("summary")]
        public string? Summary { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("start")]
        public long Start { get; set; }

        [JsonPropertyName("stop")]
        public long Stop { get; set; }
    }

    [JsonSerializable(typeof(TvHeadendGrid))]
    [JsonSerializable(typeof(TvHeadendEpgGrid))]
    internal partial class TvHeadendJsonContext : JsonSerializerContext
    {
    }
}
