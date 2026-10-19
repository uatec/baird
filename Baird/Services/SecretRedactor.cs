using System.Collections.Concurrent;

namespace Baird.Services
{
    /// <summary>
    /// Process-wide registry of secret values (passwords, API keys, access tokens).
    /// Providers register secrets as they learn them; anything written to logs or disk
    /// goes through <see cref="Redact"/> so the raw values never leave memory.
    /// </summary>
    public static class SecretRedactor
    {
        private const string Mask = "***";
        private const int MinSecretLength = 4; // avoid masking trivially short values like "a"

        private const string PlaceholderPrefix = "{{secret:";

        private static readonly ConcurrentDictionary<string, byte> _secrets = new();

        // Named secrets (name -> current value) so persisted data can store a stable
        // placeholder instead of the value, and pick up the latest value when read back.
        private static readonly ConcurrentDictionary<string, string> _named = new();

        /// <summary>
        /// Registers a secret under a stable name. Re-registering a name (e.g. a refreshed
        /// access token) updates the value used by <see cref="Detokenize"/>; old values stay
        /// masked by <see cref="Redact"/>.
        /// </summary>
        public static void Register(string name, string? secret)
        {
            Register(secret);
            if (string.IsNullOrEmpty(secret) || secret.Length < MinSecretLength) return;
            _named[name] = secret;
        }

        public static void Register(string? secret)
        {
            if (string.IsNullOrEmpty(secret) || secret.Length < MinSecretLength) return;
            _secrets[secret] = 0;
            // Secrets also appear URL-escaped inside query strings.
            var escaped = Uri.EscapeDataString(secret);
            if (escaped != secret) _secrets[escaped] = 0;
        }

        public static string Redact(string? text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
            foreach (var secret in _secrets.Keys)
            {
                if (text.Contains(secret, StringComparison.Ordinal))
                    text = text.Replace(secret, Mask, StringComparison.Ordinal);
            }
            return text;
        }

        /// <summary>
        /// Replaces named secret values with "{{secret:NAME}}" placeholders (URL-escaped
        /// occurrences become "{{secret:NAME:url}}"). Use before persisting to disk.
        /// </summary>
        public static string Tokenize(string? text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
            foreach (var (name, secret) in _named)
            {
                // Escaped form first: the raw value can be a substring of its escaped form.
                var escaped = Uri.EscapeDataString(secret);
                if (escaped != secret && text.Contains(escaped, StringComparison.Ordinal))
                    text = text.Replace(escaped, $"{PlaceholderPrefix}{name}:url}}}}", StringComparison.Ordinal);
                if (text.Contains(secret, StringComparison.Ordinal))
                    text = text.Replace(secret, $"{PlaceholderPrefix}{name}}}}}", StringComparison.Ordinal);
            }
            return text;
        }

        /// <summary>
        /// Inverse of <see cref="Tokenize"/>: substitutes the current value of each named secret.
        /// Placeholders for secrets not (yet) registered are left as-is.
        /// </summary>
        public static string Detokenize(string? text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
            if (!text.Contains(PlaceholderPrefix, StringComparison.Ordinal)) return text;
            foreach (var (name, secret) in _named)
            {
                text = text.Replace($"{PlaceholderPrefix}{name}:url}}}}", Uri.EscapeDataString(secret), StringComparison.Ordinal);
                text = text.Replace($"{PlaceholderPrefix}{name}}}}}", secret, StringComparison.Ordinal);
            }
            return text;
        }

        public static bool ContainsPlaceholder(string? text) =>
            text != null && text.Contains(PlaceholderPrefix, StringComparison.Ordinal);
    }
}
