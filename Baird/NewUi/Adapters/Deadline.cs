using System;
using System.Threading;
using System.Threading.Tasks;

namespace Baird.NewUi.Adapters
{
    /// <summary>Bounds a call: past its time, or when it fails, you get the fallback (and a log line); only the caller's own cancellation propagates.</summary>
    internal static class Deadline
    {
        public static async Task<T> Within<T>(Func<CancellationToken, Task<T>> call, T fallback, TimeSpan timeout, string what, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                var task = call(cts.Token);
                var done = await Task.WhenAny(task, Task.Delay(timeout, cts.Token)).ConfigureAwait(false);
                if (done == task) return await task.ConfigureAwait(false);
                Console.WriteLine($"[Deadline] {what} didn't answer within {timeout.TotalSeconds:0.###} s");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Console.WriteLine($"[Deadline] {what} failed: {ex.Message}"); }
            finally { cts.Cancel(); }
            return fallback;
        }
    }
}
