using SmartBank.Core.Entities;
using SmartBank.Core.Interfaces;

namespace SmartBank.Tests
{
    /// <summary>Records the one-time codes instead of e-mailing them, so tests can read the code the "user" received.</summary>
    public sealed class FakeOtpDelivery : IOtpDelivery
    {
        public bool ExposeCodeInResponse { get; set; }

        public List<(Guid UserId, string Code, OtpPurpose Purpose)> Sent { get; } = new();

        public string LastCode => Sent[^1].Code;

        public void Send(User user, string code, OtpPurpose purpose, string? detail = null)
        {
            Sent.Add((user.Id, code, purpose));
            LastDetail = detail;
        }

        public string? LastDetail { get; private set; }
    }
}
