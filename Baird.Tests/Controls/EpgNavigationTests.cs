using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
    public class EpgNavigationTests
    {
        private static async Task Settle(int ms = 300)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(20);
            }
            Dispatcher.UIThread.RunJobs();
        }

        private static async Task<(Window Window, EpgControl Control)> Open(bool withSchedule)
        {
            var epg = new MockEpgService();
            if (withSchedule)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                epg.MockSchedules["chan-1"] = new List<EpgProgram>
                {
                    new() { Id = "p1", ChannelId = "chan-1", Title = "News", StartUnix = now - 600, StopUnix = now + 1200 },
                    new() { Id = "p2", ChannelId = "chan-1", Title = "Weather", StartUnix = now + 1200, StopUnix = now + 1500 },
                };
            }

            var dataService = new DataService(
                new IMediaProvider[] { new Baird.Tests.Mocks.MockMediaProvider("Streaming", Enumerable.Empty<MediaItemData>()) },
                new MockHistoryService(), new MockWatchlistService(), new MediaItemCache(), new MockMediaDataCache());
            var channels = new List<MediaItemViewModel>
            {
                new(new MediaItemData
                {
                    Id = "chan-1", Name = "BBC One HD", ChannelNumber = "1", IsLive = true, Source = "Live TV",
                    Type = MediaType.Channel, Details = "", Synopsis = "", Subtitle = "", ImageUrl = ""
                }),
            };
            var vm = new EpgViewModel(dataService, epg, () => channels);
            await vm.RefreshAsync();

            var control = new EpgControl { DataContext = vm };
            var window = new Window { Width = 1600, Height = 900, Content = control };
            window.Show();
            await Settle();
            return (window, control);
        }

        private static void Press(Window w, Key key) => w.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);

        [AvaloniaFact]
        public async Task Right_from_the_schedule_list_moves_to_the_Watch_button()
        {
            var (window, control) = await Open(withSchedule: true);
            control.FindControl<ListBox>("ScheduleList")!.GetVisualDescendants().OfType<ListBoxItem>().First().Focus();
            await Settle(100);

            Press(window, Key.Right);
            await Settle(100);

            Assert.True(control.FindControl<Button>("WatchButton")!.IsFocused);
        }

        [AvaloniaFact]
        public async Task Right_from_the_channel_list_moves_to_the_Watch_button_when_there_is_no_schedule()
        {
            var (window, control) = await Open(withSchedule: false);
            control.FindControl<ListBox>("ChannelsList")!.GetVisualDescendants().OfType<ListBoxItem>().First().Focus();
            await Settle(100);

            Press(window, Key.Right);
            await Settle(100);

            Assert.True(control.FindControl<Button>("WatchButton")!.IsFocused);
        }
    }
}
