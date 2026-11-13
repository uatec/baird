using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Baird.Services;
using Xunit;
using Xunit.Abstractions;

namespace Baird.Tests.Services
{
    /// <summary>
    /// Runs a real Chrome (headless) with and without <see cref="CdpBrowserService.MemoryFlags"/> and checks that it adopted them:
    /// a lower JavaScript heap limit. (Process counts are not asserted: the process flags changed the count by one renderer, too
    /// small a margin to test reliably.)
    /// Linux only, and skipped when no Chrome is installed. A flag Chrome ignores or renames would pass a test of the string.
    /// </summary>
    public class CdpBrowserMemoryFlagsTests
    {
        private readonly ITestOutputHelper _out;
        public CdpBrowserMemoryFlagsTests(ITestOutputHelper o) => _out = o;

        private static string[] Flags => CdpBrowserService.MemoryFlags.Split(' ');

        [Fact]
        public void Every_disabled_feature_is_in_one_flag_because_Chromium_honours_only_the_last()
        {
            Assert.Single(Flags.Where(f => f.StartsWith("--disable-features=")));
        }

        [Fact]
        public void The_browser_starts_under_nice_with_its_own_arguments_after_it()
        {
            var (file, args) = CdpBrowserService.UnderNice("/usr/bin/chromium", "--kiosk \"https://x\"", "/usr/bin/nice");
            Assert.Equal("/usr/bin/nice", file);
            Assert.Equal("-n 10 \"/usr/bin/chromium\" --kiosk \"https://x\"", args);
        }

        private static string? Chrome()
        {
            if (!OperatingSystem.IsLinux()) return null;
            var path = new CdpBrowserService().FindBrowserExecutable();
            return File.Exists(path) ? path : null;
        }

        /// <summary>Serves the same tiny pages for any path on any 127.x.x.x address: /heap reports the heap limit, / frames four other sites.</summary>
        private sealed class Pages : IDisposable
        {
            private readonly TcpListener _l = new(IPAddress.Any, 0);
            private readonly CancellationTokenSource _stop = new();
            public int Port => ((IPEndPoint)_l.LocalEndpoint).Port;

            public Pages()
            {
                _l.Start();
                _ = Task.Run(async () =>
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        TcpClient c;
                        try { c = await _l.AcceptTcpClientAsync(_stop.Token); } catch { return; }
                        _ = Task.Run(() => Serve(c));
                    }
                });
            }

            private void Serve(TcpClient c)
            {
                using (c)
                {
                    try
                    {
                        var s = c.GetStream(); var buf = new byte[4096]; var n = s.Read(buf, 0, buf.Length);
                        var path = Encoding.ASCII.GetString(buf, 0, n).Split(' ')[1];
                        var body = path.StartsWith("/heap")
                            ? "<body><script>document.body.textContent='LIMIT='+performance.memory.jsHeapSizeLimit</script>"
                            : path.StartsWith("/leaf") ? "<body>leaf"
                            : "<body>" + string.Concat(Enumerable.Range(2, 4).Select(i => $"<iframe src='http://127.0.0.{i}:{Port}/leaf'></iframe>"));
                        var bytes = Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");
                        s.Write(bytes, 0, bytes.Length);
                    }
                    catch { }
                }
            }

            public void Dispose() { _stop.Cancel(); _l.Stop(); }
        }

        private static ProcessStartInfo Start(string chrome, string profile, bool withFlags, params string[] more)
        {
            var psi = new ProcessStartInfo(chrome) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "--headless=new", "--no-first-run", $"--user-data-dir={profile}" }.Concat(Environment.UserName == "root" ? new[] { "--no-sandbox" } : Array.Empty<string>())) psi.ArgumentList.Add(a);
            if (withFlags) foreach (var f in Flags) psi.ArgumentList.Add(f);
            foreach (var a in more) psi.ArgumentList.Add(a);
            return psi;
        }

        private static async Task<long> HeapLimit(string chrome, int port, bool withFlags)
        {
            var profile = Path.Combine(Path.GetTempPath(), "baird-heap-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var p = Process.Start(Start(chrome, profile, withFlags, "--dump-dom", "--virtual-time-budget=3000", $"http://127.0.0.1:{port}/heap"))!;
                var output = await p.StandardOutput.ReadToEndAsync();
                await p.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
                return long.Parse(Regex.Match(output, @"LIMIT=(\d+)").Groups[1].Value);
            }
            finally { try { Directory.Delete(profile, true); } catch { } }
        }

        [Fact]
        public async Task Chrome_adopts_the_heap_cap()
        {
            if (Chrome() is not { } chrome) return;
            using var pages = new Pages();
            var without = await HeapLimit(chrome, pages.Port, false);
            var with = await HeapLimit(chrome, pages.Port, true);
            _out.WriteLine($"jsHeapSizeLimit without flags {without >> 20} MB, with flags {with >> 20} MB");
            Assert.True(with < without, "the cap made no difference");
            Assert.InRange(with >> 20, 400, 700);
        }
    }
}
