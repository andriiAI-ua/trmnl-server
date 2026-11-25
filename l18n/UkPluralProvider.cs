namespace Trmnl.Server.l18n
{
    public enum UkPluralMode
    {
        One,
        Few,
        Many
    }
    public class UkPluralProvider
    { 
        public static UkPluralMode GetMode(int n)
        {
            n = Math.Abs(n);

            var n10 = n % 10;
            var n100 = n % 100;

            if (n10 == 1 && n100 != 11)
                return UkPluralMode.One;

            if (n10 is >= 2 and <= 4 &&
                (n100 < 12 || n100 > 14))
                return UkPluralMode.Few;

            return UkPluralMode.Many;
        }
    }
}
