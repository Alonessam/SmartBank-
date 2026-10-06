using System.ComponentModel.DataAnnotations;
using System.Globalization;
using FluentValidation;
using SmartBank.Core.Common;
using SmartBank.Core.DTOs;
using SmartBank.Core.Validators;

namespace SmartBank.Tests
{
    /// <summary>The small pure rules: money, constants, card dates, credit-card interest, masking, and the two validators.</summary>
    public class CoreRulesTests
    {
        // ---- money -------------------------------------------------------------------------------------------

        [Theory]
        [InlineData("1", true)]
        [InlineData("1.5", true)]
        [InlineData("1.50", true)]
        [InlineData("0.01", true)]
        [InlineData("1.505", false)]
        [InlineData("0.001", false)]
        [InlineData("100.0000", true)]
        [InlineData("100.0001", false)]
        public void Only_amounts_with_at_most_two_decimals_are_valid(string text, bool valid)
        {
            Assert.Equal(valid, Money.HasValidScale(decimal.Parse(text, CultureInfo.InvariantCulture)));
        }

        [Theory]
        [InlineData("15.005", "15.01")]
        [InlineData("15.004", "15.00")]
        [InlineData("-15.005", "-15.01")]
        [InlineData("0.125", "0.13")]
        [InlineData("2.675", "2.68")]
        public void Rounding_goes_away_from_zero_not_to_the_even_neighbour(string input, string expected)
        {
            Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), Money.Round(decimal.Parse(input, CultureInfo.InvariantCulture)));
        }

        [Fact]
        public void Money_is_always_formatted_with_a_dot_and_two_decimals_even_under_a_turkish_culture()
        {
            var previous = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            try
            {
                Assert.Equal("1234.50", Money.Format(1234.5m));
                Assert.Equal("0.00", Money.Format(0m));
                Assert.Equal("10000000.00", Money.Format(Money.MaxAmount));
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        [Fact]
        public void The_money_attribute_accepts_nothing_wrong_and_ignores_other_types()
        {
            var attribute = new MoneyScaleAttribute();

            Assert.True(attribute.IsValid(1.25m));
            Assert.True(attribute.IsValid(null));
            Assert.True(attribute.IsValid("text"));
            Assert.False(attribute.IsValid(1.255m));
        }

        // ---- the allow-lists ---------------------------------------------------------------------------------

        [Theory]
        [InlineData("usd", "USD")]
        [InlineData(" eur ", "EUR")]
        [InlineData("try", "TRY")]
        [InlineData("xau", "XAU")]
        [InlineData("", null)]
        [InlineData("   ", null)]
        [InlineData(null, null)]
        public void Currency_codes_are_normalised_without_regard_to_the_culture(string? input, string? expected)
        {
            var previous = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR"); // "i".ToUpper() is "İ" here: ToUpperInvariant must be used
            try
            {
                Assert.Equal(expected, Currencies.Normalize(input));
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        [Fact]
        public void Five_currencies_exist_and_four_of_them_trade()
        {
            Assert.Equal(new[] { "TRY", "USD", "EUR", "XAU", "XAG" }, Currencies.All);
            Assert.False(Currencies.IsTradable("TRY"));
            Assert.All(new[] { "USD", "EUR", "XAU", "XAG" }, c => Assert.True(Currencies.IsTradable(c)));
            Assert.False(Currencies.IsSupported("usd")); // the check is on the normalised value
            Assert.False(Currencies.IsSupported(null));
        }

        [Theory]
        [InlineData(null, "DemandDeposit")]
        [InlineData("", "DemandDeposit")]
        [InlineData("demanddeposit", "DemandDeposit")]
        [InlineData("TimeDeposit", "TimeDeposit")]
        [InlineData("Savings", null)]
        public void Account_types_are_canonicalised(string? input, string? expected) => Assert.Equal(expected, AccountTypes.Normalize(input));

        [Theory]
        [InlineData("transfer", "Transfer")]
        [InlineData("CREDITCARDAUTOPAY", "CreditCardAutoPay")]
        [InlineData("CreditCardDebt", null)]   // the legacy name is read but never created
        [InlineData("", null)]
        public void Order_types_are_canonicalised(string input, string? expected)
        {
            Assert.Equal(expected, OrderTypes.Normalize(input));
            Assert.True(OrderTypes.IsCreditCard("CreditCardDebt"));
            Assert.False(OrderTypes.IsCreditCard("Transfer"));
        }

        [Theory]
        [InlineData("daily", "Daily")]
        [InlineData(" WEEKLY ", "Weekly")]
        [InlineData("Monthly", "Monthly")]
        [InlineData("yearly", null)]
        [InlineData(null, null)]
        public void Frequencies_are_canonicalised(string? input, string? expected) => Assert.Equal(expected, Frequencies.Normalize(input));

        // ---- card dates and credit-card rules ----------------------------------------------------------------

        [Fact]
        public void The_expiry_date_uses_a_slash_whatever_the_cultures_date_separator()
        {
            var previous = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR"); // its date separator is "."
            try
            {
                Assert.Equal("10/31", CardFormat.Expiry(new DateTime(2031, 10, 6)));
                Assert.Equal("03/29", CardFormat.ExpiryIn(new DateTime(2026, 3, 1), 3));
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        [Theory]
        [InlineData(2026, 11, 5, "Ekim 2026")]
        [InlineData(2027, 1, 5, "Aralık 2026")]
        [InlineData(2026, 2, 1, "Ocak 2026")]
        [InlineData(2026, 3, 1, "Şubat 2026")]
        [InlineData(2026, 6, 15, "Mayıs 2026")]
        [InlineData(2026, 9, 1, "Ağustos 2026")]
        [InlineData(2026, 12, 31, "Kasım 2026")]
        public void A_statement_is_named_for_the_month_its_period_began_in(int year, int month, int day, string expected)
        {
            Assert.Equal(expected, CreditCardRules.PeriodName(new DateTime(year, month, day)));
        }

        [Theory]
        [InlineData("1000", "0", "300", "44.75")]     // below the minimum: 5 % on the 300 missing + 4.25 % on the rest
        [InlineData("1000", "300", "300", "29.75")]   // minimum paid: 4.25 % of the 700 left
        [InlineData("1000", "500", "300", "21.25")]
        [InlineData("1000", "1000", "300", "0")]
        [InlineData("1000", "1200", "300", "0")]      // overpaid
        [InlineData("0", "0", "0", "0")]
        [InlineData("100", "0", "30", "4.58")]        // 1.50 + 70 x 4.25 % = 4.475 -> 4.48? (see below)
        public void Interest_follows_the_documented_rates(string debt, string paid, string minimum, string expected)
        {
            var interest = CreditCardRules.Interest(Dec(debt), Dec(paid), Dec(minimum));

            // 100 / 0 / 30: 30 x 5 % = 1.50 plus 70 x 4.25 % = 2.975 gives 4.475, rounded away from zero to 4.48.
            Assert.Equal(expected == "4.58" ? 4.48m : Dec(expected), interest);
        }

        private static decimal Dec(string text) => decimal.Parse(text, CultureInfo.InvariantCulture);

        [Fact]
        public void The_minimum_payment_is_thirty_percent_rounded()
        {
            Assert.Equal(313.43m, CreditCardRules.MinimumPayment(1044.75m));
            Assert.Equal(0m, CreditCardRules.MinimumPayment(0m));
        }

        // ---- masking -----------------------------------------------------------------------------------------

        [Theory]
        [InlineData("12345678901", "123****01")]
        [InlineData("123456", "123****56")]
        [InlineData("1234", "****")]
        [InlineData("", "***")]
        [InlineData(null, "***")]
        public void A_tckn_is_masked_for_logs_and_audit_text(string? tckn, string expected) => Assert.Equal(expected, TcKimlikNo.Mask(tckn));

        // ---- validators --------------------------------------------------------------------------------------

        private static RegisterDto ValidRegistration() => new()
        {
            Username = "newcustomer",
            Tckn = TestTckn.Next(),
            Password = "123456",
            FirstName = "Ayşe",
            LastName = "Yılmaz",
            Email = "ayse@example.com"
        };

        private static readonly RegisterDtoValidator Register = new();

        [Fact]
        public void A_valid_registration_passes_both_validators_and_attributes()
        {
            var dto = ValidRegistration();

            Assert.True(Register.Validate(dto).IsValid);
            Assert.True(Validator.TryValidateObject(dto, new ValidationContext(dto), new List<ValidationResult>(), true));
        }

        [Theory]
        [InlineData("Username", "", "Username is required.")]
        [InlineData("Username", "ab", "at least 3")]
        [InlineData("Username", "012345678901234567890123456789012345678901234567890", "cannot exceed 50")]
        [InlineData("Tckn", "", "required")]
        [InlineData("Tckn", "123", "exactly 11")]
        [InlineData("Tckn", "1234567890a", "only digits")]
        [InlineData("Tckn", "12345678901", "check digits")]
        [InlineData("Password", "", "required")]
        [InlineData("Password", "12345", "exactly 6")]
        [InlineData("Password", "12345a", "only digits")]
        [InlineData("FirstName", "", "required")]
        [InlineData("FirstName", "Ali3", "letters, spaces, hyphens")]
        [InlineData("FirstName", "AaaaaaaaaaAaaaaaaaaaAaaaaaaaaaAaaaaaaaaaAaaaaaaaaaA", "cannot exceed 50")]
        [InlineData("LastName", "", "required")]
        [InlineData("LastName", "Yılmaz2", "letters, spaces, hyphens")]
        [InlineData("LastName", "<script>", "letters, spaces, hyphens")]
        [InlineData("LastName", "AaaaaaaaaaAaaaaaaaaaAaaaaaaaaaAaaaaaaaaaAaaaaaaaaaA", "cannot exceed 50")]
        [InlineData("Email", "", "required")]
        [InlineData("Email", "not-an-address", "valid email")]
        public void Each_registration_field_has_its_own_rule(string field, string value, string messagePart)
        {
            var dto = ValidRegistration();
            typeof(RegisterDto).GetProperty(field)!.SetValue(dto, value);

            var result = Register.Validate(dto);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.PropertyName == field && e.ErrorMessage.Contains(messagePart, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void An_email_of_101_characters_is_too_long_but_100_is_fine()
        {
            var dto = ValidRegistration();
            dto.Email = new string('a', 88) + "@example.com"; // 100
            Assert.True(Register.Validate(dto).IsValid);

            dto.Email = new string('a', 89) + "@example.com"; // 101
            Assert.Contains(Register.Validate(dto).Errors, e => e.PropertyName == "Email");
        }

        [Fact]
        public void The_longest_allowed_names_fit_the_hundred_character_full_name_column()
        {
            var dto = ValidRegistration();
            dto.FirstName = new string('A', 50);
            dto.LastName = new string('B', 49);

            Assert.True(Register.Validate(dto).IsValid);
            Assert.True($"{dto.FirstName} {dto.LastName}".Length <= 100);

            dto.LastName = new string('B', 50); // 50 + a space + 50 = 101: one too many
            Assert.Contains(Register.Validate(dto).Errors, e => e.ErrorMessage.Contains("99 characters"));
        }

        private static readonly TransferRequestDtoValidator TransferValidator = new();

        private static TransferRequestDto ValidTransfer() => new()
        {
            SourceAccountNumber = "TR0000000000000001",
            DestinationAccountNumber = "TR0000000000000002",
            Amount = 100m,
            Description = "rent",
            Category = "Bills"
        };

        [Fact]
        public void A_valid_transfer_passes()
        {
            var dto = ValidTransfer();

            Assert.True(TransferValidator.Validate(dto).IsValid);
            Assert.True(Validator.TryValidateObject(dto, new ValidationContext(dto), new List<ValidationResult>(), true));
        }

        [Theory]
        [InlineData("SourceAccountNumber", "", "Source account number is required")]
        [InlineData("DestinationAccountNumber", "", "Destination account number is required")]
        [InlineData("DestinationAccountNumber", "TR123", "at least 10")]
        [InlineData("DestinationAccountNumber", "TR00000000000000000000000000000000", "cannot exceed 30")]
        [InlineData("OtpCode", "12345678901", "cannot exceed 10")]
        public void Each_transfer_text_field_has_its_own_rule(string field, string value, string messagePart)
        {
            var dto = ValidTransfer();
            typeof(TransferRequestDto).GetProperty(field)!.SetValue(dto, value);

            var result = TransferValidator.Validate(dto);

            Assert.Contains(result.Errors, e => e.PropertyName == field && e.ErrorMessage.Contains(messagePart, StringComparison.OrdinalIgnoreCase));
        }

        [Theory]
        [InlineData(0, "greater than zero")]
        [InlineData(-1, "greater than zero")]
        [InlineData(10_000_000.01, "cannot exceed")]
        [InlineData(1.005, "2 decimal")]
        public void The_transfer_amount_is_checked_for_sign_size_and_decimals(double amount, string messagePart)
        {
            var dto = ValidTransfer();
            dto.Amount = (decimal)amount;

            var result = TransferValidator.Validate(dto);

            Assert.Contains(result.Errors, e => e.PropertyName == "Amount" && e.ErrorMessage.Contains(messagePart, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void A_description_of_201_or_a_category_of_51_characters_is_too_long()
        {
            var dto = ValidTransfer();
            dto.Description = new string('d', 201);
            dto.Category = new string('c', 51);

            var errors = TransferValidator.Validate(dto).Errors;

            Assert.Contains(errors, e => e.PropertyName == "Description");
            Assert.Contains(errors, e => e.PropertyName == "Category");
        }

        [Fact]
        public void The_data_annotations_agree_with_the_validator_on_decimals_and_lengths()
        {
            var dto = ValidTransfer();
            dto.Amount = 10.123m;
            dto.SourceAccountNumber = new string('S', 31);
            var results = new List<ValidationResult>();

            Validator.TryValidateObject(dto, new ValidationContext(dto), results, true);

            Assert.Contains(results, r => r.MemberNames.Contains("Amount"));
            Assert.Contains(results, r => r.MemberNames.Contains("SourceAccountNumber"));
        }

        [Theory]
        [InlineData(1.5, true)]
        [InlineData(1.555, false)]
        [InlineData(0, false)]
        [InlineData(1000000.01, false)]
        public void The_standing_order_amount_attributes_cap_at_a_million_with_two_decimals(double amount, bool valid)
        {
            var dto = new CreateStandingOrderDto { SourceAccountNumber = "TR1", Amount = (decimal)amount, Frequency = "Daily", OrderType = "Transfer" };

            Assert.Equal(valid, Validator.TryValidateObject(dto, new ValidationContext(dto), new List<ValidationResult>(), true));
        }

        [Theory]
        [InlineData(0.01, true)]
        [InlineData(0, false)]
        [InlineData(10000000, true)]
        [InlineData(10000001, false)]
        [InlineData(1.001, false)]
        public void The_exchange_deposit_and_payment_attributes_share_the_same_amount_rules(double amount, bool valid)
        {
            var exchange = new ExchangeDto { SourceAccountId = "x", Asset = "USD", Action = "buy", Amount = (decimal)amount };
            var deposit = new DepositRequestDto { AccountNumber = "TR1", Amount = (decimal)amount };
            var payment = new PayCreditCardDebtDto { SourceAccountNumber = "TR1", Amount = (decimal)amount };

            foreach (var dto in new object[] { exchange, deposit, payment })
            {
                Assert.Equal(valid, Validator.TryValidateObject(dto, new ValidationContext(dto), new List<ValidationResult>(), true));
            }
        }

        [Fact]
        public void The_two_factor_request_takes_only_a_six_digit_pin_and_the_hand_over_a_department()
        {
            var none = new Toggle2FaRequestDto { Enable = true, Password = null };
            Assert.True(Validator.TryValidateObject(none, new ValidationContext(none), new List<ValidationResult>(), true));
            var bad = new Toggle2FaRequestDto { Enable = true, Password = "12ab" };
            Assert.False(Validator.TryValidateObject(bad, new ValidationContext(bad), new List<ValidationResult>(), true));

            var empty = new TransferSessionDto { Department = "" };
            Assert.False(Validator.TryValidateObject(empty, new ValidationContext(empty), new List<ValidationResult>(), true));
            var tooLong = new TransferSessionDto { Department = new string('d', 101) };
            Assert.False(Validator.TryValidateObject(tooLong, new ValidationContext(tooLong), new List<ValidationResult>(), true));
        }
    }
}
