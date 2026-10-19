using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Baird.Services;
using Baird.ViewModels;
using Xunit;

namespace Baird.Tests.ViewModels
{
    public class SeerrchViewModelTests
    {
        /// <summary>
        /// Jellyseerr mock whose SearchAsync calls stay pending until the test completes them.
        /// </summary>
        private class ControllableJellyseerrService : IJellyseerrService
        {
            public ConcurrentQueue<TaskCompletionSource<IEnumerable<JellyseerrSearchResult>>> PendingSearches { get; } = new();

            public Task<IEnumerable<JellyseerrSearchResult>> SearchAsync(string query, int page, CancellationToken cancellationToken = default)
            {
                var tcs = new TaskCompletionSource<IEnumerable<JellyseerrSearchResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
                PendingSearches.Enqueue(tcs);
                return tcs.Task;
            }

            public Task<IEnumerable<JellyseerrSearchResult>> GetTrendingAsync(int page, CancellationToken cancellationToken = default)
                => Task.FromResult(Enumerable.Empty<JellyseerrSearchResult>());

            public Task<JellyseerrRequestResponse> CreateRequestAsync(int mediaId, string mediaType, CancellationToken cancellationToken = default)
                => Task.FromResult(new JellyseerrRequestResponse());

            public Task<IEnumerable<JellyseerrRequest>> GetRequestsAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(Enumerable.Empty<JellyseerrRequest>());
        }

        private static Task InvokePerformSearch(SeerrchViewModel vm, string query)
        {
            var method = typeof(SeerrchViewModel).GetMethod("PerformSearch", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("PerformSearch not found");
            return (Task)method.Invoke(vm, new object?[] { query })!;
        }

        [Fact]
        public async Task StaleSearchCompletion_DoesNotDisposeNewerSearchCts()
        {
            var service = new ControllableJellyseerrService();
            var vm = new SeerrchViewModel(service);

            // Search A starts and stays pending
            var searchA = InvokePerformSearch(vm, "alpha");
            Assert.True(service.PendingSearches.TryDequeue(out var tcsA));

            // Search B starts (cancels A) and stays pending
            var searchB = InvokePerformSearch(vm, "bravo");
            Assert.True(service.PendingSearches.TryDequeue(out var tcsB));

            // A completes after being superseded. Previously its finally block disposed
            // the CTS in the field, which by now belonged to search B.
            tcsA!.SetResult(Enumerable.Empty<JellyseerrSearchResult>());
            await searchA;

            // Search C must be able to cancel B's CTS without ObjectDisposedException
            // (the Cancel happens before the first await, so a failure faults the task immediately)
            var searchC = InvokePerformSearch(vm, "charlie");
            Assert.False(searchC.IsFaulted, searchC.Exception?.ToString());

            // Tidy up remaining pending searches
            tcsB!.TrySetResult(Enumerable.Empty<JellyseerrSearchResult>());
            while (service.PendingSearches.TryDequeue(out var pending))
            {
                pending.TrySetResult(Enumerable.Empty<JellyseerrSearchResult>());
            }
            await searchB;
            await searchC;
        }
    }
}
