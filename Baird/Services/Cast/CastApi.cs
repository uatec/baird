using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Baird.Services.Cast
{
    public sealed record CastRequest(string Method, string Path, string? Authorization, string Body);

    public sealed record CastResponse(int Status, string ContentType, byte[] Body, string? FileName = null)
    {
        public static CastResponse Html(int status, string html) => new(status, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));
    }

    /// <summary>
    /// What Baird's phone server answers (no sockets here: <see cref="CastServer"/> carries the requests).
    /// <list type="bullet">
    /// <item>GET / — the page the install QR opens: a button that adds the "Play on Baird" Shortcut.</item>
    /// <item>GET /shortcut — the signed Shortcut file.</item>
    /// <item>POST /play — {"url": "…"} (or the link as plain text) with "Authorization: Bearer &lt;token&gt;": plays it.</item>
    /// </list>
    /// /play always answers 200 with {"status": "ok" | "unpaired" | "error", "message": "…"}: the Shortcut reads the status
    /// (an unpaired phone forgets its pairing, so the next share scans the TV again) and shows the message as a notification.
    /// </summary>
    public sealed class CastApi
    {
        public const string ShortcutName = "Play on Baird";
        public const string ShortcutFile = ShortcutName + ".shortcut";
        private const int MaxBody = 16 * 1024;

        private readonly PhonePairings _pairings;
        private readonly Func<CastLink, Task<string>> _play;
        private readonly Func<string?> _shortcutPath;

        /// <param name="play">Plays the link on the TV; returns what to tell the phone ("Playing “…” on Baird").</param>
        /// <param name="shortcutPath">Where the signed Shortcut is, or null when this build has none.</param>
        public CastApi(PhonePairings pairings, Func<CastLink, Task<string>> play, Func<string?> shortcutPath)
        {
            _pairings = pairings; _play = play; _shortcutPath = shortcutPath;
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
            switch (r.Method.ToUpperInvariant(), path)
            {
                case ("GET", ""): return CastResponse.Html(200, SetupPage(_shortcutPath() != null));
                case ("GET", "/shortcut"):
                    if (_shortcutPath() is not { } file) return CastResponse.Html(404, Page("Not ready yet", "<p>This version of Baird doesn't have the Shortcut. Update Baird, then try again.</p>"));
                    return new CastResponse(200, "application/octet-stream", await File.ReadAllBytesAsync(file), ShortcutFile);
                case ("POST", "/play"): return await PlayAsync(r);
                default: return CastResponse.Html(404, Page("Not found", "<p>Nothing here.</p>"));
            }
        }

        private async Task<CastResponse> PlayAsync(CastRequest r)
        {
            var token = r.Authorization is { } a && a.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? a[7..].Trim() : null;
            if (!_pairings.Accept(token))
                return Reply("unpaired", "This phone isn't connected to Baird any more. Share again, then scan the code in Baird's Settings › Phones.");
            if (r.Body.Length > MaxBody) return Reply("error", "That's too long to be a link.");
            var shared = SharedText(r.Body);
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

        private static string SetupPage(bool hasShortcut) => Page("Play on Baird", hasShortcut
            ? $"""
              <p>Send videos from this phone to the TV.</p>
              <a class="button" href="/shortcut">Add the “{ShortcutName}” Shortcut</a>
              <ol>
                <li>Tap the button above, then <b>Add Shortcut</b>.</li>
                <li>In YouTube (or Safari, iPlayer, Netflix…) tap <b>Share</b>, then <b>{ShortcutName}</b>.</li>
                <li>The first time, point the phone at the second code on the TV.</li>
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
