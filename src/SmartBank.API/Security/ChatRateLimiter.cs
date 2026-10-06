using System.Collections.Concurrent;

namespace SmartBank.API.Security
{
    /// <summary>
    /// Sliding-window limiter for the support chat. ASP.NET's rate-limiting middleware only sees the HTTP request that opens
    /// a SignalR connection, not the hub methods called over it, so a signed-in user could send unlimited messages (each one
    /// triggers an AI call and a database write). Counts are kept in memory per instance, like the "auth" policy.
    /// </summary>
    public sealed class ChatRateLimiter
    {
        private const int CleanupThreshold = 5000;

        private sealed class Window
        {
            public readonly Queue<DateTime> Hits = new();
            public TimeSpan Length;
        }

        private readonly ConcurrentDictionary<string, Window> _windows = new();
        private readonly TimeProvider _time;

        public ChatRateLimiter(TimeProvider time) => _time = time;

        /// <summary>Records an attempt and returns true if fewer than <paramref name="limit"/> happened in the last <paramref name="window"/>.</summary>
        public bool TryAcquire(string key, int limit, TimeSpan window)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            if (_windows.Count > CleanupThreshold) Cleanup(now);

            var state = _windows.GetOrAdd(key, _ => new Window());
            lock (state)
            {
                state.Length = window;
                while (state.Hits.Count > 0 && now - state.Hits.Peek() >= window)
                {
                    state.Hits.Dequeue();
                }

                if (state.Hits.Count >= limit) return false;

                state.Hits.Enqueue(now);
                return true;
            }
        }

        private void Cleanup(DateTime now)
        {
            foreach (var (key, state) in _windows)
            {
                lock (state)
                {
                    if (state.Hits.Count == 0 || now - state.Hits.Last() >= state.Length)
                    {
                        _windows.TryRemove(key, out _);
                    }
                }
            }
        }
    }
}
