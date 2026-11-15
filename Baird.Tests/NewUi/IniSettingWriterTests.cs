using System;
using System.Collections.Generic;
using System.IO;
using Baird.Services;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class IniSettingWriterTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "baird-ini-" + Guid.NewGuid().ToString("N"));
        private string PathOf => System.IO.Path.Combine(_dir, "config.ini");
        public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

        [Fact]
        public void Creates_the_file_and_section()
        {
            IniSettingWriter.Save(PathOf, "Baird", "Ui", "new");
            Assert.Equal(new[] { "[Baird]", "Ui=new" }, File.ReadAllLines(PathOf));
        }

        [Fact]
        public void Updates_an_existing_key_in_place_and_keeps_other_lines()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllLines(PathOf, new[] { "TVH_URL=http://x", "[Baird]", "UiScale=1.2", "Ui=classic", "[Mpv]", "hwdec=no" });
            IniSettingWriter.Save(PathOf, "Baird", "Ui", "new");
            Assert.Equal(new[] { "TVH_URL=http://x", "[Baird]", "UiScale=1.2", "Ui=new", "[Mpv]", "hwdec=no" }, File.ReadAllLines(PathOf));
        }

        [Fact]
        public void Inserts_into_the_section_before_the_next_one()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllLines(PathOf, new[] { "[Baird]", "UiScale=1.2", "[Mpv]", "hwdec=no" });
            IniSettingWriter.Save(PathOf, "Baird", "Ui", "new");
            Assert.Equal(new[] { "[Baird]", "UiScale=1.2", "Ui=new", "[Mpv]", "hwdec=no" }, File.ReadAllLines(PathOf));
        }

        [Fact]
        public void Updates_a_hand_written_key_with_spaces_instead_of_adding_a_duplicate()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllLines(PathOf, new[] { "[Baird]", "; Ui=old comment", "Ui = new", "UiScale=1.4" });
            IniSettingWriter.Save(PathOf, "Baird", "Ui", "classic");
            Assert.Equal(new[] { "[Baird]", "; Ui=old comment", "Ui=classic", "UiScale=1.4" }, File.ReadAllLines(PathOf));
        }

        [Fact]
        public void Does_not_mistake_a_longer_key_for_the_one_being_saved()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllLines(PathOf, new[] { "[Baird]", "UiScale=1.4" });
            IniSettingWriter.Save(PathOf, "Baird", "Ui", "new");
            Assert.Equal(new[] { "[Baird]", "UiScale=1.4", "Ui=new" }, File.ReadAllLines(PathOf));
        }

        [Fact]
        public void Appends_to_the_section_when_it_is_last()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllLines(PathOf, new[] { "X=1", "[Baird]", "UiScale=1.2" });
            IniSettingWriter.Save(PathOf, "Baird", "Ui", "new");
            Assert.Equal(new[] { "X=1", "[Baird]", "UiScale=1.2", "Ui=new" }, File.ReadAllLines(PathOf));
        }
    }
}
