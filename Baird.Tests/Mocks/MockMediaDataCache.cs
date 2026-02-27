using System.Collections.Generic;
using Baird.Models;
using Baird.Services;

namespace Baird.Tests.Mocks
{
    /// <summary>
    /// In-memory mock of IMediaDataCache for use in tests.
    /// </summary>
    public class MockMediaDataCache : IMediaDataCache
    {
        private readonly Dictionary<string, MediaItemData> _data = new();

        public bool TryGet(string id, out MediaItemData? data) => _data.TryGetValue(id, out data);

        public void Put(MediaItemData data) => _data[data.Id] = data;
    }
}
