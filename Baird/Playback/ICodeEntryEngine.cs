using System;
using System.Threading.Tasks;

namespace Baird.Playback
{
    /// <summary>
    /// An engine whose service can stop a play to ask for a code the viewer was sent: Netflix's verification code after a
    /// sign-in. <see cref="Baird.Controls.VideoPlayer"/> shows the request, the viewer types the digits with the remote's number
    /// keys, and OK sends them. Optional: most engines never ask.
    /// </summary>
    public interface ICodeEntryEngine : IBrowserPlaybackEngine
    {
        /// <summary>What the service is asking for now, for the viewer, or null when it is not asking.</summary>
        string? CodeRequest { get; }

        /// <summary>The request appeared, changed or went away. Can fire on any thread.</summary>
        event EventHandler? CodeRequestChanged;

        /// <summary>
        /// Sends the code the viewer typed. The play then carries on, asks again (the code did not work) or fails. Does nothing
        /// when no code is asked for.
        /// </summary>
        Task SubmitCodeAsync(string code);
    }
}
