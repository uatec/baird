using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Baird.Services
{
    public class TvHeadendService : IMediaProvider
    {
        private readonly HttpClient _httpClient;
        private readonly string _serverUrl;
        private readonly string _username;
        private readonly string _password;

        public TvHeadendService()
        {
            // Load configuration locally
            string serverUrl = Environment.GetEnvironmentVariable("TVH_URL") ?? "http://localhost:9981";
            string username = Environment.GetEnvironmentVariable("TVH_USER") ?? "unknown";
            string password = Environment.GetEnvironmentVariable("TVH_PASS") ?? "unknown";

            // Support .env file if present (check current and parent dir)
            var envPath = ".env";
            if (!System.IO.File.Exists(envPath))
            {
                if (System.IO.File.Exists("../.env")) envPath = "../.env";
            }

            if (System.IO.File.Exists(envPath))
            {
                foreach (var line in System.IO.File.ReadAllLines(envPath))
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length != 2) continue;
                    var key = parts[0].Trim();
                    var val = parts[1].Trim();

                    if (key == "TVH_URL") serverUrl = val;
                    if (key == "TVH_USER") username = val;
                    if (key == "TVH_PASS") password = val;
                }
            }

            _serverUrl = serverUrl.TrimEnd('/');
            _username = username;
            _password = password;

            Console.WriteLine($"Attempting connection to: {_serverUrl} as {_username}");

            // Configure Handler with Custom Digest Auth
            // TvHeadend often requires standard Digest processing which HttpClientHandler can struggle with
            // if headers aren't perfect, so we use a custom implementation.
            var handler = new DigestAuthHandler(_username, _password);

            _httpClient = new HttpClient(handler) { BaseAddress = new Uri(_serverUrl + "/") };

            Console.WriteLine($"Initialized TVHeadend Service at {_serverUrl} with Custom Digest Auth support");
        }


        public async Task<MediaItem?> GetItemAsync(string id)
        {
            // TVHeadend doesn't have a direct lookup API easily exposed without grid
            // But we can fetch the grid and filter?
            // Or rely on search?
            // Let's fetch grid and find it. It's cached by TVH usually.
            var all = await GetListingAsync();
            return all.FirstOrDefault(x => x.Id == id);
        }

        public async Task<IEnumerable<MediaItem>> GetListingAsync()
        {
            try
            {
                // API to get channel grid: /api/channel/grid
                var url = "api/channel/grid?start=0&limit=9999";

                Console.WriteLine($"Fetching channels from: {url}");

                var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();

                // Using Source Generator context
                var grid = JsonSerializer.Deserialize(json, TvHeadendJsonContext.Default.TvHeadendGrid);

                if (grid?.Entries != null)
                {
                    return grid.Entries
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
                        .OrderBy(c => c.Name);
                }

                return Enumerable.Empty<MediaItem>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to fetch TV channels: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                return Enumerable.Empty<MediaItem>();
            }
        }

        public async Task<IEnumerable<MediaItem>> SearchAsync(string query, System.Threading.CancellationToken cancellationToken = default)
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

        public Task<IEnumerable<MediaItem>> GetChildrenAsync(string id)
        {
            return Task.FromResult(Enumerable.Empty<MediaItem>());
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
                // Send initial request
                var response = await base.SendAsync(request, cancellationToken);

                // Check for 401 Unauthorized with Digest challenge
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && response.Headers.WwwAuthenticate.Any(h => h.Scheme.Equals("Digest", StringComparison.OrdinalIgnoreCase)))
                {
                    Console.WriteLine($"[TVHeadend] 401 Unauthorized with Digest challenge");
                    var authHeader = response.Headers.WwwAuthenticate.First(h => h.Scheme.Equals("Digest", StringComparison.OrdinalIgnoreCase));
                    ParseHeader(authHeader.Parameter ?? "");

                    var headerValue = GetDigestHeader(request.Method.Method, request.RequestUri?.PathAndQuery ?? "/");

                    request.Headers.Authorization = new AuthenticationHeaderValue("Digest", headerValue);

                    // Retry with auth
                    response = await base.SendAsync(request, cancellationToken);
                    Console.WriteLine($"[TVHeadend] Digest retry response received ({response.StatusCode})");
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

                var header = $"username=\"{_username}\", realm=\"{_realm}\", nonce=\"{_nonce}\", uri=\"{uri}\", response=\"{response}\"";

                if (!string.IsNullOrEmpty(_opaque)) header += $", opaque=\"{_opaque}\"";
                if (!string.IsNullOrEmpty(_algorithm)) header += $", algorithm=\"{_algorithm}\"";
                if (!string.IsNullOrEmpty(_qop)) header += $", qop=\"{_qop}\", nc={_nc:x8}, cnonce=\"{_cnonce}\"";

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
        public string IconUrl { get; set; } = null!;

        [JsonPropertyName("icon_public_url")]
        public string IconId { get; set; } = null!; // Sometimes useful if IconUrl is relative
    }

    [JsonSerializable(typeof(TvHeadendGrid))]
    internal partial class TvHeadendJsonContext : JsonSerializerContext
    {
    }
}
