using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baird.Models;
using Baird.NewUi.Adapters;
using Baird.NewUi.Model;
using Baird.Services;
using Baird.Tests.Mocks;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class GuideDataTests
    {
        private static readonly DateTimeOffset Now = new(2026, 10, 26, 18, 10, 0, TimeSpan.Zero);

        private static EpgProgram Ep(string id, string ch, string title, int fromMin, int toMin) => new()
        {
            Id = id,
            ChannelId = ch,
            Title = title,
            Subtitle = "sub",
            Description = "desc",
            StartUnix = Now.AddMinutes(fromMin).ToUnixTimeSeconds(),
            StopUnix = Now.AddMinutes(toMin).ToUnixTimeSeconds(),
        };

        private static (GuideData Guide, MockEpgService Epg) Build()
        {
            var (home, _, epg, _) = HomeDataTests.Build();
            epg.MockSchedules["ch1"] = new List<EpgProgram>
            {
                Ep("a", "ch1", "Long Gone", -600, -300), // entirely before the window: dropped
                Ep("b", "ch1", "News", -10, 20),
                Ep("c", "ch1", "Quiz", 20, 50),
                Ep("d", "ch1", "Drama", 80, 140),        // a gap 50 → 80 before it
            };
            return (new GuideData(home, epg), epg);
        }

        [Fact]
        public void The_window_starts_on_the_half_hour_before_now_less_half_an_hour()
        {
            Assert.Equal(new DateTimeOffset(2026, 10, 26, 17, 30, 0, TimeSpan.Zero), GuideData.WindowStartFor(Now));
        }

        [Fact]
        public async Task Channels_are_numbered_and_in_order_like_on_now()
        {
            var (guide, _) = Build();
            var model = await guide.LoadAsync(Now);
            Assert.Equal(new[] { "101", "104" }, model.Channels.Select(c => c.Channel.Number));
        }

        [Fact]
        public async Task Schedules_are_mapped_windowed_and_gaps_are_filled_honestly()
        {
            var (guide, _) = Build();
            var model = await guide.LoadAsync(Now);
            var s = model.Channels[0].Schedule;
            Assert.Equal(model.Start, s[0].Start);
            Assert.True(s[0].NoInfo); // 17:30 → 18:00: before the first known programme
            Assert.Equal(new[] { "News", "Quiz" }, s.Skip(1).Take(2).Select(p => p.Title));
            Assert.Equal("desc", s[1].Synopsis);
            Assert.True(s[3].NoInfo);
            Assert.Equal(TimeSpan.FromMinutes(30), s[3].End - s[3].Start);
            Assert.Equal("Drama", s[4].Title);
            Assert.True(s[^1].NoInfo);
            Assert.Equal(model.End, s[^1].End);
            for (var i = 1; i < s.Count; i++) Assert.Equal(s[i - 1].End, s[i].Start); // gap-free
            Assert.DoesNotContain(s, p => p.Title == "Long Gone");
        }

        [Fact]
        public async Task A_load_is_shared_for_a_few_minutes_then_fetched_afresh()
        {
            var (guide, _) = Build();
            var first = await guide.LoadAsync(Now);
            Assert.Same(first, await guide.LoadAsync(Now.AddMinutes(4)));
            Assert.NotSame(first, await guide.LoadAsync(Now.AddMinutes(6)));
        }

        [Fact]
        public async Task A_channel_without_guide_data_is_one_no_information_span()
        {
            var (guide, _) = Build();
            var model = await guide.LoadAsync(Now);
            var s = Assert.Single(model.Channels[1].Schedule);
            Assert.True(s.NoInfo);
            Assert.Equal("No programme information", s.Title);
            Assert.Equal((model.Start, model.End), (s.Start, s.End));
        }

        [Fact]
        public async Task A_failing_or_silent_guide_still_lists_every_channel()
        {
            var (home, _, _, _) = HomeDataTests.Build();
            var model = await new GuideData(home, new BrokenEpg(), TimeSpan.FromMilliseconds(200)).LoadAsync(Now);
            Assert.Equal(2, model.Channels.Count);
            Assert.All(model.Channels, c => Assert.True(Assert.Single(c.Schedule).NoInfo));
        }

        private sealed class BrokenEpg : IEpgService
        {
            public Task<string?> GetCurrentProgrammeNameAsync(string channelId) => throw new InvalidOperationException();
            public Task<EpgProgram?> GetCurrentProgrammeAsync(string channelId) => throw new InvalidOperationException();
            public Task<IReadOnlyList<EpgProgram>> GetChannelScheduleAsync(string channelId, int limit = 20) =>
                channelId == "ch1" ? throw new InvalidOperationException("down") : new TaskCompletionSource<IReadOnlyList<EpgProgram>>().Task; // never answers
            public Task<IReadOnlyDictionary<string, IReadOnlyList<EpgProgram>>> GetBulkScheduleAsync(IEnumerable<string> ids, int limit = 10, CancellationToken ct = default) => throw new InvalidOperationException();
        }
    }
}
