using System.ComponentModel.DataAnnotations;
using SmartBank.Core.DTOs;
using SmartBank.Core.Validators;

namespace SmartBank.Tests
{
    /// <summary>
    /// The web form accepts real-world names (spaces, hyphens, apostrophes, dots) and the server must accept exactly what the
    /// form lets through; before, the form allowed "O'Neil-Çelik" and the server answered 400 with an English message.
    /// </summary>
    public class NameRulesTests
    {
        public static TheoryData<string> GoodNames => new()
        {
            "Ayşe", "Zeynep Nur", "O'Neil", "O’Neil", "Çelik-Yılmaz", "Ş. Ç.", "Müller", "Ömer", "İbrahim", "José María",
            "Bülüç", "D'Angelo-Smith", "Mary Ann"
        };

        public static TheoryData<string> BadNames => new()
        {
            "", " ", "123", "Ali1", "<script>", "Ali@Veli", "-Ali", "'Ali", " Ali", "Ali--", "Ali\nVeli", "Ali_Veli",
            "Ali;DROP", "Ali/Veli"
        };

        private static RegisterDto Dto(string first, string last) => new()
        {
            Username = "someone", Tckn = "10000000146", Password = "123456", FirstName = first, LastName = last, Email = "a@b.test"
        };

        [Theory]
        [MemberData(nameof(GoodNames))]
        public void Real_names_are_accepted_by_the_validator_and_the_attributes(string name)
        {
            var dto = Dto(name, name);

            Assert.True(new RegisterDtoValidator().Validate(dto).IsValid, $"validator rejected '{name}'");
            Assert.True(Validator.TryValidateObject(dto, new ValidationContext(dto), new List<ValidationResult>(), true), $"attributes rejected '{name}'");
        }

        [Theory]
        [MemberData(nameof(BadNames))]
        public void Names_with_digits_symbols_or_odd_punctuation_are_refused(string name)
        {
            var dto = Dto(name, "Yılmaz");

            Assert.False(new RegisterDtoValidator().Validate(dto).IsValid, $"validator accepted '{name}'");
            Assert.False(Validator.TryValidateObject(dto, new ValidationContext(dto), new List<ValidationResult>(), true), $"attributes accepted '{name}'");
        }

        [Fact]
        public void The_refusal_message_explains_what_is_allowed()
        {
            var errors = new RegisterDtoValidator().Validate(Dto("Ali1", "Veli")).Errors.Select(e => e.ErrorMessage).ToList();

            Assert.Contains(errors, m => m.Contains("letters") && m.Contains("hyphens") && m.Contains("apostrophes"));
        }
    }
}
