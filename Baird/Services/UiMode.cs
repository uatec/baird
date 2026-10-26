using System;
using Microsoft.Extensions.Configuration;

namespace Baird.Services
{
    /// <summary>Which UI Baird starts into. Set from Settings (Baird:Ui in ~/.baird/config.ini); BAIRD_UI overrides it.</summary>
    public enum UiMode { Classic, New }

    public static class UiModes
    {
        public const string SettingKey = "Ui";

        public static UiMode Read(IConfiguration config)
        {
            var value = config["BAIRD_UI"] is { Length: > 0 } env ? env : config[$"Baird:{SettingKey}"];
            return string.Equals(value?.Trim(), "new", StringComparison.OrdinalIgnoreCase) ? UiMode.New : UiMode.Classic;
        }

        public static string ToSetting(UiMode mode) => mode == UiMode.New ? "new" : "classic";

        /// <summary>Saves the choice to ~/.baird/config.ini.</summary>
        public static void Save(UiMode mode, string? path = null) =>
            IniSettingWriter.Save(path ?? IniSettingWriter.DefaultPath, "Baird", SettingKey, ToSetting(mode));
    }
}
