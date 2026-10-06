using System.Text.RegularExpressions;

namespace SmartBank.Core.Common
{
    /// <summary>Size limits for what a customer or agent may type into the support chat.</summary>
    public static class ChatLimits
    {
        public const int MaxMessageLength = 1000;
        public const int MaxTitleLength = 100;
    }

    /// <summary>
    /// The chat carries machine messages as text in square brackets ("[CONFIRM_TRANSFER: ...]", "[TRANSFER_SUCCESS: ...]").
    /// The web app turns them into cards with a Confirm button. Anything a person (or the AI model) types must never be
    /// able to produce one of these, otherwise a fake "transfer confirmation" or "transfer successful" could be put into
    /// somebody's conversation. <see cref="Neutralize"/> breaks the marker so it is shown as plain text.
    /// </summary>
    public static class ChatMarkers
    {
        private static readonly Regex Marker = new(
            @"\[\s*(CONFIRM_TRANSFER|TRANSFER_SUCCESS|TRANSFER_FAILED|SESSION_TRANSFERRED|ACTION)\s*:",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>Replaces the opening bracket of every marker with a parenthesis, which the client does not recognise.</summary>
        public static string Neutralize(string? text) =>
            string.IsNullOrEmpty(text) ? string.Empty : Marker.Replace(text, m => "(" + m.Value[1..]);

        public static bool ContainsMarker(string? text) => !string.IsNullOrEmpty(text) && Marker.IsMatch(text);
    }
}
