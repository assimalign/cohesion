using System;

namespace Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// A monotonically increasing sequence number assigned to a transaction when it begins.
/// </summary>
/// <remarks>
/// The sequence number (xid) orders transactions for MVCC visibility decisions and is
/// distinct from <see cref="TransactionId"/>, which is the transaction's unordered
/// external identity. Sequence numbers are engine-local and never persist beyond the
/// lifetime of a database's transaction history.
/// </remarks>
public readonly struct TransactionSequence : IEquatable<TransactionSequence>, IComparable<TransactionSequence>
{
    private readonly ulong _value;

    /// <summary>
    /// Initializes a new <see cref="TransactionSequence"/> with the specified value.
    /// </summary>
    /// <param name="value">The underlying sequence value.</param>
    public TransactionSequence(ulong value)
    {
        _value = value;
    }

    /// <summary>
    /// Gets the underlying sequence value.
    /// </summary>
    public ulong Value => _value;

    /// <summary>
    /// Gets the zero sequence, which precedes every assigned transaction sequence.
    /// </summary>
    public static TransactionSequence None => default;

    /// <inheritdoc />
    public int CompareTo(TransactionSequence other) => _value.CompareTo(other._value);

    /// <inheritdoc />
    public bool Equals(TransactionSequence other) => _value == other._value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is TransactionSequence other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _value.GetHashCode();

    /// <inheritdoc />
    public override string ToString() => _value.ToString();

    /// <summary>Equality operator.</summary>
    public static bool operator ==(TransactionSequence left, TransactionSequence right) => left._value == right._value;

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(TransactionSequence left, TransactionSequence right) => left._value != right._value;

    /// <summary>Less-than operator.</summary>
    public static bool operator <(TransactionSequence left, TransactionSequence right) => left._value < right._value;

    /// <summary>Greater-than operator.</summary>
    public static bool operator >(TransactionSequence left, TransactionSequence right) => left._value > right._value;

    /// <summary>Less-than-or-equal operator.</summary>
    public static bool operator <=(TransactionSequence left, TransactionSequence right) => left._value <= right._value;

    /// <summary>Greater-than-or-equal operator.</summary>
    public static bool operator >=(TransactionSequence left, TransactionSequence right) => left._value >= right._value;
}
