using System;
using System.IO;
using Baird.Mpv;
using Xunit;

namespace Baird.Tests.Mpv
{
    public class YtDlpTempDirTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "ytdlptmp-" + Guid.NewGuid().ToString("N"));
        public YtDlpTempDirTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private string Make(string name, TimeSpan age)
        {
            var d = Path.Combine(_dir, name);
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "f"), "x");
            Directory.SetLastWriteTimeUtc(d, DateTime.UtcNow - age);
            return d;
        }

        [Fact]
        public void RemovesOnlyStaleMeiFolders()
        {
            var stale = Make("_MEIold123", TimeSpan.FromHours(2));
            var fresh = Make("_MEInew456", TimeSpan.FromMinutes(1));
            var other = Make("keepme", TimeSpan.FromHours(5));

            Assert.Equal(1, YtDlpTempDir.CleanStale(_dir, TimeSpan.FromMinutes(30)));
            Assert.False(Directory.Exists(stale));
            Assert.True(Directory.Exists(fresh)); // may belong to a running yt-dlp
            Assert.True(Directory.Exists(other)); // not ours
        }

        [Fact]
        public void MissingDirectory_IsFine()
        {
            Assert.Equal(0, YtDlpTempDir.CleanStale(Path.Combine(_dir, "nope"), TimeSpan.FromMinutes(30)));
        }
    }
}
