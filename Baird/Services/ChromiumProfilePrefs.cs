using System;
using System.IO;
using System.Text.Json.Nodes;

namespace Baird.Services
{
    /// <summary>
    /// Pre-seeds Chromium profile preferences so the automated Netflix sign-in never ends in a "Save password?" bubble that
    /// nobody can click on a TV. (There is no working command-line switch for this in current Chromium.)
    /// Must run while the browser is not running; it merges into any existing Preferences file.
    /// </summary>
    public static class ChromiumProfilePrefs
    {
        public static bool DisablePasswordManager(string userDataDir)
        {
            try
            {
                var dir = Path.Combine(userDataDir, "Default");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "Preferences");

                JsonObject root = new();
                if (File.Exists(path))
                {
                    try { root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject(); }
                    catch { root = new JsonObject(); } // unreadable prefs: Chromium would reset them anyway
                }

                root["credentials_enable_service"] = false;
                root["credentials_enable_autosignin"] = false;
                var profile = root["profile"] as JsonObject ?? new JsonObject();
                profile["password_manager_enabled"] = false;
                root["profile"] = profile;

                File.WriteAllText(path, root.ToJsonString());
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
