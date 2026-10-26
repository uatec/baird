using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Baird.NewUi.Model;
using Baird.NewUi.Playback;

namespace Baird.NewUi.Core
{
    /// <summary>
    /// The viewer's reminders, saved as JSON at an injected path (null = in memory, for tests). A reminder is due once,
    /// a minute before it starts; ones that started over a minute ago are dropped.
    /// </summary>
    public sealed class Reminders
    {
        public static string DefaultPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".baird", "reminders.json");
        public static readonly TimeSpan Warning = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan Keep = TimeSpan.FromMinutes(1);

        private readonly string? _path;
        private readonly Func<DateTimeOffset> _now;
        private readonly List<Reminder> _items;
        private readonly HashSet<string> _shown = new();

        public event Action? Changed;

        public Reminders(string? path, Func<DateTimeOffset> now)
        {
            _path = path; _now = now;
            _items = Load();
            if (Prune()) Save();
        }

        public IReadOnlyList<Reminder> All => _items.OrderBy(r => r.Start).ToList();
        public bool Has(string programmeId) => _items.Any(r => r.ProgrammeId == programmeId);

        /// <summary>Set a reminder for <paramref name="p"/>, or cancel it if set. True when it is now set.</summary>
        public bool Toggle(Programme p, Channel ch)
        {
            var on = !Has(p.Id);
            if (on) _items.Add(new Reminder { ProgrammeId = p.Id, ChannelId = ch.Id, ChannelName = ch.Name, ChannelNumber = ch.Number, Title = p.Title, Start = p.Start, End = p.End });
            else _items.RemoveAll(r => r.ProgrammeId == p.Id);
            Save();
            Changed?.Invoke();
            return on;
        }

        public void Remove(string programmeId)
        {
            if (_items.RemoveAll(r => r.ProgrammeId == programmeId) == 0) return;
            Save();
            Changed?.Invoke();
        }

        /// <summary>The next reminder to warn about (once each): within a minute of its start, and not long started.</summary>
        public Reminder? Due()
        {
            var now = _now();
            if (Prune()) { Save(); Changed?.Invoke(); }
            var due = _items.OrderBy(r => r.Start).FirstOrDefault(r => !_shown.Contains(r.ProgrammeId) && r.Start - now <= Warning && now - r.Start < TimeSpan.FromSeconds(30));
            if (due != null) _shown.Add(due.ProgrammeId);
            return due;
        }

        private bool Prune() => _items.RemoveAll(r => _now() - r.Start > Keep) > 0;

        private List<Reminder> Load()
        {
            if (_path == null || !File.Exists(_path)) return new();
            try { return JsonSerializer.Deserialize(File.ReadAllText(_path), BairdJsonContext.Default.ListReminder) ?? new(); }
            catch (Exception ex) { Console.WriteLine($"[Reminders] Couldn't read {_path}: {ex.Message}"); return new(); }
        }

        private void Save()
        {
            if (_path == null) return;
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_path, JsonSerializer.Serialize(_items, BairdJsonContext.Default.ListReminder));
            }
            catch (Exception ex) { Console.WriteLine($"[Reminders] Couldn't save {_path}: {ex.Message}"); }
        }
    }

    /// <summary>Checks for due reminders every few seconds and puts the reminder card up, over whatever is showing.</summary>
    public sealed class ReminderClock
    {
        private readonly Reminders _reminders;
        private readonly Navigator _nav;
        private readonly ITimers _timers;
        private readonly TimeSpan _every;

        public ReminderClock(Reminders reminders, Navigator nav, ITimers timers, TimeSpan? every = null)
        {
            _reminders = reminders; _nav = nav; _timers = timers; _every = every ?? TimeSpan.FromSeconds(5);
        }

        public void Start() => _timers.After(_every, Tick);

        private void Tick()
        {
            try { Check(); }
            catch (Exception ex) { Console.WriteLine($"[Reminders] Check failed: {ex.Message}"); }
            _timers.After(_every, Tick);
        }

        public void Check()
        {
            if (_nav.State.Overlay?.Name == "reminder") return; // one at a time; the next comes after this one is answered
            if (_reminders.Due() is { } r) _nav.OpenOverlay(new OverlayRef("reminder", r));
        }
    }
}
