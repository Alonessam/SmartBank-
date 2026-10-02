namespace SmartBank.Core.Entities
{
    /// <summary>
    /// What a one-time code is valid for. A code issued for one purpose is rejected for any other,
    /// so a login code can never be replayed to reset a password or approve a transfer.
    /// </summary>
    public enum OtpPurpose
    {
        Login = 1,
        Transfer = 2,
        PasswordReset = 3
    }
}
