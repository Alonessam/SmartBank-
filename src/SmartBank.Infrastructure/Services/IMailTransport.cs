namespace SmartBank.Infrastructure.Services
{
    public sealed record OutgoingMail(string ToAddress, string ToName, string Subject, string HtmlBody);

    /// <summary>Gets one e-mail to the provider. Throws when the provider rejects it or cannot be reached.</summary>
    public interface IMailTransport
    {
        /// <returns>true when the provider accepted the mail; false when no provider is configured (nothing was sent).</returns>
        Task<bool> SendAsync(OutgoingMail mail, CancellationToken cancellationToken = default);
    }
}
