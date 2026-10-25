namespace Baird.ViewModels
{
    /// <summary>
    /// A search screen whose query is built from the on-screen keyboard (or voice) rather than a text box.
    /// </summary>
    public interface ISearchTextInput
    {
        void AppendText(string text);
        void Backspace();
    }

    /// <summary>
    /// How long the query must sit unchanged before a search runs. Remote-control key presses are slow (often
    /// more than a second apart), so this is deliberately long; tests shorten it.
    /// </summary>
    public static class SearchTiming
    {
        public static TimeSpan Debounce { get; set; } = TimeSpan.FromMilliseconds(2000);

        /// <summary>How long one provider may take. The list is shown when all have answered or this has passed; a provider that did not answer shows an error dot.</summary>
        public static TimeSpan ProviderTimeout { get; set; } = TimeSpan.FromSeconds(10);
    }
}
