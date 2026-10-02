using System;
using SmartBank.Core.Entities;

namespace SmartBank.Core.Security
{
    /// <summary>
    /// Temporary account lockout after repeated wrong PINs. The PIN is only 6 digits (1,000,000 possibilities),
    /// so without a limit it can be brute-forced; BCrypt slows each guess but does not stop an attacker.
    /// Pure functions over <see cref="User"/>; the caller saves.
    /// </summary>
    public static class LoginLockout
    {
        public const int MaxFailedAttempts = 5;
        public static readonly TimeSpan Duration = TimeSpan.FromMinutes(15);

        public static bool IsLocked(User user, DateTime utcNow) =>
            user.LockoutEnd.HasValue && user.LockoutEnd.Value > utcNow;

        public static TimeSpan Remaining(User user, DateTime utcNow) =>
            IsLocked(user, utcNow) ? user.LockoutEnd!.Value - utcNow : TimeSpan.Zero;

        public static void RegisterFailure(User user, DateTime utcNow)
        {
            // A lockout that has already ended starts a fresh count instead of locking again on the first miss.
            if (user.LockoutEnd.HasValue && user.LockoutEnd.Value <= utcNow)
            {
                user.LockoutEnd = null;
                user.FailedLoginCount = 0;
            }

            user.FailedLoginCount++;

            if (user.FailedLoginCount >= MaxFailedAttempts)
            {
                user.LockoutEnd = utcNow + Duration;
            }
        }

        public static void Reset(User user)
        {
            user.FailedLoginCount = 0;
            user.LockoutEnd = null;
        }
    }
}
