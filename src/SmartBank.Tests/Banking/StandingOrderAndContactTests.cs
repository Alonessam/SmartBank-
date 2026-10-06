using Microsoft.EntityFrameworkCore;
using SmartBank.Core.DTOs;
using SmartBank.Core.Entities;
using SmartBank.Tests.Support;

namespace SmartBank.Tests.Banking
{
    /// <summary>Creating and removing standing orders, and the saved-recipient list: validation, limits and ownership.</summary>
    [Collection("EncryptionHelper")]
    public class StandingOrderAndContactTests : IDisposable
    {
        private readonly BankingHarness _h = new();

        public void Dispose() => _h.Dispose();

        private static CreateStandingOrderDto Order(string source, string? destination, decimal? amount = 100m, string type = "Transfer", string frequency = "Monthly", Guid? card = null) => new()
        {
            SourceAccountNumber = source,
            DestinationAccountNumber = destination,
            Amount = amount,
            OrderType = type,
            Frequency = frequency,
            CreditCardId = card
        };

        private async Task<(User User, Account Source, Account Destination)> SeedAsync()
        {
            var user = await _h.AddUserAsync();
            var source = await _h.AddAccountAsync(user, "TR0000000000000001", 1000m);
            var destination = await _h.AddAccountAsync(user, "TR0000000000000002", 0m);
            return (user, source, destination);
        }

        // ---- creating ----------------------------------------------------------------------------------------

        [Fact]
        public async Task A_transfer_order_is_stored_active_and_due_now_with_a_one_year_life()
        {
            var (user, source, destination) = await SeedAsync();

            var result = await _h.Service.CreateStandingOrderAsync(user.Id, Order(source.AccountNumber, destination.AccountNumber, 250.50m, frequency: "weekly"));

            Assert.True(result.IsSuccess);
            Assert.Equal("Weekly", result.Data!.Frequency);
            Assert.Equal("Transfer", result.Data.OrderType);
            Assert.Equal(250.50m, result.Data.Amount);
            Assert.True(result.Data.IsActive);
            Assert.Equal(_h.Clock.UtcNow, result.Data.NextExecutionDate);
            Assert.Equal(_h.Clock.UtcNow.AddYears(1), result.Data.MaturityDate);
            Assert.Single(await _h.ReadAsync(c => c.StandingOrders.AsNoTracking().ToListAsync()));
        }

        [Fact]
        public async Task The_destination_may_belong_to_somebody_else_when_the_currency_is_the_same()
        {
            var (user, source, _) = await SeedAsync();
            var other = await _h.AddUserAsync("other");
            var theirs = await _h.AddAccountAsync(other, "TR0000000000000009", 0m);

            Assert.True((await _h.Service.CreateStandingOrderAsync(user.Id, Order(source.AccountNumber, theirs.AccountNumber))).IsSuccess);
        }

        [Theory]
        [InlineData("Weekly ", "Weekly")]
        [InlineData("DAILY", "Daily")]
        [InlineData("monthly", "Monthly")]
        public async Task The_frequency_is_normalised(string given, string stored)
        {
            var (user, source, destination) = await SeedAsync();

            var result = await _h.Service.CreateStandingOrderAsync(user.Id, Order(source.AccountNumber, destination.AccountNumber, frequency: given));

            Assert.Equal(stored, result.Data!.Frequency);
        }

        [Theory]
        [InlineData("Yearly")]
        [InlineData("")]
        [InlineData("Every other Tuesday at midnight")]
        public async Task An_unknown_frequency_is_refused(string frequency)
        {
            var (user, source, destination) = await SeedAsync();

            var result = await _h.Service.CreateStandingOrderAsync(user.Id, Order(source.AccountNumber, destination.AccountNumber, frequency: frequency));

            Assert.Equal("InvalidFrequency", result.ErrorKey);
            Assert.Empty(await _h.ReadAsync(c => c.StandingOrders.ToListAsync()));
        }

        [Theory]
        [InlineData("Payroll")]
        [InlineData("")]
        [InlineData("CreditCardDebt")]
        public async Task An_unknown_order_type_is_refused(string type)
        {
            var (user, source, destination) = await SeedAsync();

            var result = await _h.Service.CreateStandingOrderAsync(user.Id, Order(source.AccountNumber, destination.AccountNumber, type: type));

            Assert.Equal("InvalidOrderType", result.ErrorKey);
        }

        [Fact]
        public async Task The_source_must_be_the_callers_own_account()
        {
            var (user, _, destination) = await SeedAsync();
            var other = await _h.AddUserAsync("other");
            var theirs = await _h.AddAccountAsync(other, "TR0000000000000009", 500m);

            var result = await _h.Service.CreateStandingOrderAsync(user.Id, Order(theirs.AccountNumber, destination.AccountNumber));

            Assert.Equal("SourceAccountNotFound", result.ErrorKey);
        }

        [Fact]
        public async Task A_transfer_order_needs_an_existing_destination_other_than_the_source_in_the_same_currency()
        {
            var (user, source, destination) = await SeedAsync();
            var usd = await _h.AddAccountAsync(user, "TR0000000000000003", 0m, "USD");

            Assert.Equal("DestinationAccountNotFound", (await _h.Service.CreateStandingOrderAsync(user.Id, Order(source.AccountNumber, null))).ErrorKey);
            Assert.Equal("DestinationAccountNotFound", (await _h.Service.CreateStandingOrderAsync(user.Id, Order(source.AccountNumber, "  "))).ErrorKey);
            Assert.Equal("DestinationAccountNotFound", (await _h.Service.CreateStandingOrderAsync(user.Id, Order(source.AccountNumber, "TR9999999999999999"))).ErrorKey);
            Assert.Equal("CannotTransferToSelf", (await _h.Service.CreateStandingOrderAsync(user.Id, Order(source.AccountNumber, source.AccountNumber))).ErrorKey);
            Assert.Equal("CurrencyMismatch", (await _h.Service.CreateStandingOrderAsync(user.Id, Order(source.AccountNumber, usd.AccountNumber))).ErrorKey);
            Assert.Empty(await _h.ReadAsync(c => c.StandingOrders.ToListAsync()));
        }

        [Theory]
        [InlineData(null, "InvalidAmount")]
        [InlineData(0.0, "InvalidAmount")]
        [InlineData(-5.0, "InvalidAmount")]
        [InlineData(1_000_000.01, "InvalidAmount")]
        [InlineData(10.005, "InvalidAmountScale")]
        public async Task A_transfer_order_needs_an_amount_up_to_a_million_with_two_decimals(double? amount, string errorKey)
        {
            var (user, source, destination) = await SeedAsync();

            var result = await _h.Service.CreateStandingOrderAsync(user.Id, Order(source.AccountNumber, destination.AccountNumber, amount == null ? null : (decimal)amount.Value));

            Assert.Equal(errorKey, result.ErrorKey);
        }

        [Fact]
        public async Task The_largest_allowed_amount_is_accepted()
        {
            var (user, source, destination) = await SeedAsync();

            Assert.True((await _h.Service.CreateStandingOrderAsync(user.Id, Order(source.AccountNumber, destination.AccountNumber, 1_000_000m))).IsSuccess);
        }

        [Fact]
        public async Task A_card_auto_pay_order_needs_the_callers_own_card_and_a_try_account_and_ignores_amount_and_destination()
        {
            var (user, source, destination) = await SeedAsync();
            var card = await _h.AddCardAsync(user, 100m);

            var result = await _h.Service.CreateStandingOrderAsync(user.Id, Order(source.AccountNumber, destination.AccountNumber, 5m, "CreditCardAutoPay", card: card.Id));

            Assert.True(result.IsSuccess);
            Assert.Null(result.Data!.Amount);
            Assert.Null(result.Data.DestinationAccountNumber);
            Assert.Equal(card.Id, result.Data.CreditCardId);
        }

        [Fact]
        public async Task An_auto_pay_order_cannot_point_at_somebody_elses_card_or_use_a_foreign_currency_account()
        {
            var (user, source, _) = await SeedAsync();
            var usd = await _h.AddAccountAsync(user, "TR0000000000000003", 100m, "USD");
            var myCard = await _h.AddCardAsync(user, 0m);
            var other = await _h.AddUserAsync("other");
            var theirCard = await _h.AddCardAsync(other, 0m);

            Assert.Equal("CreditCardNotFound", (await _h.Service.CreateStandingOrderAsync(user.Id, Order(source.AccountNumber, null, null, "CreditCardAutoPay", card: theirCard.Id))).ErrorKey);
            Assert.Equal("CreditCardNotFound", (await _h.Service.CreateStandingOrderAsync(user.Id, Order(source.AccountNumber, null, null, "CreditCardAutoPay", card: null))).ErrorKey);
            Assert.Equal("CreditCardNotFound", (await _h.Service.CreateStandingOrderAsync(user.Id, Order(source.AccountNumber, null, null, "CreditCardAutoPay", card: Guid.NewGuid()))).ErrorKey);
            Assert.Equal("CurrencyMismatch", (await _h.Service.CreateStandingOrderAsync(user.Id, Order(usd.AccountNumber, null, null, "CreditCardAutoPay", card: myCard.Id))).ErrorKey);
        }

        [Fact]
        public async Task A_transfer_order_ignores_a_card_id()
        {
            var (user, source, destination) = await SeedAsync();
            var card = await _h.AddCardAsync(user, 0m);

            var result = await _h.Service.CreateStandingOrderAsync(user.Id, Order(source.AccountNumber, destination.AccountNumber, card: card.Id));

            Assert.Null(result.Data!.CreditCardId);
        }

        [Fact]
        public async Task A_customer_has_at_most_twenty_active_orders_and_a_removed_one_frees_a_place()
        {
            var (user, source, destination) = await SeedAsync();
            Guid first = Guid.Empty;
            for (var i = 0; i < 20; i++)
            {
                var created = await _h.Service.CreateStandingOrderAsync(user.Id, Order(source.AccountNumber, destination.AccountNumber));
                Assert.True(created.IsSuccess);
                if (i == 0) first = created.Data!.Id;
            }

            Assert.Equal("StandingOrderLimitReached", (await _h.Service.CreateStandingOrderAsync(user.Id, Order(source.AccountNumber, destination.AccountNumber))).ErrorKey);

            Assert.True((await _h.Service.DeleteStandingOrderAsync(user.Id, first)).IsSuccess);
            Assert.True((await _h.Service.CreateStandingOrderAsync(user.Id, Order(source.AccountNumber, destination.AccountNumber))).IsSuccess);
        }

        // ---- reading and removing ----------------------------------------------------------------------------

        [Fact]
        public async Task The_list_shows_only_the_callers_orders_and_only_the_last_four_digits_of_a_card()
        {
            var (user, source, destination) = await SeedAsync();
            var card = await _h.AddCardAsync(user, 0m);
            await _h.Service.CreateStandingOrderAsync(user.Id, Order(source.AccountNumber, destination.AccountNumber));
            await _h.Service.CreateStandingOrderAsync(user.Id, Order(source.AccountNumber, null, null, "CreditCardAutoPay", card: card.Id));
            var other = await _h.AddUserAsync("other");
            var theirs = await _h.AddAccountAsync(other, "TR0000000000000009", 0m);
            await _h.Service.CreateStandingOrderAsync(other.Id, Order(theirs.AccountNumber, destination.AccountNumber));

            var orders = (await _h.Service.GetStandingOrdersAsync(user.Id)).Data!;

            Assert.Equal(2, orders.Count);
            Assert.Matches("^[0-9]{4}$", orders.Single(o => o.CreditCardId != null).CreditCardLast4);
            Assert.Null(orders.Single(o => o.CreditCardId == null).CreditCardLast4);
        }

        [Fact]
        public async Task An_order_can_be_removed_by_its_owner_only()
        {
            var (user, source, destination) = await SeedAsync();
            var order = (await _h.Service.CreateStandingOrderAsync(user.Id, Order(source.AccountNumber, destination.AccountNumber))).Data!;
            var stranger = await _h.AddUserAsync("stranger");

            var refused = await _h.Service.DeleteStandingOrderAsync(stranger.Id, order.Id);
            Assert.Equal("OrderNotFound", refused.ErrorKey);
            Assert.Single(await _h.ReadAsync(c => c.StandingOrders.ToListAsync()));

            Assert.True((await _h.Service.DeleteStandingOrderAsync(user.Id, order.Id)).IsSuccess);
            Assert.Empty(await _h.ReadAsync(c => c.StandingOrders.ToListAsync()));
            Assert.Equal("OrderNotFound", (await _h.Service.DeleteStandingOrderAsync(user.Id, order.Id)).ErrorKey);
        }

        // ---- contacts ----------------------------------------------------------------------------------------

        [Fact]
        public async Task A_recipient_is_saved_trimmed_and_listed_newest_first()
        {
            var user = await _h.AddUserAsync();

            var first = await _h.Service.SaveContactAsync(user.Id, new CreateSavedContactDto { AccountNumber = " TR0000000000000011 ", Alias = " Mum " });
            _h.Clock.Advance(TimeSpan.FromMinutes(1));
            await _h.Service.SaveContactAsync(user.Id, new CreateSavedContactDto { AccountNumber = "TR0000000000000012", Alias = "Landlord" });

            Assert.True(first.IsSuccess);
            Assert.Equal("TR0000000000000011", first.Data!.AccountNumber);
            Assert.Equal("Mum", first.Data.Alias);
            var list = (await _h.Service.GetSavedContactsAsync(user.Id)).Data!;
            Assert.Equal(new[] { "Landlord", "Mum" }, list.Select(c => c.Alias).ToArray());
        }

        [Fact]
        public async Task Saving_the_same_account_again_renames_the_entry()
        {
            var user = await _h.AddUserAsync();
            var first = (await _h.Service.SaveContactAsync(user.Id, new CreateSavedContactDto { AccountNumber = "TR0000000000000011", Alias = "Old" })).Data!;

            var again = await _h.Service.SaveContactAsync(user.Id, new CreateSavedContactDto { AccountNumber = "TR0000000000000011", Alias = "New" });

            Assert.Equal(first.Id, again.Data!.Id);
            Assert.Equal("New", (await _h.ReadAsync(c => c.SavedContacts.AsNoTracking().SingleAsync())).Alias);
        }

        [Fact]
        public async Task Two_customers_can_save_the_same_recipient_independently()
        {
            var one = await _h.AddUserAsync("one");
            var two = await _h.AddUserAsync("two");

            await _h.Service.SaveContactAsync(one.Id, new CreateSavedContactDto { AccountNumber = "TR0000000000000011", Alias = "One's" });
            await _h.Service.SaveContactAsync(two.Id, new CreateSavedContactDto { AccountNumber = "TR0000000000000011", Alias = "Two's" });

            Assert.Equal("One's", Assert.Single((await _h.Service.GetSavedContactsAsync(one.Id)).Data!).Alias);
            Assert.Equal("Two's", Assert.Single((await _h.Service.GetSavedContactsAsync(two.Id)).Data!).Alias);
        }

        [Theory]
        [InlineData("TR123", "Alias", "InvalidAccountNumber")]
        [InlineData("TR00000000000000000000000000000000", "Alias", "InvalidAccountNumber")]
        [InlineData("TR0000000000000011", "", "InvalidAlias")]
        [InlineData("TR0000000000000011", "   ", "InvalidAlias")]
        public async Task A_recipient_needs_a_plausible_number_and_an_alias(string number, string alias, string errorKey)
        {
            var user = await _h.AddUserAsync();

            var result = await _h.Service.SaveContactAsync(user.Id, new CreateSavedContactDto { AccountNumber = number, Alias = alias });

            Assert.Equal(errorKey, result.ErrorKey);
            Assert.Empty(await _h.ReadAsync(c => c.SavedContacts.ToListAsync()));
        }

        [Fact]
        public async Task An_alias_over_100_characters_is_refused()
        {
            var user = await _h.AddUserAsync();

            var result = await _h.Service.SaveContactAsync(user.Id, new CreateSavedContactDto { AccountNumber = "TR0000000000000011", Alias = new string('a', 101) });

            Assert.Equal("InvalidAlias", result.ErrorKey);
        }

        [Fact]
        public async Task At_most_100_recipients_can_be_saved_but_renaming_still_works_at_the_limit()
        {
            var user = await _h.AddUserAsync();
            for (var i = 0; i < 100; i++)
            {
                Assert.True((await _h.Service.SaveContactAsync(user.Id, new CreateSavedContactDto { AccountNumber = $"TR00000000000{i:D5}", Alias = "c" + i })).IsSuccess);
            }

            var extra = await _h.Service.SaveContactAsync(user.Id, new CreateSavedContactDto { AccountNumber = "TR0000000000099999", Alias = "extra" });
            var rename = await _h.Service.SaveContactAsync(user.Id, new CreateSavedContactDto { AccountNumber = "TR0000000000000000", Alias = "renamed" });

            Assert.Equal("ContactLimitReached", extra.ErrorKey);
            Assert.True(rename.IsSuccess);
        }

        [Fact]
        public async Task A_recipient_can_be_deleted_by_its_owner_only()
        {
            var user = await _h.AddUserAsync();
            var stranger = await _h.AddUserAsync("stranger");
            var contact = (await _h.Service.SaveContactAsync(user.Id, new CreateSavedContactDto { AccountNumber = "TR0000000000000011", Alias = "Mum" })).Data!;

            Assert.Equal("ContactNotFound", (await _h.Service.DeleteContactAsync(stranger.Id, contact.Id)).ErrorKey);
            Assert.Single(await _h.ReadAsync(c => c.SavedContacts.ToListAsync()));

            Assert.True((await _h.Service.DeleteContactAsync(user.Id, contact.Id)).IsSuccess);
            Assert.Empty(await _h.ReadAsync(c => c.SavedContacts.ToListAsync()));
        }
    }
}
