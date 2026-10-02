using SmartBank.Core.Entities;
using SmartBank.Core.Security;

namespace SmartBank.Tests
{
    public class LoginLockoutTests
    {
        private static readonly DateTime Now = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Fewer_Failures_Than_The_Limit_Do_Not_Lock()
        {
            var user = new User();

            for (var i = 0; i < LoginLockout.MaxFailedAttempts - 1; i++)
            {
                LoginLockout.RegisterFailure(user, Now);
            }

            Assert.False(LoginLockout.IsLocked(user, Now));
            Assert.Equal(LoginLockout.MaxFailedAttempts - 1, user.FailedLoginCount);
        }

        [Fact]
        public void Reaching_The_Limit_Locks_For_The_Configured_Duration()
        {
            var user = new User();

            for (var i = 0; i < LoginLockout.MaxFailedAttempts; i++)
            {
                LoginLockout.RegisterFailure(user, Now);
            }

            Assert.True(LoginLockout.IsLocked(user, Now));
            Assert.Equal(Now + LoginLockout.Duration, user.LockoutEnd);
            Assert.Equal(LoginLockout.Duration, LoginLockout.Remaining(user, Now));
        }

        [Fact]
        public void The_Lock_Ends_On_Its_Own()
        {
            var user = new User();
            for (var i = 0; i < LoginLockout.MaxFailedAttempts; i++) LoginLockout.RegisterFailure(user, Now);

            var later = Now + LoginLockout.Duration + TimeSpan.FromSeconds(1);

            Assert.False(LoginLockout.IsLocked(user, later));
            Assert.Equal(TimeSpan.Zero, LoginLockout.Remaining(user, later));
        }

        [Fact]
        public void After_A_Lock_Ends_The_Count_Starts_Over_Instead_Of_Locking_On_The_First_Miss()
        {
            var user = new User();
            for (var i = 0; i < LoginLockout.MaxFailedAttempts; i++) LoginLockout.RegisterFailure(user, Now);
            var later = Now + LoginLockout.Duration + TimeSpan.FromSeconds(1);

            LoginLockout.RegisterFailure(user, later);

            Assert.False(LoginLockout.IsLocked(user, later));
            Assert.Equal(1, user.FailedLoginCount);
        }

        [Fact]
        public void Reset_Clears_The_Counter_And_The_Lock()
        {
            var user = new User { FailedLoginCount = 4, LockoutEnd = Now.AddMinutes(5) };

            LoginLockout.Reset(user);

            Assert.Equal(0, user.FailedLoginCount);
            Assert.Null(user.LockoutEnd);
            Assert.False(LoginLockout.IsLocked(user, Now));
        }
    }
}
