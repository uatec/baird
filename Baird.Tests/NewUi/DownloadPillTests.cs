using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Baird.NewUi.Controls;
using Baird.NewUi.Model;
using Baird.NewUi.Theme;
using Xunit;

namespace Baird.Tests.NewUi
{
    /// <summary>Each Downloads state is a solid, readable pill: amber waiting, lavender not out, blue moving, green here, red problem.</summary>
    public class DownloadPillTests
    {
        private static (string Text, IBrush Back, IBrush Fore) PillOf(string badge)
        {
            var card = new Card(new HomeCard(CardKind.Series, "x", "Show", "S1", null, null, badge, "Seerr", PlayVia.None), CardShape.Poster);
            var pill = card.GetLogicalDescendants().OfType<Border>().First(b => b.CornerRadius.TopLeft >= 900 && b.Child is TextBlock);
            var text = (TextBlock)pill.Child!;
            return (text.Text!, pill.Background!, text.Foreground!);
        }

        [AvaloniaTheory]
        [InlineData("requested", "Requested", "Waiting")]
        [InlineData("searching", "Searching", "Waiting")]
        [InlineData("out:18 Dec", "Out 18 Dec", "Soon")]
        [InlineData("dl:42", "⤓ 42%", "Downloading")]
        [InlineData("part", "◐ Part ready", "Ready")]
        [InlineData("ready", "✓ Ready", "Ready")]
        [InlineData("complete", "✓✓ Complete", "Complete")]
        [InlineData("notfound", "Not found", "Missing")]
        [InlineData("declined", "Declined", "Declined")]
        public void Each_state_has_its_own_words_and_colour(string badge, string text, string colour)
        {
            var (t, back, _) = PillOf(badge);
            Assert.Equal(text, t);
            var expected = (IBrush)typeof(T).GetField(colour)!.GetValue(null)!;
            Assert.Equal(((ISolidColorBrush)expected).Color, ((ISolidColorBrush)back).Color);
        }

        [AvaloniaTheory]
        [InlineData("requested")] [InlineData("searching")] [InlineData("out:18 Dec")] [InlineData("dl:42")] [InlineData("part")]
        [InlineData("ready")] [InlineData("complete")] [InlineData("notfound")] [InlineData("declined")]
        public void Pill_text_is_easy_to_read_on_its_colour(string badge)
        {
            var (_, back, fore) = PillOf(badge);
            static double L(Color c) { static double f(byte v) { var x = v / 255.0; return x <= 0.03928 ? x / 12.92 : System.Math.Pow((x + 0.055) / 1.055, 2.4); } return 0.2126 * f(c.R) + 0.7152 * f(c.G) + 0.0722 * f(c.B); }
            var a = L(((ISolidColorBrush)back).Color); var b = L(((ISolidColorBrush)fore).Color);
            var contrast = (System.Math.Max(a, b) + 0.05) / (System.Math.Min(a, b) + 0.05);
            Assert.True(contrast >= 4.5, $"{badge}: contrast {contrast:0.0}");
        }
    }
}
