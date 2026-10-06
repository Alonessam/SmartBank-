using SmartBank.Core.Common;

namespace SmartBank.Tests
{
    /// <summary>
    /// Registration now checks the T.C. Kimlik Numarası check digits, so tests that go through the real registration
    /// endpoint need numbers that pass. These are made-up, well-formed numbers: unique within the test run, belonging to nobody.
    /// </summary>
    public static class TestTckn
    {
        private static long _next = 100_000_000;

        /// <summary>A unique, valid number.</summary>
        public static string Next() => FromNineDigits(Interlocked.Increment(ref _next));

        /// <summary>Appends the two check digits to a nine-digit base (first digit 1-9).</summary>
        public static string FromNineDigits(long nine)
        {
            var s = nine.ToString("D9");
            var d = s.Select(c => c - '0').ToArray();

            var odd = d[0] + d[2] + d[4] + d[6] + d[8];
            var even = d[1] + d[3] + d[5] + d[7];
            var tenth = (((odd * 7) - even) % 10 + 10) % 10;
            var eleventh = (odd + even + tenth) % 10;

            var result = s + tenth + eleventh;
            if (!TcKimlikNo.IsValid(result)) throw new InvalidOperationException($"Generated an invalid test number: {result}");
            return result;
        }
    }
}
