namespace Assimalign.Cohesion.Core.Tests;

internal sealed partial class GeneratedStringClass
{
    internal const string InvalidMessage = "The value must contain between 1 and 8 characters.";

    internal static int ValidationCount { get; set; }

    public static partial bool IsValid(string value, out string? message)
    {
        ValidationCount++;
        bool valid = !string.IsNullOrEmpty(value) && value.Length <= 8;
        message = valid ? null : InvalidMessage;
        return valid;
    }
}
