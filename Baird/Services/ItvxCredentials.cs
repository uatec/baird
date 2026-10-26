using Microsoft.Extensions.Configuration;

namespace Baird.Services
{
    /// <summary>
    /// ITVX login from ~/.baird/config.ini (ITVX_USER / ITVX_PASS, same flat-key convention as DISNEY_USER).
    /// The password is only held in memory and typed into the sign-in form; it must never be logged or shown.
    /// </summary>
    public sealed record ItvxCredentials(string User, string Password)
    {
        public const string UserKey = "ITVX_USER";
        public const string PasswordKey = "ITVX_PASS";

        public static ItvxCredentials? FromConfiguration(IConfiguration? config)
        {
            var user = config?[UserKey];
            var pass = config?[PasswordKey];
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(pass)) return null;
            return new ItvxCredentials(user.Trim(), pass);
        }

        // Keep the secret out of ToString()/logging by accident.
        public override string ToString() => $"ItvxCredentials(User={User}, Password=***)";
    }
}
