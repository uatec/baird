using System;
using System.IO;

namespace Baird.NewUi.Lifecycle
{
    /// <summary>
    /// Tells the auto-updater (a separate process with no way to ask Baird) whether anyone could be watching: a one-word file,
    /// "idle" (screensaver up, or the TV in standby) or "active". update_baird.sh restarts Baird only when it says idle,
    /// or in its overnight window.
    /// </summary>
    public sealed class FileIdleState : IIdleState
    {
        public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".baird", "state");
        private readonly string _path;
        private bool? _last;

        public FileIdleState(string? path = null) => _path = path ?? DefaultPath;

        public void Set(bool idle)
        {
            if (_last == idle) return;
            _last = idle;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, idle ? "idle\n" : "active\n");
                File.Move(tmp, _path, true);   // never a half-written file for the updater to read
            }
            catch (Exception ex) { Console.WriteLine($"[TvLifecycle] Couldn't write {_path}: {ex.Message}"); }
        }
    }
}
