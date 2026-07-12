using System;
using System.Globalization;

namespace Assimalign.Cohesion.Database.Types;

/// <summary>
/// An explicit string collation identity: a named, stable rule set for comparing
/// strings and producing order-preserving sort keys.
/// </summary>
/// <remarks>
/// <para>
/// Collation is always explicit in the Cohesion data platform — no comparison ever
/// depends on the ambient culture. Two collations ship with the kernel:
/// </para>
/// <list type="bullet">
///   <item><see cref="Binary"/> — Unicode code-point order (equivalently, UTF-8 byte
///   order). The fastest and the default for keys.</item>
///   <item><see cref="Invariant"/> — culture-invariant linguistic ordering
///   (<see cref="CultureInfo.InvariantCulture"/>, case-sensitive).</item>
/// </list>
/// <para>
/// The identifier byte is persisted inside encoded keys, so values are append-only
/// and never renumbered. Adding a collation is a kernel change: implement its
/// comparison and its sort-key encoding here, so every model orders strings the
/// same way.
/// </para>
/// </remarks>
public sealed class Collation
{
    private readonly CompareInfo? _compareInfo;

    private Collation(byte id, string name, CompareInfo? compareInfo)
    {
        Id = id;
        Name = name;
        _compareInfo = compareInfo;
    }

    /// <summary>
    /// Unicode code-point order (UTF-8 byte order). The default key collation.
    /// </summary>
    public static Collation Binary { get; } = new(0, "binary", null);

    /// <summary>
    /// Culture-invariant linguistic ordering, case-sensitive.
    /// </summary>
    public static Collation Invariant { get; } = new(1, "invariant", CultureInfo.InvariantCulture.CompareInfo);

    /// <summary>
    /// Gets the stable identifier persisted inside encoded keys.
    /// </summary>
    public byte Id { get; }

    /// <summary>
    /// Gets the collation name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Resolves a collation from its persisted identifier.
    /// </summary>
    /// <param name="id">The persisted collation identifier.</param>
    /// <returns>The collation.</returns>
    /// <exception cref="DatabaseTypeException">The identifier is unknown.</exception>
    public static Collation FromId(byte id) => id switch
    {
        0 => Binary,
        1 => Invariant,
        _ => throw new DatabaseTypeException($"Unknown collation identifier {id}."),
    };

    /// <summary>
    /// Compares two strings under this collation.
    /// </summary>
    /// <param name="left">The first string.</param>
    /// <param name="right">The second string.</param>
    /// <returns>Negative, zero, or positive per standard comparison semantics.</returns>
    public int Compare(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (_compareInfo is not null)
        {
            return _compareInfo.Compare(left, right, CompareOptions.None);
        }

        return CompareCodePoints(left, right);
    }

    /// <summary>
    /// Compares by Unicode code point, matching UTF-8 byte order (which differs from
    /// <see cref="string.CompareOrdinal(string, string)"/> for supplementary-plane
    /// characters, whose UTF-16 surrogate units would order below U+E000).
    /// </summary>
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
