using System.Linq;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Baird.NewUi.Controls;
using Baird.NewUi.Core;
using Baird.NewUi.Screens;
using Baird.Services;
using Xunit;

namespace Baird.Tests.NewUi
{
    /// <summary>Settings › Phones and the Connect a phone page.</summary>
    public class PhonesSettingsTests
    {
        private int _connected = 2, _opened, _forgot;

        private SettingsScreen Settings() => new(() => UiMode.New, _ => { }, 1.0, _ => { }, options: new SettingsOptions(
            () => true, _ => { }, () => true, _ => { },
            Phones: new PhoneSettings(() => _connected, () => _opened++, () => { _forgot++; _connected = 0; })));

        private static void GoTo(SettingsScreen s, string label)
        {
            for (var i = 0; i < 30 && ((ListRow)s.LitElement!).LabelText != label; i++) s.OnAction(UiAction.Down, false);
            Assert.Equal(label, ((ListRow)s.LitElement!).LabelText);
        }

        [AvaloniaFact]
        public void Connect_a_phone_opens_the_codes()
        {
            var s = Settings();
            GoTo(s, "Connect a phone");
            s.OnAction(UiAction.Ok, false);
            Assert.Equal(1, _opened);
        }

        [AvaloniaFact]
        public void Connected_phones_says_how_many_and_ok_forgets_them()
        {
            var s = Settings();
            Assert.Contains(("Connected phones", "2 · OK to forget them"), s.RowTexts);
            GoTo(s, "Connected phones");
            s.OnAction(UiAction.Ok, false);
            Assert.Equal(1, _forgot);
            s.OnAction(UiAction.Ok, false); // none left: nothing to forget
            Assert.Equal(1, _forgot);
        }

        [AvaloniaFact]
        public void Without_phones_there_are_no_phone_rows() =>
            Assert.DoesNotContain(new SettingsScreen(() => UiMode.New, _ => { }, 1.0, _ => { }, options: new SettingsOptions(() => true, _ => { }, () => true, _ => { })).RowTexts,
                r => r.Label.Contains("phone"));

        [AvaloniaFact]
        public void The_page_shows_two_codes_and_issues_a_token()
        {
            var issued = 0;
            var page = new PhonesPage("http://192.168.1.20:8099", () => { issued++; return "abc"; });
            Assert.Equal(1, issued);
            Assert.Equal(2, page.View.GetLogicalDescendantsOfQr());
            Assert.False(page.OnAction(UiAction.Back, false));
            Assert.True(page.OnAction(UiAction.Ok, false));
        }

        [AvaloniaFact]
        public void Off_the_home_network_the_page_explains_instead_of_showing_codes()
        {
            var issued = 0;
            var page = new PhonesPage(null, () => { issued++; return "abc"; });
            Assert.Equal(0, issued);
            Assert.Equal(0, page.View.GetLogicalDescendantsOfQr());
        }

        /// <summary>PHONES_SHOT=/path.png renders the page (to check the codes scan, e.g. with zbarimg).</summary>
        [AvaloniaFact]
        public void Renders_the_page_when_asked()
        {
            if (System.Environment.GetEnvironmentVariable("PHONES_SHOT") is not { Length: > 0 } path) return;
            var page = new PhonesPage("http://192.168.1.20:8099", () => "0123456789abcdef0123456789abcdef0123456789abcdef");
            var w = new Avalonia.Controls.Window { Width = 1920, Height = 1080, Content = page.View, Background = Avalonia.Media.Brushes.Black };
            w.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(w);
            frame!.Save(path);
            w.Close();
        }

        [AvaloniaFact]
        public void The_pairing_code_holds_the_address_and_token()
        {
            using var doc = JsonDocument.Parse(PhonesPage.PairingText("http://192.168.1.20:8099", "abc"));
            Assert.Equal("http://192.168.1.20:8099", doc.RootElement.GetProperty("baird").GetString());
            Assert.Equal("abc", doc.RootElement.GetProperty("token").GetString());
        }
    }

    internal static class QrSearch
    {
        /// <summary>QR codes in a control tree (logical: the page isn't on a window, so has no visual tree yet).</summary>
        public static int GetLogicalDescendantsOfQr(this Avalonia.Controls.Control c) =>
            Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(c).OfType<QrCode>().Count();
    }
}
