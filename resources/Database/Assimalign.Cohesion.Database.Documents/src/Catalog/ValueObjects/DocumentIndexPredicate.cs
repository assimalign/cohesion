using System;
using System.Diagnostics;
using System.Globalization;

using Assimalign.Cohesion.Database.Documents.Internal;

namespace Assimalign.Cohesion.Database.Documents.Catalog;

/// <summary>The supported comparison operations in a normalized array-key filter.</summary>
public enum DocumentIndexComparison : byte
{
    /// <summary>Equal.</summary>
    Equal,
    /// <summary>Not equal.</summary>
    NotEqual,
    /// <summary>Less than.</summary>
    LessThan,
    /// <summary>Less than or equal.</summary>
    LessThanOrEqual,
    /// <summary>Greater than.</summary>
    GreaterThan,
    /// <summary>Greater than or equal.</summary>
    GreaterThanOrEqual
}

/// <summary>The operation of a normalized array-key predicate node.</summary>
public enum DocumentIndexPredicateKind : byte
{
    /// <summary>A path compared with a scalar constant.</summary>
    Comparison = 1,
    /// <summary>An explicit NULL test.</summary>
    IsNull,
    /// <summary>A MISSING test.</summary>
    IsMissing,
    /// <summary>A conjunction.</summary>
    And,
    /// <summary>A disjunction.</summary>
    Or,
    /// <summary>A negation.</summary>
    Not
}

/// <summary>An immutable, bounded predicate over paths relative to a single array element.</summary>
/// <remarks>Supports scalar comparisons, IS NULL, IS MISSING, AND, OR and NOT. Comparisons
/// propagate unknown values; only TRUE admits an element. Depth is at most 32 and size at most
/// 128 nodes. Constants are normalized to null, Boolean, Decimal or String.</remarks>
public sealed record DocumentIndexPredicate
{
    private DocumentIndexPredicate(byte kind, string? path = null, DocumentIndexComparison comparison = default,
        object? value = null, DocumentIndexPredicate? left = null, DocumentIndexPredicate? right = null)
    {
        Operation = (DocumentIndexPredicateKind)kind; Path = path; Comparison = comparison; Value = value; Left = left; Right = right;
        Depth = 1 + Math.Max(left?.Depth ?? 0, right?.Depth ?? 0);
        NodeCount = 1 + (left?.NodeCount ?? 0) + (right?.NodeCount ?? 0);
        if (Depth > 32 || NodeCount > 128)
        { throw new ArgumentException(ErrorMessages.IndexPredicateTooLarge); }
    }

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    internal byte Kind => (byte)Operation;
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    internal int Depth { get; }
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    internal int NodeCount { get; }
    /// <summary>Gets the node operation.</summary>
    public DocumentIndexPredicateKind Operation { get; }
    /// <summary>Gets the element-relative operand path, or null for a logical operation.</summary>
    public string? Path { get; }
    /// <summary>Gets the comparison operation for a comparison node.</summary>
    public DocumentIndexComparison Comparison { get; }
    /// <summary>Gets the normalized immutable scalar constant for a comparison node.</summary>
    public object? Value { get; }
    /// <summary>Gets the left or sole logical operand.</summary>
    public DocumentIndexPredicate? Left { get; }
    /// <summary>Gets the right logical operand.</summary>
    public DocumentIndexPredicate? Right { get; }

    /// <summary>Compares an element-relative path with a scalar constant.</summary>
    /// <param name="path">The relative path, or empty for the element itself.</param>
    /// <param name="comparison">The comparison operation.</param>
    /// <param name="value">Null, Boolean, integral, Decimal or String constant.</param>
    /// <returns>The immutable comparison.</returns>
    /// <exception cref="ArgumentException">The path is invalid, or the constant is not null, Boolean,
    /// Byte, SByte, Int16, UInt16, Int32, UInt32, Int64, UInt64, Decimal or String. Single and Double
    /// constants are refused even when finite and exactly representable as Decimal.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The comparison is unknown.</exception>
    public static DocumentIndexPredicate Compare(string path, DocumentIndexComparison comparison, object? value)
    {
        ValidatePath(path);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((byte)comparison, (byte)DocumentIndexComparison.GreaterThanOrEqual);
        if (value is byte or sbyte or short or ushort or int or uint or long or ulong)
        { value = Convert.ToDecimal(value, CultureInfo.InvariantCulture); }
        if (value is not (null or bool or decimal or string))
        { throw new ArgumentException(ErrorMessages.IndexPredicateConstantInvalid, nameof(value)); }
        return new DocumentIndexPredicate(1, path, comparison, value);
    }

    /// <summary>Tests an element-relative path for explicit NULL.</summary>
    /// <param name="path">The relative path, or empty for the element itself.</param>
    /// <returns>The immutable null test.</returns>
    /// <exception cref="ArgumentException">The path is invalid.</exception>
    public static DocumentIndexPredicate IsNull(string path)
    { ValidatePath(path); return new DocumentIndexPredicate(2, path); }

    /// <summary>Tests an element-relative path for MISSING.</summary>
    /// <param name="path">The relative path, or empty for the element itself.</param>
    /// <returns>The immutable missing test.</returns>
    /// <exception cref="ArgumentException">The path is invalid.</exception>
    public static DocumentIndexPredicate IsMissing(string path)
    { ValidatePath(path); return new DocumentIndexPredicate(3, path); }

    /// <summary>Combines two predicates with AND.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The immutable conjunction.</returns>
    /// <exception cref="ArgumentNullException">An operand is null.</exception>
    /// <exception cref="ArgumentException">The combined predicate exceeds 32 levels or 128 nodes, counting repeated subtrees at each occurrence.</exception>
    public static DocumentIndexPredicate And(DocumentIndexPredicate left, DocumentIndexPredicate right)
    { ArgumentNullException.ThrowIfNull(left); ArgumentNullException.ThrowIfNull(right); return new DocumentIndexPredicate(4, left: left, right: right); }

    /// <summary>Combines two predicates with OR.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The immutable disjunction.</returns>
    /// <exception cref="ArgumentNullException">An operand is null.</exception>
    /// <exception cref="ArgumentException">The combined predicate exceeds 32 levels or 128 nodes, counting repeated subtrees at each occurrence.</exception>
    public static DocumentIndexPredicate Or(DocumentIndexPredicate left, DocumentIndexPredicate right)
    { ArgumentNullException.ThrowIfNull(left); ArgumentNullException.ThrowIfNull(right); return new DocumentIndexPredicate(5, left: left, right: right); }

    /// <summary>Negates a predicate.</summary>
    /// <param name="operand">The predicate to negate.</param>
    /// <returns>The immutable negation.</returns>
    /// <exception cref="ArgumentNullException">The operand is null.</exception>
    /// <exception cref="ArgumentException">The negated predicate exceeds 32 levels or 128 nodes, counting repeated subtrees at each occurrence.</exception>
    public static DocumentIndexPredicate Not(DocumentIndexPredicate operand)
    { ArgumentNullException.ThrowIfNull(operand); return new DocumentIndexPredicate(6, left: operand); }

    private static void ValidatePath(string path)
    { ArgumentNullException.ThrowIfNull(path); if (path.Length != 0) { DocumentIndexKeys.ValidatePath(path); } }
}
