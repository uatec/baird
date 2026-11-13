using System;
using System.Threading.Tasks;
using Baird.Services;
using Xunit;

namespace Baird.Tests
{
    public class TunerRecoveryTests
    {
        private static (TunerRecovery R, Func<int> Restarts, Action<TimeSpan> Advance) Build(bool succeeds = true, TaskCompletionSource<bool>? gate = null)
        {
            var now = DateTimeOffset.UnixEpoch; var restarts = 0;
            var r = new TunerRecovery(() => { restarts++; return gate?.Task ?? Task.FromResult(succeeds); }, () => now);
            return (r, () => restarts, by => now += by);
        }

        [Fact]
        public async Task Restarts_TvHeadend_the_first_time()
        {
            var (r, restarts, _) = Build();
            Assert.True(await r.TryRecoverAsync());
            Assert.Equal(1, restarts());
        }

        [Fact]
        public async Task Does_not_restart_again_within_the_cooldown_even_if_the_first_failed()
        {
            var (r, restarts, advance) = Build(succeeds: false);
            Assert.False(await r.TryRecoverAsync());
            advance(TunerRecovery.Cooldown - TimeSpan.FromSeconds(1));
            Assert.False(await r.TryRecoverAsync());
            Assert.Equal(1, restarts());
        }

        [Fact]
        public async Task Restarts_again_after_the_cooldown()
        {
            var (r, restarts, advance) = Build();
            await r.TryRecoverAsync();
            advance(TunerRecovery.Cooldown);
            Assert.True(await r.TryRecoverAsync());
            Assert.Equal(2, restarts());
        }

        [Fact]
        public async Task Ignores_a_second_request_while_one_is_running()
        {
            var gate = new TaskCompletionSource<bool>();
            var (r, restarts, _) = Build(gate: gate);
            var first = r.TryRecoverAsync();
            Assert.False(await r.TryRecoverAsync());
            gate.SetResult(true);
            Assert.True(await first);
            Assert.Equal(1, restarts());
        }
    }
}
