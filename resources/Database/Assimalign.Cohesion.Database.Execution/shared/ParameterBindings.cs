using System;
using System.Collections.Generic;

// Deviates from the repo RootNamespace rule per shared-source design: stateless parameter rules
// keep the Execution namespace when compiled into language and engine assemblies (#1102, D15).
namespace Assimalign.Cohesion.Database.Execution.Internal;

internal static class ParameterBindings
{
    internal static string BareName(string name)
        => name.Length > 0 && name[0] is '@' or '$' ? name[1..] : name;

    internal static bool IsValidReference(string reference)
    {
        var name = reference.AsSpan(1);
        if (name.IsEmpty) { return false; }
        bool numeric = char.IsAsciiDigit(name[0]);
        if (!numeric && !char.IsLetter(name[0]) && name[0] != '_') { return false; }
        foreach (char character in name)
        {
            if (numeric ? !char.IsAsciiDigit(character) : !char.IsLetterOrDigit(character) && character != '_') { return false; }
        }
        return true;
    }

    // Copy by enumeration: the caller's comparer must never decide parameter matching.
    // Only one sigil is presentation. Unused keys need no validation or value conversion.
    internal static IReadOnlyDictionary<string, object?>? Normalize(
        IReadOnlyDictionary<string, object?>? parameters, Func<string, Exception> collision)
    {
        if (parameters is null) { return null; }
        var normalized = new Dictionary<string, object?>(parameters.Count, StringComparer.Ordinal);
        foreach (var pair in parameters)
        {
            string name = BareName(pair.Key);
            if (!normalized.TryAdd(name, pair.Value)) { throw collision(name); }
        }
        return normalized;
    }
}
