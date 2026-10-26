using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Baird.Services
{
    /// <summary>
    /// Persists a single key in an INI section, updating it in place if present, inserting it at the end of the
    /// section, or creating the section as needed. Every other line is preserved.
    /// </summary>
    public static class IniSettingWriter
    {
        /// <summary>~/.baird/config.ini, the user-level config Baird reads after ./config.ini.</summary>
        public static string DefaultPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".baird", "config.ini");

        public static void Save(string path, string section, string key, string value)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
            var header = $"[{section}]";

            var start = lines.FindIndex(l => l.Trim().Equals(header, StringComparison.OrdinalIgnoreCase));
            if (start < 0)
            {
                lines.Add(header);
                lines.Add($"{key}={value}");
            }
            else
            {
                var end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith("["));
                if (end < 0) end = lines.Count;
                // The key as the INI reader sees it: the name before '=', trimmed ("Ui = new" too), never a comment.
                var existing = lines.FindIndex(start + 1, end - start - 1, l =>
                {
                    var t = l.TrimStart();
                    return !t.StartsWith(";") && !t.StartsWith("#") && t.Contains('=')
                        && t.Split('=', 2)[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase);
                });
                if (existing >= 0) lines[existing] = $"{key}={value}";
                else
                {
                    // Insert after the section's last non-blank line, so a trailing blank line stays before the next section.
                    var at = end;
                    while (at - 1 > start && string.IsNullOrWhiteSpace(lines[at - 1])) at--;
                    lines.Insert(at, $"{key}={value}");
                }
            }
            File.WriteAllLines(path, lines);
        }
    }
}
