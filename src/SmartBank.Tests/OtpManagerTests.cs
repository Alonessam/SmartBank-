using SmartBank.Core.Entities;
using SmartBank.Core.Security;

namespace SmartBank.Tests
{
    public class OtpManagerTests
    {
        private static readonly DateTime Now = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

        private static string WrongCodeFor(string code) => code == "000000" ? "000001" : "000000";

        [Fact]
        public void Issue_Creates_A_Six_Digit_Code_With_Expiry_And_Purpose()
        {
            var user = new User { OtpFailedCount = 3 };

            var code = OtpManager.Issue(user, OtpPurpose.Login, Now);

            Assert.Matches("^[1-9][0-9]{5}$", code);
            Assert.Equal(code, user.TwoFactorSecret);
            Assert.Equal(Now + OtpManager.Lifetime, user.TwoFactorExpiry);
            Assert.Equal(OtpPurpose.Login, user.PendingOtpPurpose);
            Assert.Equal(0, user.OtpFailedCount);
        }

        [Fact]
        public void A_Correct_Code_Works_Exactly_Once()
        {
            var user = new User();
            var code = OtpManager.Issue(user, OtpPurpose.Login, Now);

            Assert.Equal(OtpCheckResult.Valid, OtpManager.Verify(user, OtpPurpose.Login, code, Now));
            Assert.Equal(OtpCheckResult.NoPendingCode, OtpManager.Verify(user, OtpPurpose.Login, code, Now));
            Assert.Null(user.TwoFactorSecret);
        }

        [Fact]
        public void A_Wrong_Code_Is_Rejected_And_Counted()
        {
            var user = new User();
            var code = OtpManager.Issue(user, OtpPurpose.Login, Now);

            Assert.Equal(OtpCheckResult.Invalid, OtpManager.Verify(user, OtpPurpose.Login, WrongCodeFor(code), Now));
            Assert.Equal(1, user.OtpFailedCount);

            // The real code still works afterwards.
            Assert.Equal(OtpCheckResult.Valid, OtpManager.Verify(user, OtpPurpose.Login, code, Now));
        }

        [Fact]
        public void The_Code_Is_Destroyed_After_Too_Many_Wrong_Guesses()
        {
            var user = new User();
            var code = OtpManager.Issue(user, OtpPurpose.Login, Now);
            var wrong = WrongCodeFor(code);

            for (var i = 1; i < OtpManager.MaxFailedAttempts; i++)
            {
                Assert.Equal(OtpCheckResult.Invalid, OtpManager.Verify(user, OtpPurpose.Login, wrong, Now));
            }

            Assert.Equal(OtpCheckResult.TooManyAttempts, OtpManager.Verify(user, OtpPurpose.Login, wrong, Now));

            // Even the right code is useless now: brute force cannot win by being patient.
            Assert.Equal(OtpCheckResult.NoPendingCode, OtpManager.Verify(user, OtpPurpose.Login, code, Now));
        }

        [Fact]
        public void After_Too_Many_Wrong_Guesses_A_New_Code_Is_Not_Available_Right_Away()
        {
            var user = new User();
            OtpManager.Issue(user, OtpPurpose.PasswordReset, Now);
            for (var i = 0; i < OtpManager.MaxFailedAttempts; i++) OtpManager.Verify(user, OtpPurpose.PasswordReset, "000000", Now);

            // Without this, "ask for a new code" would give a fresh set of five guesses with every request.
            Assert.True(OtpManager.IsCoolingDown(user, OtpPurpose.PasswordReset, Now.AddSeconds(30), TimeSpan.FromSeconds(60)));
            Assert.False(OtpManager.IsCoolingDown(user, OtpPurpose.PasswordReset, Now.AddSeconds(61), TimeSpan.FromSeconds(60)));
            Assert.Equal(OtpCheckResult.NoPendingCode, OtpManager.Verify(user, OtpPurpose.PasswordReset, "123456", Now.AddSeconds(5)));
        }

        [Fact]
        public void An_Expired_Code_Is_Rejected_And_Removed()
        {
            var user = new User();
            var code = OtpManager.Issue(user, OtpPurpose.Login, Now);

            var result = OtpManager.Verify(user, OtpPurpose.Login, code, Now + OtpManager.Lifetime + TimeSpan.FromSeconds(1));

            Assert.Equal(OtpCheckResult.Expired, result);
            Assert.Null(user.TwoFactorSecret);
        }

        [Fact]
        public void A_Code_Is_Only_Valid_For_The_Purpose_It_Was_Issued_For()
        {
            var user = new User();
            var code = OtpManager.Issue(user, OtpPurpose.Login, Now);

            Assert.Equal(OtpCheckResult.NoPendingCode, OtpManager.Verify(user, OtpPurpose.PasswordReset, code, Now));
            Assert.Equal(OtpCheckResult.NoPendingCode, OtpManager.Verify(user, OtpPurpose.Transfer, code, Now));

            // The failed cross-purpose attempts did not burn the code.
            Assert.Equal(OtpCheckResult.Valid, OtpManager.Verify(user, OtpPurpose.Login, code, Now));
        }

        [Fact]
        public void Verifying_With_Nothing_Pending_Does_Not_Count_As_A_Failed_Attempt()
        {
            var user = new User();

            Assert.Equal(OtpCheckResult.NoPendingCode, OtpManager.Verify(user, OtpPurpose.Login, "123456", Now));
            Assert.Equal(0, user.OtpFailedCount);
        }

        [Fact]
        public void A_Null_Code_Is_Treated_As_A_Wrong_Code()
        {
            var user = new User();
            OtpManager.Issue(user, OtpPurpose.Login, Now);

            Assert.Equal(OtpCheckResult.Invalid, OtpManager.Verify(user, OtpPurpose.Login, null, Now));
        }

        // ---- binding to one specific action ---------------------------------------------------------------

        [Fact]
        public void A_Bound_Code_Only_Approves_The_Exact_Action()
        {
            var user = new User();
            var binding = OtpManager.TransferBinding("TR01", "TR02", 2500m);
            var code = OtpManager.Issue(user, OtpPurpose.Transfer, Now, binding);

            var other = OtpManager.TransferBinding("TR01", "TR02", 2600m);
            Assert.Equal(OtpCheckResult.Invalid, OtpManager.Verify(user, OtpPurpose.Transfer, code, Now, other));
            Assert.Equal(1, user.OtpFailedCount); // a replay attempt counts as a failure

            Assert.Equal(OtpCheckResult.Valid, OtpManager.Verify(user, OtpPurpose.Transfer, code, Now, binding));
        }

        [Fact]
        public void An_Unbound_Code_Is_Rejected_When_The_Caller_Presents_A_Binding()
        {
            var user = new User();
            var code = OtpManager.Issue(user, OtpPurpose.Login, Now);

            Assert.Equal(OtpCheckResult.Invalid,
                OtpManager.Verify(user, OtpPurpose.Login, code, Now, OtpManager.TransferBinding("A", "B", 1m)));
        }

        [Fact]
        public void TransferBinding_Changes_When_Any_Part_Changes_And_Ignores_Amount_Formatting()
        {
            var baseline = OtpManager.TransferBinding("TR01", "TR02", 100m);

            Assert.NotEqual(baseline, OtpManager.TransferBinding("TR09", "TR02", 100m));
            Assert.NotEqual(baseline, OtpManager.TransferBinding("TR01", "TR09", 100m));
            Assert.NotEqual(baseline, OtpManager.TransferBinding("TR01", "TR02", 100.01m));
            Assert.Equal(baseline, OtpManager.TransferBinding("TR01", "TR02", 100.00m));
            Assert.Matches("^[0-9a-f]{64}$", baseline);
        }

        // ---- cooldown ---------------------------------------------------------------------------------------

        [Fact]
        public void Cooldown_Applies_Right_After_Issuing_And_Ends_Later()
        {
            var user = new User();
            var cooldown = TimeSpan.FromSeconds(60);
            OtpManager.Issue(user, OtpPurpose.PasswordReset, Now);

            Assert.True(OtpManager.IsCoolingDown(user, OtpPurpose.PasswordReset, Now.AddSeconds(10), cooldown));
            Assert.False(OtpManager.IsCoolingDown(user, OtpPurpose.PasswordReset, Now.AddSeconds(61), cooldown));
        }

        [Fact]
        public void Cooldown_Only_Applies_To_The_Same_Purpose_And_Not_Without_A_Code()
        {
            var user = new User();
            var cooldown = TimeSpan.FromSeconds(60);

            Assert.False(OtpManager.IsCoolingDown(user, OtpPurpose.PasswordReset, Now, cooldown));

            OtpManager.Issue(user, OtpPurpose.Login, Now);
            Assert.False(OtpManager.IsCoolingDown(user, OtpPurpose.PasswordReset, Now, cooldown));
        }
    }
}
