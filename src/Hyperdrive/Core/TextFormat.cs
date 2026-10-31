using System.Text;

namespace Hyperdrive.Core
{
    /// <summary>
    /// Allocation-free number formatting into a StringBuilder (on this Mono, Append(int/float)
    /// goes through ToString() and allocates a temporary string).
    /// </summary>
    internal static class TextFormat
    {
        [System.ThreadStatic] private static char[] t_digits;

        public static StringBuilder AppendInt(this StringBuilder sb, long value)
        {
            if (value == 0)
                return sb.Append('0');

            char[] digits = t_digits ?? (t_digits = new char[20]);
            bool negative = value < 0;
            ulong v = negative ? (ulong)(-(value + 1)) + 1 : (ulong)value;
            int count = 0;
            while (v > 0)
            {
                digits[count++] = (char)('0' + (int)(v % 10));
                v /= 10;
            }
            if (negative)
                sb.Append('-');
            while (count > 0)
                sb.Append(digits[--count]);
            return sb;
        }

        /// <summary>Fixed-point, rounded to <paramref name="decimals"/> (0..3) digits.</summary>
        public static StringBuilder AppendFixed(this StringBuilder sb, double value, int decimals)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return sb.Append('-');

            long scale = decimals <= 0 ? 1 : decimals == 1 ? 10 : decimals == 2 ? 100 : 1000;
            long scaled = (long)(value * scale + (value >= 0 ? 0.5 : -0.5));
            if (scaled < 0)
            {
                sb.Append('-');
                scaled = -scaled;
            }
            sb.AppendInt(scaled / scale);
            if (decimals <= 0)
                return sb;

            sb.Append('.');
            long fraction = scaled % scale;
            for (long div = scale / 10; div > 0; div /= 10)
            {
                sb.Append((char)('0' + (int)(fraction / div)));
                fraction %= div;
            }
            return sb;
        }

        public static StringBuilder AppendMB(this StringBuilder sb, long bytes) => sb.AppendInt(bytes / (1024 * 1024));
    }
}
