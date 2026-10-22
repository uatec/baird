using System;
using System.IO;
using System.Text.Json.Nodes;
using Baird.Services;
using Xunit;

namespace Baird.Tests.Services
{
    public class ChromiumProfilePrefsTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "prefs-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private JsonObject Read() => (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, "Default", "Preferences")))!;

        [Fact]
        public void CreatesPreferencesThatDisableThePasswordManager()
        {
            Assert.True(ChromiumProfilePrefs.DisablePasswordManager(_dir));
            var root = Read();
            Assert.False((bool)root["credentials_enable_service"]!);
            Assert.False((bool)root["profile"]!["password_manager_enabled"]!);
        }

        [Fact]
        public void MergesIntoExistingPreferencesWithoutLosingThem()
        {
            Directory.CreateDirectory(Path.Combine(_dir, "Default"));
            File.WriteAllText(Path.Combine(_dir, "Default", "Preferences"), "{\"profile\":{\"name\":\"Baird\",\"password_manager_enabled\":true},\"intl\":{\"x\":1}}");

            Assert.True(ChromiumProfilePrefs.DisablePasswordManager(_dir));
            var root = Read();
            Assert.Equal("Baird", (string)root["profile"]!["name"]!);
            Assert.Equal(1, (int)root["intl"]!["x"]!);
            Assert.False((bool)root["profile"]!["password_manager_enabled"]!);
        }

        [Fact]
        public void CorruptPreferences_AreReplacedNotThrown()
        {
            Directory.CreateDirectory(Path.Combine(_dir, "Default"));
            File.WriteAllText(Path.Combine(_dir, "Default", "Preferences"), "{not json");
            Assert.True(ChromiumProfilePrefs.DisablePasswordManager(_dir));
            Assert.False((bool)Read()["credentials_enable_service"]!);
        }
    }
}
