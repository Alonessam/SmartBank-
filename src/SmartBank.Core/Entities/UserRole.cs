namespace SmartBank.Core.Entities
{
    /// <summary>
    /// What a signed-in person may do. Stored in the database and copied into the JWT as a role claim.
    /// It is never taken from anything the user supplies (a registration always creates a Customer); support agents
    /// are promoted by an administrator directly in the database.
    /// </summary>
    public enum UserRole
    {
        Customer = 0,
        Agent = 1
    }

    /// <summary>Role names as they appear in the JWT and in [Authorize(Roles = ...)].</summary>
    public static class RoleNames
    {
        public const string Customer = nameof(UserRole.Customer);
        public const string Agent = nameof(UserRole.Agent);
    }
}
