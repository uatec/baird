using Microsoft.Extensions.Configuration;

namespace Baird.Services
{
    /// <summary>
    /// Netflix login from ~/.baird/config.ini (NETFLIX_USER / NETFLIX_PASS, same flat-key convention as TVH_USER).
    /// The password is only held in memory and typed into the login form; it must never be logged or shown.
    /// </summary>
    public sealed record NetflixCredentials(string User, string Password)
    {
        public const string UserKey = "NETFLIX_USER";
        public const string PasswordKey = "NETFLIX_PASS";

        public static NetflixCredentials? FromConfiguration(IConfiguration? config)
        {
            var user = config?[UserKey];
            var pass = config?[PasswordKey];
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(pass)) return null;
            return new NetflixCredentials(user.Trim(), pass);
        }

        // Keep the secret out of ToString()/logging by accident.
        public override string ToString() => $"NetflixCredentials(User={User}, Password=***)";
    }
}
