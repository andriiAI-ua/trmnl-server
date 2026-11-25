using System.Collections.Concurrent;

namespace Trmnl.Server.Services
{
    public class JournalService
    {
        private readonly ConcurrentDictionary<string, int> _journal = new ConcurrentDictionary<string, int> { };

        public void LogRequest(string deviceId, string key)
        {
            var internalKey = $"{deviceId}:{key}";
            if (_journal.TryGetValue(internalKey, out int count))
            {
                _journal[internalKey] = count + 1;
            }
            _journal[internalKey] = 1;
        }

        public bool AlreadyRecorded(string deviceId, string key)
        {
            return _journal.Any(kvp => kvp.Key == $"{deviceId}:{key}");
        }
    }
}
