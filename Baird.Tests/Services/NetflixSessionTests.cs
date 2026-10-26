using System;
using System.Threading;
using System.Threading.Tasks;
using Baird.Services;
using Baird.Tests.ViewModels;
using Xunit;

namespace Baird.Tests.Services
{
    public class NetflixSessionTests
    {
        private static readonly NetflixCredentials Creds = new("viewer@example.com", "secret");

        internal static NetflixSessionState SignedOut() => new("https://www.netflix.com/login", true, false, Array.Empty<string>());
        internal static NetflixSessionState Member() => new("https://www.netflix.com/browse", false, true, Array.Empty<string>());
        internal static AuthPageStatus VerifyPage() => new("https://www.netflix.com/verify", "Verify", false, true, false, 0);
        private static AuthPageStatus BrowsePage() => new("https://www.netflix.com/browse", "Netflix", false, false, false, 0);

        private static (MockCdpBrowserService svc, NetflixSession session) Session(NetflixCredentials? creds = null)
        {
            var svc = new MockCdpBrowserService { SessionSequence = SignedOut, AuthStatusToReturn = BrowsePage() };
            var session = new NetflixSession(svc, creds)
            {
                PollInterval = TimeSpan.FromMilliseconds(5),
                SettleDelay = TimeSpan.FromMilliseconds(5),
                HandSignInTimeout = TimeSpan.FromSeconds(5),
            };
            return (svc, session);
        }

        [Fact]
        public async Task AFailedSignIn_IsNotTriedAgainInTheBackground_OnlyWhenTheViewerAsks()
        {
            var (svc, session) = Session(Creds); // Netflix stays signed out: the credentials do not work

            var first = await session.EnsureReadyAsync(CancellationToken.None);
            Assert.Contains("did not work", first);
            Assert.True(session.NeedsSignIn);
            Assert.Equal(1, svc.SubmitCredentialsCalls);

            Assert.Equal(first, await session.EnsureReadyAsync(CancellationToken.None)); // the watchdog, a search
            Assert.Equal(1, svc.SubmitCredentialsCalls);

            await session.EnsureReadyAsync(CancellationToken.None, signInAgain: true); // a play
            Assert.Equal(2, svc.SubmitCredentialsCalls);
        }

        [Fact]
        public async Task NoCredentials_SaysSo_AndNeedsASignIn()
        {
            var (svc, session) = Session();

            var failure = await session.EnsureReadyAsync(CancellationToken.None);

            Assert.Contains("NETFLIX_USER", failure);
            Assert.True(session.NeedsSignIn);
            Assert.False(session.NeedsVerificationCode);
            Assert.Equal(0, svc.SubmitCredentialsCalls);
        }

        [Fact]
        public async Task AVerificationStepAfterSigningIn_StaysOnThatPage_AndAsksForTheCode()
        {
            var (svc, session) = Session(Creds);
            svc.AuthStatusToReturn = VerifyPage();

            var failure = await session.EnsureReadyAsync(CancellationToken.None);

            Assert.Equal(NetflixSession.VerificationCodeMessage, failure);
            Assert.True(session.NeedsVerificationCode);
            Assert.Equal(NetflixSession.LoginUrl, svc.NavigatedUrls[^1]); // not sent on to /browse, which would lose the code
            Assert.Equal(1, svc.SubmitCredentialsCalls);
        }

        [Fact]
        public async Task AnAcceptedCode_GetsTheBrowserReady()
        {
            var (svc, session) = Session(Creds);
            svc.AuthStatusToReturn = VerifyPage();
            await session.EnsureReadyAsync(CancellationToken.None);

            svc.AuthStatusToReturn = BrowsePage();
            svc.SessionSequence = Member;
            var failure = await session.SubmitVerificationCodeAsync("123456", CancellationToken.None);

            Assert.Null(failure);
            Assert.Equal("123456", svc.LastSubmittedPin);
            Assert.False(session.NeedsVerificationCode);
            Assert.False(session.NeedsSignIn);
            Assert.Equal(NetflixSession.IdleUrl, svc.NavigatedUrls[^1]);
        }

        [Fact]
        public async Task ARefusedCode_CanBeTypedAgain()
        {
            var (svc, session) = Session(Creds);
            svc.AuthStatusToReturn = VerifyPage();
            await session.EnsureReadyAsync(CancellationToken.None);

            var failure = await session.SubmitVerificationCodeAsync("000000", CancellationToken.None);

            Assert.Equal(NetflixSession.CodeRefusedMessage, failure);
            Assert.True(session.NeedsVerificationCode);
            Assert.Equal(1, svc.SubmitCredentialsCalls); // no new sign-in (which would send another code)
        }

        [Fact]
        public async Task SignInByHand_ShowsNetflixsPage_UntilTheViewerHasSignedIn_ThenBairdIsBackOnTop()
        {
            var (svc, session) = Session();
            await session.EnsureReadyAsync(CancellationToken.None);
            var reads = 0;
            svc.SignInWatchSequence = () => ++reads < 3 ? SignedOut() : Member();
            svc.SessionSequence = Member;

            var failure = await session.SignInByHandAsync(CancellationToken.None);

            Assert.Null(failure);
            Assert.Contains(NetflixSession.LoginUrl, svc.NavigatedUrls);
            Assert.True(svc.RevealWindowCalled);
            Assert.True(svc.HideWindowCalled);
            Assert.False(session.NeedsSignIn);
            Assert.False(session.IsPlaybackActive);
            Assert.Null(await session.EnsureReadyAsync(CancellationToken.None));
            Assert.Equal(0, svc.SubmitCredentialsCalls); // Baird typed nothing
        }

        [Fact]
        public async Task SignInByHand_Back_GivesUp_AndBairdIsBackOnTop()
        {
            var (svc, session) = Session();
            svc.SignInWatchSequence = () => SignedOut() with { BackPressed = true };

            var failure = await session.SignInByHandAsync(CancellationToken.None);

            Assert.Contains("cancelled", failure);
            Assert.True(svc.HideWindowCalled);
            Assert.False(session.IsPlaybackActive);
        }
    }
}
