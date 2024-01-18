namespace Assimalign.Cohesion.Net.Http.Internal;

internal static class HttpRules
{
    // Controls
    public const byte CarriageReturn = (byte)'\r';
    public const byte LineFeed = (byte)'\n';
    public const byte Space = (byte)' ';
    public const byte Tab = (byte)'\t';
    public const int MaxInt64Digits = 19;
    public const int MaxInt32Digits = 10;
    public static readonly string[] DateFormats = new string[]
    {
        // "r", // RFC 1123, required output format but too strict for input
        "ddd, d MMM yyyy H:m:s 'GMT'",      // RFC 1123 (r, except it allows both 1 and 01 for date and time)
        "ddd, d MMM yyyy H:m:s",            // RFC 1123, no zone - assume GMT
        "d MMM yyyy H:m:s 'GMT'",           // RFC 1123, no day-of-week
        "d MMM yyyy H:m:s",                 // RFC 1123, no day-of-week, no zone
        "ddd, d MMM yy H:m:s 'GMT'",        // RFC 1123, short year
        "ddd, d MMM yy H:m:s",              // RFC 1123, short year, no zone
        "d MMM yy H:m:s 'GMT'",             // RFC 1123, no day-of-week, short year
        "d MMM yy H:m:s",                   // RFC 1123, no day-of-week, short year, no zone

        "dddd, d'-'MMM'-'yy H:m:s 'GMT'",   // RFC 850, short year
        "dddd, d'-'MMM'-'yy H:m:s",         // RFC 850 no zone
        "ddd, d'-'MMM'-'yyyy H:m:s 'GMT'",  // RFC 850, long year
        "ddd MMM d H:m:s yyyy",             // ANSI C's asctime() format

        "ddd, d MMM yyyy H:m:s zzz",        // RFC 5322
        "ddd, d MMM yyyy H:m:s",            // RFC 5322 no zone
        "d MMM yyyy H:m:s zzz",             // RFC 5322 no day-of-week
        "d MMM yyyy H:m:s",                 // RFC 5322 no day-of-week, no zone
    };
}
