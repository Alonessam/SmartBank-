using System.Text;
using SmartBank.Core.Common;
using SmartBank.Core.DTOs;

namespace SmartBank.Infrastructure.Services
{
    /// <summary>
    /// What the AI services send to a model. The one place that decides which stored messages become model input, so
    /// Gemini and Ollama cannot differ.
    /// </summary>
    internal static class AiConversation
    {
        /// <summary>The model sees at most this many of the newest conversation messages: bounded cost, bounded latency.</summary>
        public const int MaxMessages = 20;

        public const string SystemPrompt =
            "You are SmartBank AI, a helpful, polite, and professional customer support assistant for SmartBank Fintech. " +
            "Answer general banking questions (such as credit card limits, interest rates, account types) and keep answers concise and safe. " +
            "Analyze the language of the user's message. If they write in Turkish, reply in Turkish. If they write in English, reply in English. " +
            "If the user asks for a human representative, or if you cannot answer a complex transaction/security issue, tell the user politely in their language that you will notify and connect them to a human customer representative. " +
            "\n\nCRITICAL ACTION CAPABILITIES:\n" +
            "1. If the user wants to check their balance, see their accounts, or asks 'bakiyem', 'hesaplarım', you MUST respond with EXACTLY: [ACTION:GET_BALANCES]\n" +
            "2. If the user wants to transfer money (para transferi, para gönder vb.), you must ask them for: (a) Source Account, (b) Destination Account, and (c) Amount. Once you have collected these 3 details from the conversation, you MUST respond with EXACTLY: [ACTION:TRANSFER, source:SOURCE_ACCOUNT, destination:DESTINATION_ACCOUNT, amount:AMOUNT, description:DESCRIPTION]\n" +
            "Do NOT output any other text when returning these actions.";

        public sealed record Turn(bool FromUser, string Text);

        /// <summary>
        /// The system prompt (base prompt plus retrieved FAQ text) and the conversation turns.
        ///  - "Context" messages are server-built FAQ text: they extend the system prompt.
        ///  - "System" rows from the database (transfer results, hand-over notes) are NOT instructions and are dropped. They used
        ///    to be appended to the system prompt, which let text a customer controls (a transfer description) speak with the
        ///    prompt's authority.
        ///  - "User" is the customer; "AI" and "Agent" are the assistant side.
        /// </summary>
        public static (string SystemPrompt, List<Turn> Turns) Prepare(IEnumerable<ChatMessageDto> history)
        {
            var system = new StringBuilder(SystemPrompt);
            var turns = new List<Turn>();

            foreach (var message in history)
            {
                if (message.Sender.Equals(ChatSenders.Context, StringComparison.OrdinalIgnoreCase))
                {
                    system.Append("\n\nAdditional Context:\n").Append(message.Content);
                }
                else if (message.Sender.Equals(ChatSenders.System, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                else
                {
                    var text = string.IsNullOrWhiteSpace(message.Content) ? "..." : message.Content;
                    turns.Add(new Turn(message.Sender.Equals(ChatSenders.User, StringComparison.OrdinalIgnoreCase), text));
                }
            }

            if (turns.Count > MaxMessages)
            {
                turns = turns.GetRange(turns.Count - MaxMessages, MaxMessages);
            }

            return (system.ToString(), turns);
        }
    }
}
