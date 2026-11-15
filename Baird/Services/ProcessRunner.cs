using System;
using System.Diagnostics;
using System.Linq;

namespace Baird.Services
{
    public interface IProcessRunner { void Start(string file, string args); }

    public sealed class SystemProcessRunner : IProcessRunner
    {
        public void Start(string file, string args) =>
            Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false });
    }
}
