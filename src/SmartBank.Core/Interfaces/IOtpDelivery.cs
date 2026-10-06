using SmartBank.Core.Entities;

namespace SmartBank.Core.Interfaces
{
    /// <summary>Delivers one-time codes to the user (e-mail). Never throws: a delivery failure must not break the request.</summary>
    public interface IOtpDelivery
    {
        /// <summary>
        /// Demo switch (configuration key Demo:ExposeOtp, off by default). When true the code is also returned inside
        /// API responses and written to the server log, so a public demo without a mailbox can still show the flow.
        /// It removes the value of the second factor, so it must stay off anywhere that holds real data.
        /// </summary>
        bool ExposeCodeInResponse { get; }

        /// <param name="detail">What the code approves, in words ("2500.00 to TR..."), so the user can see it in the mail. Optional.</param>
        void Send(User user, string code, OtpPurpose purpose, string? detail = null);
    }
}
