namespace Baird.NewUi.Model
{
    public enum SourceHealth { Ok, Warning, Down, Off }

    /// <summary>A source's state for Settings: "Jellyfin · Connected" with a dot.</summary>
    public sealed record SourceStatus(string Name, string Text, SourceHealth Health);
}
