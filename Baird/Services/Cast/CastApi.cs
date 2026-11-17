using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Baird.Services.Cast
{
    public sealed record CastRequest(string Method, string Path, string Body);

    public sealed record CastResponse(int Status, string ContentType, byte[] Body, string? FileName = null)
    {
        public static CastResponse Html(int status, string html) => new(status, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));
    }

    /// <summary>
    /// What Baird's phone server answers (no sockets here: <see cref="CastServer"/> carries the requests).
    /// <list type="bullet">
    /// <item>GET /phone/&lt;token&gt; — what the TV's code opens in the camera: a button that adds the "Play on Baird" Shortcut.</item>
    /// <item>GET /shortcut — the signed Shortcut file.</item>
    /// <item>POST /phone/&lt;token&gt; — {"url": "…"} (or the link as plain text): plays it.</item>
    /// </list>
    /// One code does both: the camera opens it to install the Shortcut, and the Shortcut scans the same code on its first run
    /// and keeps the address, token and all. The token is in the path rather than a header: on plain HTTP it's no more exposed
    /// there, and the Shortcut only has to remember one address.
    /// POST always answers 200 with {"status": "ok" | "unpaired" | "error", "message": "…"}: the Shortcut reads the status
    /// (an unpaired phone forgets its address, so the next share scans the TV again) and shows the message as a notification.
    /// </summary>
    public sealed class CastApi
    {
        public const string ShortcutName = "Play on Baird";
        public const string ShortcutFile = ShortcutName + ".shortcut";
        public const string PhonePath = "/phone/";

        /// <summary>What the TV's code holds: this phone's own address on Baird.</summary>
        public static string PhoneUrl(string baseUrl, string token) => baseUrl.TrimEnd('/') + PhonePath + token;
        private const int MaxBody = 16 * 1024;

        private readonly PhonePairings _pairings;
        private readonly Func<CastLink, Task<string>> _play;
        private readonly Func<string?> _shortcutPath;
        private readonly Func<string?> _shortcutLink;

        /// <param name="play">Plays the link on the TV; returns what to tell the phone ("Playing “…” on Baird").</param>
        /// <param name="shortcutPath">Where the signed Shortcut is, or null when this build has none.</param>
        /// <param name="shortcutLink">
        /// An iCloud link to the Shortcut (Baird:ShortcutLink), for a Shortcut built by hand on an iPhone (its iCloud link is
        /// signed by its owner's account). Wins over the file; read each time, so a changed config.ini takes effect at once.
        /// </param>
        public CastApi(PhonePairings pairings, Func<CastLink, Task<string>> play, Func<string?> shortcutPath, Func<string?>? shortcutLink = null)
        {
            _pairings = pairings; _play = play; _shortcutPath = shortcutPath; _shortcutLink = shortcutLink ?? (() => null);
        }

        /// <summary>
        /// The signed Shortcut: a copy in ~/.baird wins (to try one out), else the one shipped in the release zip: in the brew
        /// keg's libexec (the brew wrapper runs Baird from an unpacked copy elsewhere), or beside Baird when run from the zip.
        /// </summary>
        public static string? FindShortcut() => new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".baird", ShortcutFile),
            Path.Combine(Path.GetDirectoryName(UpdateService.DefaultScript)!, ShortcutFile),
            Path.Combine(AppContext.BaseDirectory, ShortcutFile),
        }.FirstOrDefault(File.Exists);

        public async Task<CastResponse> HandleAsync(CastRequest r)
        {
            var path = r.Path.TrimEnd('/');
            var method = r.Method.ToUpperInvariant();
            if ((path + "/").StartsWith(PhonePath, StringComparison.Ordinal))
            {
                var token = path.Length > PhonePath.Length ? path[PhonePath.Length..] : "";
                if (method == "POST") return await PlayAsync(token, r.Body);
                if (method == "GET") return CastResponse.Html(200, SetupPage(InstallHref()));
            }
            switch (method, path)
            {
                case ("GET", ""): return CastResponse.Html(200, SetupPage(InstallHref()));
                case ("GET", "/shortcut"):
                    if (_shortcutPath() is not { } file) return CastResponse.Html(404, Page("Not ready yet", "<p>This version of Baird doesn't have the Shortcut. Update Baird, then try again.</p>"));
                    return new CastResponse(200, "application/octet-stream", await File.ReadAllBytesAsync(file), ShortcutFile);
                default: return CastResponse.Html(404, Page("Not found", "<p>Nothing here.</p>"));
            }
        }

        private async Task<CastResponse> PlayAsync(string token, string body)
        {
            if (!_pairings.Accept(token))
                return Reply("unpaired", "This phone isn't connected to Baird any more. Share again, then scan the code in Baird's Settings › Connect a phone.");
            if (body.Length > MaxBody) return Reply("error", "That's too long to be a link.");
            var shared = SharedText(body);
            if (CastLinks.Resolve(shared) is not { } link)
                return Reply("error", string.IsNullOrWhiteSpace(shared) ? "Nothing was shared. Share a video's link to Play on Baird." : "That isn't a link Baird can play.");
            Console.WriteLine($"[Cast] Playing {link.Service}: {link.PlayUrl}");
            try { return Reply("ok", await _play(link)); }
            catch (Exception ex)
            {
                Console.WriteLine($"[Cast] Couldn't play {link.PlayUrl}: {ex}");
                return Reply("error", "Baird couldn't play that.");
            }
        }

        /// <summary>{"url": …} as the Shortcut sends it, or the body itself.</summary>
        internal static string SharedText(string body)
        {
            var trimmed = body.Trim();
            if (!trimmed.StartsWith('{')) return trimmed;
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                return doc.RootElement.TryGetProperty("url", out var u) ? u.ValueKind switch
                {
                    JsonValueKind.String => u.GetString() ?? "",
                    JsonValueKind.Array => string.Join(" ", u.EnumerateArray().Select(x => x.ToString())), // more than one item shared
                    _ => u.ToString(),
                } : "";
            }
            catch (JsonException) { return trimmed; }
        }

        private static CastResponse Reply(string status, string message)
        {
            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream))
            {
                w.WriteStartObject();
                w.WriteString("status", status);
                w.WriteString("message", message);
                w.WriteEndObject();
            }
            return new CastResponse(200, "application/json; charset=utf-8", stream.ToArray());
        }

        /// <summary>Where "Add the Shortcut" goes: the iCloud link if one is set, else the shipped file, else nowhere.</summary>
        private string? InstallHref()
        {
            var link = _shortcutLink()?.Trim();
            if (!string.IsNullOrEmpty(link) && link.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return link;
            return _shortcutPath() != null ? "/shortcut" : null;
        }

        private static string SetupPage(string? installHref) => Page("Play on Baird", installHref != null
            ? $"""
              <p>Send videos from this phone to the TV.</p>
              <a class="button" href="{WebUtility.HtmlEncode(installHref)}">Add the “{ShortcutName}” Shortcut</a>
              <ol>
                <li>Tap the button above, then <b>Add Shortcut</b>.</li>
                <li>In YouTube (or Safari, iPlayer, Netflix…) tap <b>Share</b>, then <b>{ShortcutName}</b>.</li>
                <li>The first time, it asks to scan: point it at the same code on the TV.</li>
              </ol>
              """
            : "<p>This version of Baird doesn't have the Shortcut yet. Update Baird, then scan the code again.</p>");

        private static string Page(string title, string body) => $$"""
            <!doctype html>
            <html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{WebUtility.HtmlEncode(title)}}</title>
            <style>
              body { font: 18px -apple-system, system-ui, sans-serif; background: #0a0a0c; color: #f4f4f6; margin: 0; padding: 32px 24px; line-height: 1.45; }
              h1 { font-size: 30px; margin: 0 0 12px; }
              .button { display: block; text-align: center; background: #f4f4f6; color: #0a0a0c; text-decoration: none; font-weight: 600;
                        padding: 16px; border-radius: 14px; margin: 24px 0; }
              li { margin: 8px 0; }
            </style></head>
            <body><h1>{{WebUtility.HtmlEncode(title)}}</h1>{{body}}</body></html>
            """;
    }
}
