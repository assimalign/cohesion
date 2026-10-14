namespace Assimalign.Cohesion.Core.Tests;

internal readonly partial struct GeneratedRangeInt
{
    internal const string InvalidMessage = "The value must be between 1 and 10.";

    internal static int ValidationCount { get; set; }

    public static partial bool IsValid(int value, out string? message)
    {
        ValidationCount++;
        bool valid = value is >= 1 and <= 10;
        message = valid ? null : InvalidMessage;
        return valid;
    }
}
