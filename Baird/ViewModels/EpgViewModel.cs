using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Baird.Models;
using Baird.Services;
using ReactiveUI;

namespace Baird.ViewModels
{
    public class ChannelScheduleViewModel : ReactiveObject
    {
        public MediaItemViewModel Channel { get; }

        public bool IsLive => Channel.IsLive;
        public string Source => Channel.Source;
        public string ChannelNumber => Channel.ChannelNumber ?? string.Empty;
        public string Name => Channel.Name;
        public string ImageUrl => Channel.ImageUrl;

        public string DisplayBadge => IsLive ? "LIVE" : (Channel.Type == MediaType.Brand ? "SERIES" : "VIDEO");

        public string SourceBadge => !string.IsNullOrEmpty(Channel.Source) ? Channel.Source : (IsLive ? "Live TV" : "Streaming");

        private EpgProgram? _now;
        public EpgProgram? Now
        {
            get => _now;
            set => this.RaiseAndSetIfChanged(ref _now, value);
        }

        private EpgProgram? _next;
        public EpgProgram? Next
        {
            get => _next;
            set => this.RaiseAndSetIfChanged(ref _next, value);
        }

        public ObservableCollection<EpgProgram> Schedule { get; } = new();

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set => this.RaiseAndSetIfChanged(ref _isLoading, value);
        }

        public ChannelScheduleViewModel(MediaItemViewModel channel)
        {
            Channel = channel;
        }

        public void UpdateSchedule(IReadOnlyList<EpgProgram> programs)
        {
            Schedule.Clear();
            foreach (var prog in programs)
            {
                Schedule.Add(prog);
            }

            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Now = programs.FirstOrDefault(p => p.StartUnix <= nowUnix && nowUnix < p.StopUnix)
                ?? programs.FirstOrDefault();

            if (Now != null)
            {
                Next = programs.FirstOrDefault(p => p.StartUnix >= Now.StopUnix)
                    ?? programs.SkipWhile(p => p.Id != Now.Id).Skip(1).FirstOrDefault();
            }
            else
            {
                Next = programs.Skip(1).FirstOrDefault();
            }

            // Sync with channel tile's CurrentProgrammeName
            if (Now != null && !string.IsNullOrWhiteSpace(Now.Title))
            {
                Channel.CurrentProgrammeName = Now.Title;
            }
        }

        public void LoadStreamingEpisodes(IEnumerable<MediaItemViewModel> episodes)
        {
            Schedule.Clear();
            var epList = episodes.ToList();

            if (epList.Count == 0)
            {
                // Single movie or video item without children
                var singleProg = new EpgProgram
                {
                    Id = Channel.Id,
                    ChannelId = Channel.Id,
                    ChannelName = Channel.Name,
                    Title = Channel.Name,
                    Subtitle = Channel.Subtitle,
                    Description = Channel.Synopsis,
                    Duration = Channel.Duration,
                    StreamingProgress = Channel.Progress,
                    IsStreamingEpisode = true,
                    MediaItem = Channel
                };
                Schedule.Add(singleProg);
                Now = singleProg;
                Next = null;
                return;
            }

            foreach (var ep in epList)
            {
                var prog = new EpgProgram
                {
                    Id = ep.Id,
                    ChannelId = Channel.Id,
                    ChannelName = Channel.Name,
                    Title = ep.Name,
                    Subtitle = !string.IsNullOrWhiteSpace(ep.Subtitle) ? ep.Subtitle : ep.Details,
                    Description = ep.Synopsis,
                    Duration = ep.Duration,
                    StreamingProgress = ep.Progress,
                    IsStreamingEpisode = true,
                    MediaItem = ep
                };
                Schedule.Add(prog);
            }

            // Now represents the next unwatched episode or in-progress episode
            Now = Schedule.FirstOrDefault(p => p.Progress > 0.0 && p.Progress < 0.95)
                ?? Schedule.FirstOrDefault(p => p.Progress == 0.0)
                ?? Schedule.FirstOrDefault();

            if (Now != null)
            {
                Next = Schedule.SkipWhile(p => p.Id != Now.Id).Skip(1).FirstOrDefault();
                Channel.CurrentProgrammeName = Now.Title;
            }
        }
    }

    public class EpgViewModel : ReactiveObject
    {
        private readonly IDataService _dataService;
        private readonly IEpgService _epgService;
        private readonly Func<List<MediaItemViewModel>>? _getChannels;

        public ObservableCollection<ChannelScheduleViewModel> Channels { get; } = new();

        private readonly List<ChannelScheduleViewModel> _allFeeds = new();

        public ObservableCollection<string> FilterOptions { get; } = new() { "All", "Live TV", "Streaming" };

        private string _selectedFilter = "All";
        public string SelectedFilter
        {
            get => _selectedFilter;
            set
            {
                this.RaiseAndSetIfChanged(ref _selectedFilter, value);
                ApplyFilter();
            }
        }

        private string _channelNumberFilter = "";
        /// <summary>Digits typed on the remote: the guide lists only the live channels whose number starts with them.</summary>
        public string ChannelNumberFilter
        {
            get => _channelNumberFilter;
            set
            {
                this.RaiseAndSetIfChanged(ref _channelNumberFilter, value);
                this.RaisePropertyChanged(nameof(HasChannelNumberFilter));
                ApplyFilter();
            }
        }

        public bool HasChannelNumberFilter => _channelNumberFilter.Length > 0;

        public void AppendChannelDigit(string digit) => ChannelNumberFilter += digit;

        /// <summary>Removes the last typed digit; false when there was none (the key then keeps its other meaning).</summary>
        public bool BackspaceChannelDigit()
        {
            if (_channelNumberFilter.Length == 0) return false;
            ChannelNumberFilter = _channelNumberFilter[..^1];
            return true;
        }

        private ChannelScheduleViewModel? _selectedChannel;
        public ChannelScheduleViewModel? SelectedChannel
        {
            get => _selectedChannel;
            set
            {
                this.RaiseAndSetIfChanged(ref _selectedChannel, value);
                if (value != null)
                {
                    SelectedProgram = value.Now ?? value.Schedule.FirstOrDefault();
                    if (!value.IsLive && value.Schedule.Count == 0)
                    {
                        _ = LoadStreamingScheduleAsync(value);
                    }
                }
                this.RaisePropertyChanged(nameof(PlayButtonText));
            }
        }

        private EpgProgram? _selectedProgram;
        public EpgProgram? SelectedProgram
        {
            get => _selectedProgram;
            set
            {
                this.RaiseAndSetIfChanged(ref _selectedProgram, value);
                this.RaisePropertyChanged(nameof(PlayButtonText));
            }
        }

        public string PlayButtonText
        {
            get
            {
                if (SelectedChannel == null) return "▶ Watch";
                if (SelectedChannel.IsLive) return "▶ Watch Channel";
                if (SelectedProgram != null && !string.IsNullOrWhiteSpace(SelectedProgram.Title))
                    return $"▶ Play: {SelectedProgram.DisplayTitle}";
                return "▶ Play Show";
            }
        }

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set => this.RaiseAndSetIfChanged(ref _isLoading, value);
        }

        public event EventHandler<MediaItemViewModel>? PlayRequested;
        public event EventHandler? BackRequested;

        public ReactiveCommand<MediaItemViewModel, Unit> PlayChannelCommand { get; }
        public ReactiveCommand<ChannelScheduleViewModel, Unit> SelectChannelCommand { get; }
        public ReactiveCommand<EpgProgram, Unit> SelectProgramCommand { get; }
        public ReactiveCommand<string, Unit> SetFilterCommand { get; }
        public ReactiveCommand<Unit, Unit> BackCommand { get; }
        public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

        public EpgViewModel(IDataService dataService, IEpgService epgService, Func<List<MediaItemViewModel>>? getChannels = null)
        {
            _dataService = dataService;
            _epgService = epgService;
            _getChannels = getChannels;

            PlayChannelCommand = ReactiveCommand.Create<MediaItemViewModel>(channel =>
            {
                if (SelectedProgram?.MediaItem != null)
                {
                    PlayRequested?.Invoke(this, SelectedProgram.MediaItem);
                }
                else
                {
                    PlayRequested?.Invoke(this, channel);
                }
            });

            SelectChannelCommand = ReactiveCommand.Create<ChannelScheduleViewModel>(channelSchedule =>
            {
                SelectedChannel = channelSchedule;
            });

            SelectProgramCommand = ReactiveCommand.Create<EpgProgram>(program =>
            {
                SelectedProgram = program;
            });

            SetFilterCommand = ReactiveCommand.Create<string>(filter =>
            {
                SelectedFilter = filter;
            });

            BackCommand = ReactiveCommand.Create(() =>
            {
                ChannelNumberFilter = "";
                BackRequested?.Invoke(this, EventArgs.Empty);
            });

            RefreshCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                await RefreshAsync();
            });
        }

        private void ApplyFilter()
        {
            var currentId = SelectedChannel?.Channel.Id;
            Channels.Clear();

            var filtered = _allFeeds.Where(feed =>
            {
                if (ChannelNumberFilter.Length > 0 &&
                    !(feed.IsLive && (feed.Channel.ChannelNumber ?? "").StartsWith(ChannelNumberFilter, StringComparison.Ordinal))) return false;
                if (SelectedFilter == "Live TV") return feed.IsLive;
                if (SelectedFilter == "Streaming") return !feed.IsLive;
                return true;
            });

            foreach (var item in filtered)
            {
                Channels.Add(item);
            }

            if (Channels.Count > 0)
            {
                SelectedChannel = Channels.FirstOrDefault(c => c.Channel.Id == currentId) ?? Channels[0];
            }
        }

        private async Task LoadStreamingScheduleAsync(ChannelScheduleViewModel feed)
        {
            try
            {
                var children = await _dataService.GetChildrenAsync(feed.Channel.Id);
                var epList = children.Where(c => c.Type != MediaType.Folder && c.Type != MediaType.Brand).ToList();
                feed.LoadStreamingEpisodes(epList);

                if (SelectedChannel == feed)
                {
                    SelectedProgram = feed.Now ?? feed.Schedule.FirstOrDefault();
                    this.RaisePropertyChanged(nameof(PlayButtonText));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EpgViewModel] Error loading episodes for {feed.Name}: {ex.Message}");
            }
        }

        public async Task RefreshAsync()
        {
            IsLoading = true;
            try
            {
                // 1. Fetch Live TV channels
                List<MediaItemViewModel> liveChannels;
                if (_getChannels != null)
                {
                    liveChannels = _getChannels();
                }
                else
                {
                    var listing = await _dataService.GetListingAsync();
                    liveChannels = listing
                        .Where(i => i.IsLive && i.ChannelNumber != null && i.ChannelNumber != "0")
                        .OrderBy(i =>
                        {
                            if (int.TryParse(i.ChannelNumber, out var num)) return num;
                            return int.MaxValue;
                        })
                        .ToList();
                }

                if (liveChannels.Count == 0)
                {
                    var listing = await _dataService.GetListingAsync();
                    liveChannels = listing.Where(i => i.IsLive).ToList();
                }

                // 2. Fetch Streaming Shows & Series (equal prominence)
                var allListing = await _dataService.GetListingAsync();
                var streamingShows = allListing
                    .Where(i => !i.IsLive)
                    .OrderBy(i => i.Name)
                    .ToList();

                // 3. Assemble unified feeds
                _allFeeds.Clear();
                foreach (var ch in liveChannels)
                {
                    _allFeeds.Add(new ChannelScheduleViewModel(ch));
                }
                foreach (var show in streamingShows)
                {
                    _allFeeds.Add(new ChannelScheduleViewModel(show));
                }

                ApplyFilter();

                // 4. Bulk fetch schedule for live channels in parallel
                var channelIds = liveChannels.Select(c => c.Id).ToList();
                var schedules = await _epgService.GetBulkScheduleAsync(channelIds, limitPerChannel: 10);

                foreach (var chVm in _allFeeds.Where(f => f.IsLive))
                {
                    if (schedules.TryGetValue(chVm.Channel.Id, out var progs) && progs.Count > 0)
                    {
                        chVm.UpdateSchedule(progs);
                    }
                    else
                    {
                        var name = await _epgService.GetCurrentProgrammeNameAsync(chVm.Channel.Id);
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                            var fallback = new EpgProgram
                            {
                                Id = $"{chVm.Channel.Id}_now",
                                ChannelId = chVm.Channel.Id,
                                ChannelName = chVm.Channel.Name,
                                ChannelNumber = chVm.Channel.ChannelNumber,
                                Title = name,
                                StartUnix = nowUnix - 600,
                                StopUnix = nowUnix + 1800
                            };
                            chVm.UpdateSchedule(new[] { fallback });
                        }
                    }
                }

                // 5. Preload first few streaming shows
                foreach (var streamVm in _allFeeds.Where(f => !f.IsLive).Take(3))
                {
                    _ = LoadStreamingScheduleAsync(streamVm);
                }

                // Refresh SelectedProgram
                if (SelectedChannel != null)
                {
                    SelectedProgram = SelectedChannel.Now ?? SelectedChannel.Schedule.FirstOrDefault();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EpgViewModel] Error refreshing EPG: {ex.Message}");
            }
            finally
            {
                IsLoading = false;
            }
        }
    }
}
