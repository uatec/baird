using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Baird.NewUi.Model;
using Baird.Services;

namespace Baird.NewUi.Adapters
{
    public sealed record GuideChannel(Channel Channel, IReadOnlyList<Programme> Schedule);

    /// <summary>The guide's data: every channel with a gap-free schedule from <see cref="Start"/> to <see cref="End"/>.</summary>
    public sealed record GuideModel(DateTimeOffset Start, DateTimeOffset End, IReadOnlyList<GuideChannel> Channels);

    /// <summary>
    /// Channels (as On now: numbered, in order) and their schedules from the EPG, windowed by time. Robust to a missing
    /// or slow guide: a channel with nothing gets one "No programme information" span; gaps between programmes are
    /// filled the same way, so every moment of every row is something you can light (and watch).
    /// </summary>
    public sealed class GuideData
    {
        public const string NoInfoTitle = "No programme information";
        /// <summary>How far the guide reaches: the prototype's 3 h back is moot (TvHeadend only lists from now on).</summary>
        public static readonly TimeSpan Ahead = TimeSpan.FromHours(26);
        private const int Limit = 80; // a day of a busy channel is ~40 programmes

        private readonly HomeData _home;
        private readonly IEpgService _epg;
        private readonly TimeSpan _timeout;

        public GuideData(HomeData home, IEpgService epg, TimeSpan? timeout = null)
        {
            _home = home; _epg = epg; _timeout = timeout ?? TimeSpan.FromSeconds(8);
        }

        /// <summary>Half an hour of context before now, on a half-hour line.</summary>
        public static DateTimeOffset WindowStartFor(DateTimeOffset now)
        {
            var t = now.AddMinutes(-30);
            return new DateTimeOffset(t.Year, t.Month, t.Day, t.Hour, t.Minute >= 30 ? 30 : 0, 0, t.Offset);
        }

        public async Task<GuideModel> LoadAsync(DateTimeOffset now)
        {
            var start = WindowStartFor(now);
            var end = start + Ahead;
            var channels = await _home.ChannelsAsync().ConfigureAwait(false);
            var schedules = await Task.WhenAll(channels.Select(c => ScheduleAsync(c.Id))).ConfigureAwait(false);
            return new GuideModel(start, end, channels.Select((c, i) => new GuideChannel(c, Fill(c.Id, schedules[i], start, end))).ToList());
        }

        private async Task<IReadOnlyList<Programme>> ScheduleAsync(string channelId)
        {
            try
            {
                var task = _epg.GetChannelScheduleAsync(channelId, Limit);
                if (await Task.WhenAny(task, Task.Delay(_timeout)).ConfigureAwait(false) != task)
                {
                    Console.WriteLine($"[GuideData] Guide for {channelId} timed out.");
                    return Array.Empty<Programme>();
                }
                return (await task.ConfigureAwait(false)).Select(HomeData.Map).OfType<Programme>().ToList();
            }
            catch (Exception ex) { Console.WriteLine($"[GuideData] Guide for {channelId} failed: {ex.Message}"); return Array.Empty<Programme>(); }
        }

        /// <summary>Clip to [start, end), drop overlaps, and fill every gap with a "No programme information" span.</summary>
        public static IReadOnlyList<Programme> Fill(string channelId, IEnumerable<Programme> programmes, DateTimeOffset start, DateTimeOffset end)
        {
            var output = new List<Programme>();
            var cursor = start;
            foreach (var prog in programmes.Where(p => p.End > start && p.Start < end && p.End > p.Start).OrderBy(p => p.Start))
            {
                var p = prog;
                if (p.End <= cursor) continue; // overlapped by the one before
                if (p.Start < cursor && output.Count > 0) p = p with { Start = cursor }; // overlapping: starts when the last ends
                if (p.Start > cursor) output.Add(NoInfo(channelId, cursor, p.Start));
                output.Add(p);
                cursor = p.End;
            }
            if (cursor < end) output.Add(NoInfo(channelId, cursor, end));
            return output;
        }

        private static Programme NoInfo(string channelId, DateTimeOffset from, DateTimeOffset to) =>
            new($"{channelId}-none-{from.ToUnixTimeSeconds()}", channelId, NoInfoTitle, null, null, from, to, NoInfo: true);
    }
}
