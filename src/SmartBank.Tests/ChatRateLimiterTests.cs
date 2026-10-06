using Microsoft.Extensions.Configuration;
using SmartBank.API.Security;

namespace SmartBank.Tests
{
    public class ChatRateLimiterTests
    {
        private sealed class ManualTime : TimeProvider
        {
            private DateTimeOffset _now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
            public override DateTimeOffset GetUtcNow() => _now;
            public void Advance(TimeSpan by) => _now += by;
        }

        private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

        [Fact]
        public void Allows_up_to_the_limit_and_then_refuses()
        {
            var limiter = new ChatRateLimiter(new ManualTime());

            for (var i = 0; i < 3; i++) Assert.True(limiter.TryAcquire("u1", 3, Minute));

            Assert.False(limiter.TryAcquire("u1", 3, Minute));
            Assert.False(limiter.TryAcquire("u1", 3, Minute));
        }

        [Fact]
        public void The_allowance_returns_as_the_window_slides()
        {
            var time = new ManualTime();
            var limiter = new ChatRateLimiter(time);
            for (var i = 0; i < 3; i++) limiter.TryAcquire("u1", 3, Minute);

            time.Advance(TimeSpan.FromSeconds(59));
            Assert.False(limiter.TryAcquire("u1", 3, Minute));

            time.Advance(TimeSpan.FromSeconds(2));
            Assert.True(limiter.TryAcquire("u1", 3, Minute));
        }

        [Fact]
        public void Refused_attempts_do_not_extend_the_block()
        {
            var time = new ManualTime();
            var limiter = new ChatRateLimiter(time);
            limiter.TryAcquire("u1", 1, Minute);

            for (var i = 0; i < 20; i++)
            {
                time.Advance(TimeSpan.FromSeconds(2));
                limiter.TryAcquire("u1", 1, Minute); // hammering while blocked
            }

            time.Advance(TimeSpan.FromSeconds(30));
            Assert.True(limiter.TryAcquire("u1", 1, Minute));
        }

        [Fact]
        public void Users_do_not_share_an_allowance()
        {
            var limiter = new ChatRateLimiter(new ManualTime());
            Assert.True(limiter.TryAcquire("u1", 1, Minute));
            Assert.False(limiter.TryAcquire("u1", 1, Minute));

            Assert.True(limiter.TryAcquire("u2", 1, Minute));
        }

        [Fact]
        public void Different_actions_have_separate_counters()
        {
            var limiter = new ChatRateLimiter(new ManualTime());
            Assert.True(limiter.TryAcquire("msg:u1", 1, Minute));

            Assert.True(limiter.TryAcquire("transfer:u1", 1, Minute));
        }

        [Fact]
        public async Task Parallel_callers_never_get_more_than_the_limit()
        {
            var limiter = new ChatRateLimiter(new ManualTime());

            var results = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() => limiter.TryAcquire("u1", 10, Minute))));

            Assert.Equal(10, results.Count(r => r));
        }

        [Fact]
        public void Settings_have_sane_defaults_and_can_be_overridden()
        {
            var defaults = ChatSettings.From(new ConfigurationBuilder().Build());
            Assert.Equal(10, defaults.MessagesPerMinute);
            Assert.Equal(100, defaults.MessagesPerHour);
            Assert.Equal(10, defaults.SessionsPerHour);
            Assert.Equal(5, defaults.TransfersPerMinute);

            var custom = ChatSettings.From(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Chat:MessagesPerMinute"] = "2" }).Build());
            Assert.Equal(2, custom.MessagesPerMinute);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("-3")]
        [InlineData("abc")]
        [InlineData("100001")]
        public void Nonsense_limits_stop_the_app_from_starting(string value)
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Chat:MessagesPerMinute"] = value }).Build();

            Assert.Throws<InvalidOperationException>(() => ChatSettings.From(config));
        }
    }
}
