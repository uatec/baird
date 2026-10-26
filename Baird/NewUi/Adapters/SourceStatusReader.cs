using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baird.NewUi.Model;
using Baird.Services;

namespace Baird.NewUi.Adapters
{
    /// <summary>
    /// Settings' source rows from what the services already know: the channels loaded, Jellyfin's sign-in, Netflix's
    /// sign-in state, whether Disney+ is set up. Seerr is the only one asked over the network, and at most once a minute.
    /// </summary>
    public sealed class SourceStatusReader
    {
        private readonly Func<int> _channels;
        private readonly Func<bool> _jellyfin;
        private readonly IJellyseerrService? _seerr;
        private readonly bool _seerrConfigured;
        private readonly Func<AccountState> _netflix, _disney;
        private readonly Func<DateTimeOffset> _now;
        private (DateTimeOffset At, bool Ok)? _seerrSeen;
        public static readonly TimeSpan SeerrEvery = TimeSpan.FromMinutes(1);

        public SourceStatusReader(Func<int> channels, Func<bool> jellyfinSignedIn, IJellyseerrService? seerr, bool seerrConfigured,
            Func<AccountState> netflix, Func<AccountState> disney, Func<DateTimeOffset> now)
        {
            _channels = channels; _jellyfin = jellyfinSignedIn; _seerr = seerr; _seerrConfigured = seerrConfigured;
            _netflix = netflix; _disney = disney; _now = now;
        }

        /// <summary>
        /// Netflix: signed in once Baird has seen a member on a profile; "needs signing in" only when Baird tried and
        /// couldn't. Disney+: signed in once its API token was taken (it signs in at start-up, after ~30 s). Until then
        /// either is "Set up": nothing has said it isn't signed in.
        /// </summary>
        public static SourceStatusReader For(LegacyServices s, Func<int> channels, bool seerrConfigured, Func<DateTimeOffset> now) =>
            new(channels, () => s.Jellyfin.IsAuthenticated, s.Seerr, seerrConfigured,
                () => s.Netflix == null ? AccountState.NotSetUp : s.Netflix.NeedsSignIn ? AccountState.NeedsSignIn : s.Netflix.IsSignedIn ? AccountState.SignedIn : AccountState.SetUp,
                () => s.Disney == null ? AccountState.NotSetUp : s.Disney.Session.HasApiToken ? AccountState.SignedIn : AccountState.SetUp,
                now);

        private static SourceStatus Account(string name, AccountState state) => state switch
        {
            AccountState.SignedIn => new(name, "Signed in", SourceHealth.Ok),
            AccountState.SetUp => new(name, "Set up", SourceHealth.Ok),
            AccountState.NeedsSignIn => new(name, "Needs signing in", SourceHealth.Warning),
            _ => new(name, "Not set up", SourceHealth.Off),
        };

        public async Task<IReadOnlyList<SourceStatus>> ReadAsync(CancellationToken ct = default)
        {
            var n = _channels();
            var list = new List<SourceStatus>
            {
                n > 0 ? new("Freesat (TvHeadend)", $"{n} channels", SourceHealth.Ok) : new("Freesat (TvHeadend)", "No channels yet", SourceHealth.Warning),
                _jellyfin() ? new("Jellyfin", "Connected", SourceHealth.Ok) : new("Jellyfin", "Not connected", SourceHealth.Down),
                await SeerrAsync(ct).ConfigureAwait(false),
                Account("Netflix", _netflix()),
                Account("Disney+", _disney()),
            };
            return list;
        }

        private async Task<SourceStatus> SeerrAsync(CancellationToken ct)
        {
            const string name = "Seerr requests";
            if (_seerr == null || !_seerrConfigured) return new(name, "Not set up", SourceHealth.Off);
            if (_seerrSeen is not { } seen || _now() - seen.At >= SeerrEvery)
            {
                bool ok;
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(4));
                    ok = (await _seerr.GetTrendingAsync(1, cts.Token).ConfigureAwait(false)).Any();
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { ok = false; }
                _seerrSeen = seen = (_now(), ok);
            }
            return seen.Ok ? new(name, "Connected", SourceHealth.Ok) : new(name, "Not answering", SourceHealth.Down);
        }
    }
}
