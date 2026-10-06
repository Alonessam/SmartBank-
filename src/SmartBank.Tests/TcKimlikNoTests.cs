using SmartBank.Core.Common;
using SmartBank.Core.DTOs;
using SmartBank.Core.Validators;

namespace SmartBank.Tests
{
    public class TcKimlikNoTests
    {
        [Theory]
        [InlineData("11111111110")]
        [InlineData("10000000146")]
        [InlineData("98765432150")]
        public void Numbers_with_matching_check_digits_are_valid(string value)
        {
            Assert.True(TcKimlikNo.IsValid(value));
        }

        [Theory]
        [InlineData("11111111111")] // last digit wrong
        [InlineData("11111111120")] // tenth digit wrong
        [InlineData("12345678901")]
        [InlineData("99999999999")]
        [InlineData("00000000000")]
        [InlineData("01234567890")] // leading zero is never valid
        public void Numbers_with_wrong_check_digits_are_invalid(string value)
        {
            Assert.False(TcKimlikNo.IsValid(value));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("1111111111")]   // 10 digits
        [InlineData("111111111100")] // 12 digits
        [InlineData("1111111111a")]
        [InlineData("1111 111 1110")]
        [InlineData("-1111111110")]
        public void Anything_that_is_not_eleven_digits_is_invalid(string? value)
        {
            Assert.False(TcKimlikNo.IsValid(value));
        }

        [Fact]
        public void Changing_any_single_digit_of_a_valid_number_makes_it_invalid()
        {
            // The two check digits catch every single-digit typing mistake.
            const string valid = "10000000146";
            for (var position = 0; position < 11; position++)
            {
                for (var digit = 0; digit <= 9; digit++)
                {
                    if (valid[position] - '0' == digit) continue;
                    var mistyped = valid.Remove(position, 1).Insert(position, digit.ToString());
                    Assert.False(TcKimlikNo.IsValid(mistyped), $"{mistyped} should be rejected");
                }
            }
        }

        [Fact]
        public void The_test_helper_produces_valid_unique_numbers()
        {
            var numbers = Enumerable.Range(0, 500).Select(_ => TestTckn.Next()).ToList();

            Assert.All(numbers, n => Assert.True(TcKimlikNo.IsValid(n)));
            Assert.Equal(numbers.Count, numbers.Distinct().Count());
        }

        private static RegisterDto Dto(string tckn) => new()
        {
            Username = "someone", Tckn = tckn, Password = "123456", FirstName = "A", LastName = "B", Email = "a@b.test"
        };

        [Fact]
        public void Registration_validation_accepts_a_valid_number_and_rejects_an_invalid_one_with_one_clear_message()
        {
            var validator = new RegisterDtoValidator();

            Assert.True(validator.Validate(Dto("10000000146")).IsValid);

            var result = validator.Validate(Dto("12345678901"));
            Assert.False(result.IsValid);
            var message = Assert.Single(result.Errors, e => e.PropertyName == "Tckn").ErrorMessage;
            Assert.Contains("check digits", message);
        }

        [Fact]
        public void A_number_that_is_not_all_digits_gets_the_digits_message_not_a_second_one()
        {
            var result = new RegisterDtoValidator().Validate(Dto("1234567890a"));

            var messages = result.Errors.Where(e => e.PropertyName == "Tckn").Select(e => e.ErrorMessage).ToList();
            Assert.Single(messages);
            Assert.Contains("only digits", messages[0]);
        }
    }
}
