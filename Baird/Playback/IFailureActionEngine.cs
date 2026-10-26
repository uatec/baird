using System.Threading;
using System.Threading.Tasks;

namespace Baird.Playback
{
    /// <summary>
    /// An engine whose failure can come with something the viewer can do about it, offered on the failure screen as "Press OK to
    /// ...". Channel 4: "sign in to Channel 4", when its page asks for a sign-in. Optional: most engines have none.
    /// </summary>
    public interface IFailureActionEngine : IBrowserPlaybackEngine
    {
        /// <summary>What OK does after the last failure ("sign in to Channel 4"), or null when the failure offers nothing.</summary>
        string? FailureAction { get; }

        /// <summary>
        /// Does it (the service's own page may cover Baird meanwhile). Returns null when the title can be played again now, else why
        /// not, for the viewer.
        /// </summary>
        Task<string?> RunFailureActionAsync(CancellationToken ct = default);
    }
}
