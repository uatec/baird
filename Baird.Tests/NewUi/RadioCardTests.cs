using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Baird.NewUi.Model;
using Baird.NewUi.Screens;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class RadioCardTests
    {
        private static readonly DateTimeOffset Noon = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        private static readonly Channel Radio = new("r2", "702", "BBC Radio 2", null, "http://tvh/702");

        private static string[] Texts(Control c) => c.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text ?? "").ToArray();

        [AvaloniaFact]
        public void Shows_the_channel_and_what_is_on_with_its_progress()
        {
            var card = new RadioCard();
            var now = new Programme("p", "r2", "Jeremy Vine", null, "Talk and phone-ins.", Noon.AddMinutes(-30), Noon.AddMinutes(30));
            card.Update(Radio, now, Noon);
            Assert.True(card.IsVisible);
            var texts = Texts(card);
            Assert.Contains("702  ·  BBC Radio 2", texts);
            Assert.Contains("Jeremy Vine", texts);
            Assert.Contains("Talk and phone-ins.", texts);
        }

        [AvaloniaFact]
        public void Without_guide_data_it_still_names_the_channel()
        {
            var card = new RadioCard();
            card.Update(Radio, null, Noon);
            Assert.Contains("No programme information", Texts(card));
            Assert.Contains("BBC Radio 2", Texts(card)); // the title falls back to the channel name
        }

        [AvaloniaFact]
        public void Is_hidden_when_there_is_no_radio_channel()
        {
            var card = new RadioCard();
            card.Update(Radio, null, Noon);
            card.Update(null, null, Noon);
            Assert.False(card.IsVisible);
        }
    }
}
