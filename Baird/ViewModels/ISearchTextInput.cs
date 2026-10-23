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
}
