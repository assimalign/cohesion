using System;
using System.Diagnostics;

namespace Assimalign.Cohesion.Core.Tests;

// Compiling this attribute and override proves the generator emits neither duplicate member.
[DebuggerDisplay("[redacted]")]
internal readonly partial struct GeneratedRedactedString
{
    public static partial bool IsValid(string value, out string? message)
    {
        bool valid = !string.IsNullOrEmpty(value) && value.Length <= 8;
        message = valid ? null : GeneratedValidatedString.InvalidMessage;
        return valid;
    }

    /// <summary>Returns a redacted diagnostic representation.</summary>
    /// <returns>The redaction marker, including for the default value.</returns>
    public override string ToString() => "[redacted]";
}
