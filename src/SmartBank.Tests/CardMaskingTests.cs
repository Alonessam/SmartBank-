using SmartBank.Core.Common;

namespace SmartBank.Tests
{
    public class CardMaskingTests
    {
        [Theory]
        [InlineData("4111111111111111", "1111")]
        [InlineData("4111 1111 1111 9876", "9876")]
        [InlineData("1234", "1234")]
        [InlineData("123", "****")]
        [InlineData("", "****")]
        [InlineData(null, "****")]
        public void LastFour_Returns_The_Last_Four_Digits_Or_A_Mask(string? input, string expected)
        {
            Assert.Equal(expected, CardMasking.LastFour(input));
        }
    }
}
