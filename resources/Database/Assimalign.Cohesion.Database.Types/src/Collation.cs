using System;
using System.Buffers;
using System.Globalization;
using System.Text;

using Assimalign.Cohesion.Database.Types.Internal;

namespace Assimalign.Cohesion.Database.Types;

/// <summary>
/// A named, stable rule for string comparison, equality, hashing, and index encoding.
/// </summary>
/// <remarks>
/// The three index-backed collations use pinned Unicode 17.0 data and UTF-8 byte
/// order. Their meaning is independent of the current culture, ICU version, and
/// invariant globalization. Identifiers are persisted and must never be renumbered.
/// The legacy <see cref="Invariant"/> linguistic collation is not index-backed.
/// </remarks>
public sealed class Collation
{
    private Collation(byte id, string name, bool isIndexBacked)
    {
        Id = id;
        Name = name;
        IsIndexBacked = isIndexBacked;
    }

    /// <summary>Gets Unicode code-point order, equivalent to unmodified UTF-8 byte order.</summary>
    public static Collation Binary { get; } = new(0, "binary", true);

    /// <summary>
    /// Gets the legacy case-sensitive invariant linguistic collation. It is not
    /// index-backed: predicates require a scan, and key encoding is rejected.
    /// Linguistic operations fail explicitly under invariant globalization.
    /// </summary>
    public static Collation Invariant { get; } = new(1, "invariant", false);

    /// <summary>Gets Unicode 17.0 default simple case folding followed by UTF-8 byte order.</summary>
    public static Collation CaseInsensitive { get; } = new(2, "case_insensitive", true);

    /// <summary>
    /// Gets Unicode 17.0 canonical decomposition with all combining marks removed,
    /// followed by default simple case folding and UTF-8 byte order.
    /// </summary>
    public static Collation CaseAccentInsensitive { get; } = new(3, "case_accent_insensitive", true);

    /// <summary>Gets the stable identifier persisted inside encoded keys.</summary>
    public byte Id { get; }

    /// <summary>Gets the SQL collation name.</summary>
    public string Name { get; }

    /// <summary>Gets whether comparison can be implemented by a deterministic byte transform.</summary>
    public bool IsIndexBacked { get; }

    /// <summary>Resolves a collation from its persisted identifier.</summary>
    /// <param name="id">The persisted collation identifier.</param>
    /// <returns>The collation.</returns>
    /// <exception cref="DatabaseTypeException">The identifier is unknown.</exception>
    public static Collation FromId(byte id) => id switch
    {
        0 => Binary,
        1 => Invariant,
        2 => CaseInsensitive,
        3 => CaseAccentInsensitive,
        _ => throw new DatabaseTypeException($"Unknown collation identifier {id}."),
    };

    /// <summary>Resolves a built-in SQL collation name, ignoring ASCII letter case.</summary>
    /// <param name="name">The collation name.</param>
    /// <returns>The collation.</returns>
    /// <exception cref="DatabaseTypeException">The name is unknown or names an unsupported collation.</exception>
    public static Collation FromName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Equals("binary", StringComparison.OrdinalIgnoreCase))
        {
            return Binary;
        }
        if (name.Equals("invariant", StringComparison.OrdinalIgnoreCase))
        {
            return Invariant;
        }
        if (name.Equals("case_insensitive", StringComparison.OrdinalIgnoreCase))
        {
            return CaseInsensitive;
        }
        if (name.Equals("case_accent_insensitive", StringComparison.OrdinalIgnoreCase))
        {
            return CaseAccentInsensitive;
        }
        throw new DatabaseTypeException($"Unknown or unsupported collation '{name}'. Culture-aware and user-defined collations are not supported.");
    }

    /// <summary>Compares two strings under this collation.</summary>
    /// <param name="left">The first string.</param>
    /// <param name="right">The second string.</param>
    /// <returns>Negative, zero, or positive per standard comparison semantics.</returns>
    public int Compare(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (!IsIndexBacked)
        {
            EnsureLinguisticGlobalization();
            return CultureInfo.InvariantCulture.CompareInfo.Compare(left, right, CompareOptions.None);
        }

        return CompareCodePoints(Normalize(left), Normalize(right));
    }

    /// <summary>
    /// Produces the canonical text whose UTF-8 bytes define comparison and equality.
    /// Original spelling belongs in the stored value, not in an index-key tie-breaker.
    /// </summary>
    /// <param name="value">The string to transform.</param>
    /// <returns>The transformed text, or the original text for binary collation.</returns>
    /// <exception cref="DatabaseTypeException">The collation is not index-backed or the text has invalid UTF-16.</exception>
    public string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!IsIndexBacked)
        {
            throw new DatabaseTypeException($"Collation '{Name}' is not index-backed and has no deterministic byte transform.");
        }

        StringBuilder? result = Id == Binary.Id ? null : new StringBuilder(value.Length);
        ReadOnlySpan<char> remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out Rune rune, out int consumed) != OperationStatus.Done)
            {
                throw new DatabaseTypeException("Collated strings must contain valid UTF-16 Unicode scalar values.");
            }

            remaining = remaining[consumed..];
            if (Id == CaseAccentInsensitive.Id)
            {
                CollationUnicodeData.AppendWithoutAccents(result!, rune.Value);
            }
            else if (result is not null)
            {
                result.Append(new Rune(CollationUnicodeData.FoldCase(rune.Value)).ToString());
            }
        }

        return result?.ToString() ?? value;
    }

    /// <summary>Produces the deterministic UTF-8 bytes used by index-backed collations.</summary>
    /// <param name="value">The string to transform.</param>
    /// <returns>The order-preserving transformed bytes.</returns>
    /// <exception cref="DatabaseTypeException">The collation is not index-backed or the text has invalid UTF-16.</exception>
    public byte[] GetSortKey(string value) => Encoding.UTF8.GetBytes(Normalize(value));

    /// <summary>Computes a string hash consistent with equality under this collation.</summary>
    /// <param name="value">The string to hash.</param>
    /// <returns>The collation-aware hash code.</returns>
    public int GetHashCode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!IsIndexBacked)
        {
            EnsureLinguisticGlobalization();
            return CultureInfo.InvariantCulture.CompareInfo.GetHashCode(value, CompareOptions.None);
        }

        uint hash = 2166136261;
        foreach (byte component in GetSortKey(value))
        {
            hash = unchecked((hash ^ component) * 16777619);
        }

        return unchecked((int)hash);
    }

    private static void EnsureLinguisticGlobalization()
    {
        bool invariant = AppContext.TryGetSwitch("System.Globalization.Invariant", out bool configured)
            ? configured
            : Environment.GetEnvironmentVariable("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT") is string setting
                && (setting == "1" || setting.Equals("true", StringComparison.OrdinalIgnoreCase));
        if (invariant)
        {
            throw new DatabaseTypeException("The legacy invariant linguistic collation is unavailable under invariant globalization; use an index-backed collation.");
        }
    }

    private static int CompareCodePoints(string left, string right)
    {
        int i = 0, j = 0;
        while (i < left.Length && j < right.Length)
        {
            int leftCodePoint = char.ConvertToUtf32(left, i);
            int rightCodePoint = char.ConvertToUtf32(right, j);
            if (leftCodePoint != rightCodePoint)
            {
                return leftCodePoint < rightCodePoint ? -1 : 1;
            }

            i += char.IsSurrogatePair(left, i) ? 2 : 1;
            j += char.IsSurrogatePair(right, j) ? 2 : 1;
        }

        return (left.Length - i).CompareTo(right.Length - j);
    }

    /// <inheritdoc />
    public override string ToString() => Name;
}
