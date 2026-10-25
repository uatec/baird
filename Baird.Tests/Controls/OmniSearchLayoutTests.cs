using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Baird.Controls;
using Baird.Models;
using Baird.Services;
using Baird.Tests.Mocks;
using Baird.ViewModels;
using Xunit;

namespace Baird.Tests.Controls
{
    public class OmniSearchLayoutTests
    {
        private class StubSearchHistory : ISearchHistoryService
        {
            private readonly string[] _terms;
            public StubSearchHistory(params string[] terms) => _terms = terms;
            public Task AddSearchTermAsync(string term) => Task.CompletedTask;
            public Task<IEnumerable<string>> GetSuggestedTermsAsync(int maxCount) => Task.FromResult<IEnumerable<string>>(_terms);
        }

        private record Fixture(Window Window, OmniSearchControl Control, OmniSearchViewModel Vm, VirtualKeyboardControl Keyboard);

        private static async Task Settle(int ms = 400)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(20);
            }
            Dispatcher.UIThread.RunJobs();
        }

        private static async Task<Fixture> Open(params string[] suggestions)
        {
            var items = Enumerable.Range(0, 8).Select(i => new MediaItemData
            {
                Id = "i" + i,
                Name = "Alpha " + i,
                Details = "",
                ImageUrl = "",
                IsLive = false,
                Source = "P",
                Type = MediaType.Video,
                Synopsis = "",
                Subtitle = ""
            }).ToList();

            var dataService = new DataService(
                new IMediaProvider[] { new Mocks.MockMediaProvider("P", items) },
                new MockHistoryService(), new MockWatchlistService(),
                new MediaItemCache(), new MockMediaDataCache());
            SearchTiming.Debounce = TimeSpan.FromMilliseconds(300);
            var vm = new OmniSearchViewModel(dataService, new StubSearchHistory(suggestions), () => new List<MediaItemViewModel>());

            var control = new OmniSearchControl { DataContext = vm };
            var window = new Window { Width = 1600, Height = 900, Content = control };
            window.Show();
            await Settle();
            return new Fixture(window, control, vm, control.FindControl<VirtualKeyboardControl>("VirtualKeyboard")!);
        }

        private static Button Key(Fixture f, string content) =>
            f.Keyboard.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == content);

        private static object? Focused(Fixture f) => TopLevel.GetTopLevel(f.Control)!.FocusManager!.GetFocusedElement();

        private static void Press(Fixture f, Key key) =>
            f.Window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);

        private static IEnumerable<Button> ResultButtons(Fixture f) =>
            f.Control.FindControl<ItemsControl>("ResultsList")!.GetVisualDescendants().OfType<Button>();

        [AvaloniaFact]
        public async Task Pressing_keyboard_keys_builds_the_query_and_shows_it_in_the_text_block()
        {
            var f = await Open();

            Key(f, "A").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Key(f, "L").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Key(f, "P").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Key(f, "⌫").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal("al", f.Vm.SearchText);
            Assert.Equal("al", f.Control.FindControl<TextBlock>("SearchText")!.Text);
        }

        [AvaloniaFact]
        public async Task The_search_text_is_not_an_editable_text_box()
        {
            var f = await Open();
            Assert.Empty(f.Control.GetVisualDescendants().OfType<TextBox>());
        }

        [AvaloniaFact]
        public async Task Focus_stays_on_the_key_while_typing_and_results_come_and_go()
        {
            var f = await Open();
            var keyA = Key(f, "A");
            keyA.Focus();
            await Settle(100);

            foreach (var ch in new[] { "A", "L", "P" })
            {
                Key(f, ch).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Settle(700); // debounce, search, results arrive
                Assert.True(ResultButtons(f).Any(), $"no results after '{ch}'");
                Assert.True(keyA.IsFocused, $"focus left the keyboard after '{ch}' (now {Focused(f)?.GetType().Name})");
            }
        }

        [AvaloniaFact]
        public async Task Right_from_the_keyboards_edge_moves_to_the_results_and_Left_returns_to_the_same_key()
        {
            var f = await Open();
            Key(f, "A").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle(700);

            var m = Key(f, "M"); // last key on its row
            m.Focus();
            Press(f, Avalonia.Input.Key.Right);
            await Settle(100);

            var first = ResultButtons(f).First();
            Assert.True(first.IsFocused, $"expected first result, got {Focused(f)?.GetType().Name}");

            Press(f, Avalonia.Input.Key.Left);
            await Settle(100);
            Assert.True(m.IsFocused, "Left from the first column should return to the key last used");
        }

        [AvaloniaFact]
        public async Task Right_from_the_edge_does_nothing_when_there_are_no_results()
        {
            var f = await Open();
            var m = Key(f, "M");
            m.Focus();

            Press(f, Avalonia.Input.Key.Right);
            await Settle(100);

            Assert.True(m.IsFocused);
        }

        [AvaloniaFact]
        public async Task Enter_key_on_the_keyboard_moves_to_the_results()
        {
            var f = await Open();
            Key(f, "A").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle(700);

            Key(f, "ENTER").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle(100);

            Assert.True(ResultButtons(f).First().IsFocused);
        }

        [AvaloniaFact]
        public async Task Up_from_the_top_row_goes_to_the_suggestions_and_Down_comes_back_to_the_keyboard()
        {
            var f = await Open("doctor who");
            var q = Key(f, "1");
            q.Focus();

            Press(f, Avalonia.Input.Key.Up);
            await Settle(100);
            var pill = f.Control.FindControl<ItemsControl>("SuggestionsList")!.GetVisualDescendants().OfType<Button>().First();
            Assert.True(pill.IsFocused, $"expected suggestion pill, got {Focused(f)?.GetType().Name}");

            Press(f, Avalonia.Input.Key.Down);
            await Settle(100);
            Assert.True(q.IsFocused);
        }

        private class HangingProvider : IMediaProvider
        {
            public string Name => "Hang";
            public Task InitializeAsync() => Task.CompletedTask;
            public Task<IEnumerable<MediaItemData>> GetListingAsync() => Task.FromResult(Enumerable.Empty<MediaItemData>());
            public Task<IEnumerable<MediaItemData>> SearchAsync(string query, System.Threading.CancellationToken cancellationToken = default) =>
                new TaskCompletionSource<IEnumerable<MediaItemData>>().Task; // never answers and ignores the token
            public Task<IEnumerable<MediaItemData>> GetChildrenAsync(string id) => Task.FromResult(Enumerable.Empty<MediaItemData>());
            public Task<MediaItemData?> GetItemAsync(string id) => Task.FromResult<MediaItemData?>(null);
        }

        [AvaloniaFact]
        public async Task A_provider_that_never_answers_does_not_hold_the_list_back()
        {
            var fast = new Mocks.MockMediaProvider("P", new[]
            {
                new MediaItemData { Id = "a", Name = "Alpha", Details = "", ImageUrl = "", IsLive = false, Source = "P", Type = MediaType.Video, Synopsis = "", Subtitle = "" }
            });
            var dataService = new DataService(new IMediaProvider[] { fast, new HangingProvider() },
                new MockHistoryService(), new MockWatchlistService(), new MediaItemCache(), new MockMediaDataCache());
            SearchTiming.Debounce = TimeSpan.FromMilliseconds(50);
            SearchTiming.ProviderTimeout = TimeSpan.FromMilliseconds(300);
            try
            {
                var vm = new OmniSearchViewModel(dataService, new StubSearchHistory(), () => new List<MediaItemViewModel>());
                vm.SearchText = "alpha";

                await Settle(150);
                Assert.Empty(vm.SearchResults); // the hanging provider has not answered yet: nothing is shown
                Assert.True(vm.ShowSpinner);

                await Settle(900);
                Assert.Single(vm.SearchResults);
                Assert.False(vm.ShowSpinner);
                Assert.True(vm.ProviderStatuses.First(p => p.Name == "Hang").IsError);
            }
            finally
            {
                SearchTiming.ProviderTimeout = TimeSpan.FromSeconds(10);
            }
        }

        [AvaloniaFact]
        public async Task The_on_screen_keyboard_has_a_key_for_every_digit()
        {
            var f = await Open();

            foreach (var digit in "1234567890")
                Key(f, digit.ToString()).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal("1234567890", f.Vm.SearchText);
        }

        [AvaloniaFact]
        public async Task Remote_number_keys_append_digits_to_the_query()
        {
            var f = await Open();
            Key(f, "F").Focus();

            Press(f, Avalonia.Input.Key.D1);
            Press(f, Avalonia.Input.Key.NumPad0);

            Assert.Equal("10", f.Vm.SearchText);
        }
    }

    public class SearchTextInputTests
    {
        [Fact]
        public void Seerrch_AppendText_and_Backspace_edit_the_query()
        {
            ISearchTextInput vm = new SeerrchViewModel(new MockJellyseerrService());
            vm.AppendText("ab");
            vm.Backspace();
            Assert.Equal("a", ((SeerrchViewModel)vm).SearchText);
            vm.Backspace();
            vm.Backspace(); // no-op on empty
            Assert.Equal("", ((SeerrchViewModel)vm).SearchText);
        }
    }
}
