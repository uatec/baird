using Microsoft.Extensions.Configuration;

namespace Baird.Services
{
    /// <summary>
    /// Disney+ login from ~/.baird/config.ini (DISNEY_USER / DISNEY_PASS, same flat-key convention as NETFLIX_USER).
    /// The password is only held in memory and typed into the login form; it must never be logged or shown.
    /// </summary>
    public sealed record DisneyCredentials(string User, string Password)
    {
        public const string UserKey = "DISNEY_USER";
        public const string PasswordKey = "DISNEY_PASS";

        public static DisneyCredentials? FromConfiguration(IConfiguration? config)
        {
            var user = config?[UserKey];
            var pass = config?[PasswordKey];
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(pass)) return null;
            return new DisneyCredentials(user.Trim(), pass);
        }

        // Keep the secret out of ToString()/logging by accident.
        public override string ToString() => $"DisneyCredentials(User={User}, Password=***)";
    }
}
