using System;

namespace Baird.NewUi.Model
{
    /// <summary>
    /// A reminder for a broadcast (persisted as JSON, so a plain class with settable properties for the source
    /// generator). A minute before <see cref="Start"/> a card asks, then switches at the start unless dismissed.
    /// </summary>
    public sealed class Reminder
    {
        public string ProgrammeId { get; set; } = "";
        public string ChannelId { get; set; } = "";
        public string ChannelName { get; set; } = "";
        public string ChannelNumber { get; set; } = "";
        public string Title { get; set; } = "";
        public DateTimeOffset Start { get; set; }
        public DateTimeOffset End { get; set; }
    }

    /// <summary>The guide's two layouts: channels down the side (time across), or channels across the top (time down).</summary>
    public enum GuideLayout { Rows, Columns }
}
