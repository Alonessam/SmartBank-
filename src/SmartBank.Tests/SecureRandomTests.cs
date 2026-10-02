using SmartBank.Core.Common;

namespace SmartBank.Tests
{
    public class SecureRandomTests
    {
        [Fact]
        public void Next_Always_Returns_A_Value_Inside_The_Range()
        {
            for (var i = 0; i < 5000; i++)
            {
                var value = SecureRandom.Next(100000, 1000000);
                Assert.InRange(value, 100000, 999999);
            }
        }

        [Fact]
        public void Next_Throws_When_Range_Is_Empty()
        {
            // RandomNumberGenerator.GetInt32 rejects an empty range with a plain ArgumentException.
            Assert.Throws<ArgumentException>(() => SecureRandom.Next(10, 10));
            Assert.Throws<ArgumentException>(() => SecureRandom.Next(10, 5));
        }

        [Fact]
        public void Next_Covers_The_Whole_Range_Roughly_Evenly()
        {
            const int draws = 20000;
            var buckets = new int[10];

            for (var i = 0; i < draws; i++)
            {
                buckets[SecureRandom.Next(0, 10)]++;
            }

            // Expected 2000 per bucket, standard deviation about 42, so a 20% band is ~9 sigma wide.
            Assert.All(buckets, count => Assert.InRange(count, 1600, 2400));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(3)]
        [InlineData(15)]
        [InlineData(16)]
        public void Digits_Returns_The_Requested_Number_Of_Decimal_Digits(int length)
        {
            var value = SecureRandom.Digits(length);

            Assert.Equal(length, value.Length);
            Assert.All(value, c => Assert.InRange(c, '0', '9'));
        }

        [Fact]
        public void Digits_Throws_For_A_Negative_Length()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => SecureRandom.Digits(-1));
        }

        [Fact]
        public void Digits_Does_Not_Repeat_Itself()
        {
            // 100 sixteen-digit strings: a collision has probability around 5e-13.
            var values = Enumerable.Range(0, 100).Select(_ => SecureRandom.Digits(16)).ToHashSet();

            Assert.Equal(100, values.Count);
        }
    }
}
