namespace Baird.NewUi.Model
{
    public enum SourceHealth { Ok, Warning, Down, Off }

    /// <summary>
    /// A browser service's account as far as Baird knows it: not set up; set up but not confirmed yet (it signs in on
    /// first use); signed in (seen); or needing the viewer to sign in (Baird tried and couldn't).
    /// </summary>
    public enum AccountState { NotSetUp, SetUp, SignedIn, NeedsSignIn }

    /// <summary>A source's state for Settings: "Jellyfin · Connected" with a dot.</summary>
    public sealed record SourceStatus(string Name, string Text, SourceHealth Health);
}
