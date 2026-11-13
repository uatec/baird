using System;

namespace Baird.NewUi.Model
{
    /// <summary>Plain records the new UI works with. The legacy service types never appear above the adapters.</summary>
    public sealed record Channel(string Id, string Number, string Name, string? LogoUrl, string StreamUrl);

    /// <summary>A broadcast. <paramref name="NoInfo"/> marks a stretch the guide has nothing for (shown honestly, still watchable).</summary>
    public sealed record Programme(string Id, string ChannelId, string Title, string? Subtitle, string? Synopsis, DateTimeOffset Start, DateTimeOffset End, bool NoInfo = false,
        string? ImageUrl = null)
    {
        public bool IsOnAt(DateTimeOffset t) => Start <= t && t < End;

        public double ProgressAt(DateTimeOffset now) =>
            End <= Start ? 0 : Math.Clamp((now - Start).TotalSeconds / (End - Start).TotalSeconds, 0, 1);
    }

    public enum CardKind { Episode, Film, Series, Channel }

    /// <summary>Where an item plays: in mpv (TvHeadend, Jellyfin, iPlayer, YouTube) or in the browser (Netflix, Disney+).</summary>
    public enum PlayVia { Mpv, Browser, None }

    /// <summary>
    /// A card on Home. Payload carries what playing it needs: a <see cref="Channel"/> (with its current programme in
    /// <see cref="Programme"/>), or an item id + stream URL + resume point. <see cref="Title"/> is the title it belongs to,
    /// when known (search results), so OK can open its show page.
    /// </summary>
    public sealed record HomeCard(
        CardKind Kind, string Id, string Label, string? Sublabel, string? ImageUrl, double? Progress, string? Badge,
        string Source, PlayVia Via, string? StreamUrl = null, TimeSpan Resume = default, Channel? Channel = null, Programme? Programme = null,
        string? Synopsis = null, CatalogueTitle? Title = null, string? Show = null)
    {
        /// <summary>What to call it while it loads: an episode under its show's name ("The Simpsons"), else its own.</summary>
        public string Heading => Kind == CardKind.Episode && Show is { Length: > 0 } s ? s : Label;

        /// <summary>The line under <see cref="Heading"/>: the episode's own title, when the heading is the show's.</summary>
        public string? Subheading => Heading != Label ? Label : null;
    }
}
