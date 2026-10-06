namespace SmartBank.Core.Common
{
    /// <summary>
    /// The check-digit rule of a T.C. Kimlik Numarası: 11 digits, the first one not 0; the 10th digit is
    /// ((d1+d3+d5+d7+d9) * 7 - (d2+d4+d6+d8)) mod 10 and the 11th is (d1+...+d10) mod 10.
    /// This only tells a mistyped number from a well-formed one. It does NOT prove that the number belongs to the person
    /// (that needs the official MERNIS service), and most random 11-digit strings fail it, which is the point.
    /// </summary>
    public static class TcKimlikNo
    {
        public static bool IsValid(string? value)
        {
            if (value is null || value.Length != 11) return false;

            var d = new int[11];
            for (var i = 0; i < 11; i++)
            {
                if (value[i] is < '0' or > '9') return false;
                d[i] = value[i] - '0';
            }

            if (d[0] == 0) return false;

            var odd = d[0] + d[2] + d[4] + d[6] + d[8];
            var even = d[1] + d[3] + d[5] + d[7];
            var tenth = (((odd * 7) - even) % 10 + 10) % 10;
            if (d[9] != tenth) return false;

            var eleventh = (odd + even + d[9]) % 10;
            return d[10] == eleventh;
        }
    }
}
