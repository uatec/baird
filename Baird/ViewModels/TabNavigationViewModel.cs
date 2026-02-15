using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;

namespace Baird.ViewModels
{
    public class TabItem : ReactiveObject
    {
        private string _title;
        public string Title
        {
            get => _title;
            set => this.RaiseAndSetIfChanged(ref _title, value);
        }

        private ReactiveObject _content;
        public ReactiveObject Content
        {
            get => _content;
            set => this.RaiseAndSetIfChanged(ref _content, value);
        }

        public TabItem(string title, ReactiveObject content)
        {
            _title = title;
            _content = content;
        }
    }

    public class TabNavigationViewModel : ReactiveObject
    {
        public ObservableCollection<TabItem> Tabs { get; }

        public event EventHandler? BackRequested;

        private TabItem? _selectedTab;
        public TabItem? SelectedTab
        {
            get => _selectedTab;
            set => this.RaiseAndSetIfChanged(ref _selectedTab, value);
        }

        private int _selectedIndex;
        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                this.RaiseAndSetIfChanged(ref _selectedIndex, value);
                if (value >= 0 && value < Tabs.Count)
                {
                    SelectedTab = Tabs[value];
                }
            }
        }

        public TabNavigationViewModel(IEnumerable<TabItem> tabs)
        {
            Tabs = new ObservableCollection<TabItem>(tabs);

            // Select first tab by default
            if (Tabs.Count > 0)
            {
                SelectedIndex = 0;
            }

            SelectTabCommand = ReactiveCommand.Create<TabItem>(tab =>
            {
                var index = Tabs.IndexOf(tab);
                if (index >= 0)
                {
                    SelectedIndex = index;
                }
            });

            BackCommand = ReactiveCommand.Create(() =>
            {
                BackRequested?.Invoke(this, EventArgs.Empty);
            });
        }

        public ReactiveCommand<TabItem, Unit> SelectTabCommand { get; }
        public ReactiveCommand<Unit, Unit> BackCommand { get; }
    }
}
