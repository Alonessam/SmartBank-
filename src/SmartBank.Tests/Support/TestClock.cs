namespace SmartBank.Tests.Support
{
    /// <summary>A clock the test moves by hand, so a rule that depends on time (an expiry, a cooldown, a schedule) needs no waiting.</summary>
    public sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now;

        public TestClock(DateTimeOffset? start = null) => _now = start ?? new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

        public DateTime UtcNow => _now.UtcDateTime;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;

        public void Set(DateTimeOffset value) => _now = value;
    }
}
