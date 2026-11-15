using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace Baird.Services.Cast
{
    /// <summary>
    /// Baird's phone server: plain HTTP on the home network (port Baird:PhonePort, 8099 by default), carrying requests to
    /// <see cref="CastApi"/>. Off with Baird:Phones = false.
    /// </summary>
    public sealed class CastServer : IDisposable
    {
        public const int DefaultPort = 8099;
        private const int MaxBody = 64 * 1024;

        private readonly HttpListener _listener = new();
        private readonly CastApi _api;
        private readonly CancellationTokenSource _stop = new();

        public int Port { get; }

        public CastServer(CastApi api, int port)
        {
            _api = api; Port = port;
            _listener.Prefixes.Add($"http://+:{port}/");
        }

        public static bool Enabled(IConfiguration config) =>
            !string.Equals(config["Baird:Phones"]?.Trim(), "false", StringComparison.OrdinalIgnoreCase);

        public static int PortFrom(IConfiguration config) =>
            int.TryParse(config["Baird:PhonePort"], out var p) && p is > 0 and < 65536 ? p : DefaultPort;

        /// <summary>
        /// The address a phone reaches Baird at, for the QR code: Baird:PhoneAddress if set, else this machine's hostname
        /// ("http://baird:8099"). A name, not an IP: it survives the router handing out a new IP, the home router's DNS
        /// answers it on the Wi-Fi (guests included), and Tailscale's MagicDNS answers it away from home. Null when this
        /// machine isn't on a home network (nothing could reach it).
        /// </summary>
        public static string? BaseUrl(IConfiguration config, int port) =>
            BaseUrl(config["Baird:PhoneAddress"], port, SafeHostName(), HomeAddress() != null);

        internal static string? BaseUrl(string? phoneAddress, int port, string? hostName, bool onHomeNetwork)
        {
            if (phoneAddress is { Length: > 0 } set && set.Trim().TrimEnd('/') is { Length: > 0 } trimmed)
            {
                if (trimmed.Contains("://")) return trimmed;
                return trimmed.Contains(':') ? $"http://{trimmed}" : $"http://{trimmed}:{port}";
            }
            if (!onHomeNetwork) return null;
            var name = hostName?.Trim();
            if (string.IsNullOrEmpty(name) || name.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return HomeAddress() is { } ip ? $"http://{ip}:{port}" : null;
            return $"http://{name.ToLowerInvariant()}:{port}";
        }

        private static string? SafeHostName()
        {
            try { return Dns.GetHostName(); }
            catch (Exception ex) { Console.WriteLine($"[Cast] Couldn't read the hostname: {ex.Message}"); return null; }
        }

        /// <summary>The private IPv4 address of a wired or Wi-Fi interface (not loopback, not Tailscale's 100.64/10).</summary>
        internal static IPAddress? HomeAddress()
        {
            try
            {
                var candidates = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 1)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => a.Address))
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork && IsHomeNetwork(a));
                return candidates.FirstOrDefault();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Cast] Couldn't read network addresses: {ex.Message}");
                return null;
            }
        }

        internal static bool IsHomeNetwork(IPAddress a)
        {
            var b = a.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
        }

        public void Start()
        {
            try { _listener.Start(); }
            catch (Exception ex)
            {
                Console.WriteLine($"[Cast] Couldn't listen on port {Port}: {ex.Message}");
                return;
            }
            Console.WriteLine($"[Cast] Listening for phones on port {Port}.");
            _ = Task.Run(LoopAsync);
        }

        private async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch (Exception) when (_stop.IsCancellationRequested || !_listener.IsListening) { return; }
                catch (Exception ex) { Console.WriteLine($"[Cast] Accept failed: {ex.Message}"); continue; }
                _ = Task.Run(() => ServeAsync(ctx));
            }
        }

        private async Task ServeAsync(HttpListenerContext ctx)
        {
            try
            {
                var req = ctx.Request;
                var body = "";
                if (req.HasEntityBody)
                {
                    var buffer = new byte[MaxBody + 1];
                    var read = 0;
                    int n;
                    while (read < buffer.Length && (n = await req.InputStream.ReadAsync(buffer.AsMemory(read)).ConfigureAwait(false)) > 0) read += n;
                    body = Encoding.UTF8.GetString(buffer, 0, read);
                }
                var res = await _api.HandleAsync(new CastRequest(req.HttpMethod, req.Url?.AbsolutePath ?? "/", body)).ConfigureAwait(false);
                ctx.Response.StatusCode = res.Status;
                ctx.Response.ContentType = res.ContentType;
                if (res.FileName != null) ctx.Response.AddHeader("Content-Disposition", $"attachment; filename=\"{res.FileName}\"");
                ctx.Response.ContentLength64 = res.Body.Length;
                await ctx.Response.OutputStream.WriteAsync(res.Body).ConfigureAwait(false);
            }
            catch (Exception ex) { Console.WriteLine($"[Cast] Request failed: {ex.Message}"); try { ctx.Response.StatusCode = 500; } catch { } }
            finally { try { ctx.Response.Close(); } catch { } }
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _listener.Close(); } catch { }
        }
    }
}
