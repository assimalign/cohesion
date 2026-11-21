using System;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// Selects the records of one record set that an <see cref="IIdentityHubStore"/> returns a page
/// of: every record, or the records that list one value under one index.
/// </summary>
/// <remarks>
/// A query is deliberately this small. A store answers it with a key or index lookup on any
/// medium, so the contract holds for a relational table, a key-value space, and a document
/// container alike. Filtering, sorting, and prefix or range matching are not part of it.
/// </remarks>
public sealed class IdentityHubRecordQuery
{
    /// <summary>
    /// The number of records a page holds when a query does not say.
    /// </summary>
    public const int DefaultLimit = 100;

    /// <summary>
    /// The most records a query asks for in one page.
    /// </summary>
    public const int MaxLimit = 1000;

    private IdentityHubRecordQuery(
        IdentityHubRecordIndexName? index,
        IdentityHubRecordKey? value,
        int limit,
        string? continuation)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxLimit);
        if (continuation is { Length: 0 })
        {
            throw new ArgumentException(ErrorMessages.PageContinuationEmpty, nameof(continuation));
        }

        Index = index;
        Value = value;
        Limit = limit;
        Continuation = continuation;
    }

    /// <summary>
    /// Gets the index the query matches on, or <see langword="null"/> for every record of the set.
    /// </summary>
    public IdentityHubRecordIndexName? Index { get; }

    /// <summary>
    /// Gets the value a record lists under <see cref="Index"/> to match, or <see langword="null"/>
    /// for every record of the set.
    /// </summary>
    public IdentityHubRecordKey? Value { get; }

    /// <summary>
    /// Gets the most records the page holds.
    /// </summary>
    public int Limit { get; }

    /// <summary>
    /// Gets the token of the page to continue after, or <see langword="null"/> for the first page.
    /// </summary>
    public string? Continuation { get; }

    /// <summary>
    /// Creates a query for every live record of a record set.
    /// </summary>
    /// <param name="limit">The most records the page holds, from 1 to <see cref="MaxLimit"/>.</param>
    /// <param name="continuation">
    /// The <see cref="IdentityHubRecordPage.Continuation"/> of the previous page of the same
    /// query, or <see langword="null"/> for the first page.
    /// </param>
    /// <returns>The query.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is out of range.</exception>
    /// <exception cref="ArgumentException"><paramref name="continuation"/> is empty.</exception>
    public static IdentityHubRecordQuery All(int limit = DefaultLimit, string? continuation = null)
    {
        return new IdentityHubRecordQuery(null, null, limit, continuation);
    }

    /// <summary>
    /// Creates a query for the live records that list a value under an index, whether the entry
    /// is unique or not.
    /// </summary>
    /// <param name="index">The index to match on.</param>
    /// <param name="value">The value a record lists under <paramref name="index"/>.</param>
    /// <param name="limit">The most records the page holds, from 1 to <see cref="MaxLimit"/>.</param>
    /// <param name="continuation">
    /// The <see cref="IdentityHubRecordPage.Continuation"/> of the previous page of the same
    /// query, or <see langword="null"/> for the first page.
    /// </param>
    /// <returns>The query.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="index"/> or <paramref name="value"/> is <see langword="default"/>, or
    /// <paramref name="continuation"/> is empty.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is out of range.</exception>
    public static IdentityHubRecordQuery ByIndex(
        IdentityHubRecordIndexName index,
        IdentityHubRecordKey value,
        int limit = DefaultLimit,
        string? continuation = null)
    {
        if (index.Value is null)
        {
            throw new ArgumentException(ErrorMessages.RecordIndexNameRequired, nameof(index));
        }

        if (value.Value is null)
        {
            throw new ArgumentException(ErrorMessages.RecordIndexValueRequired, nameof(value));
        }

        return new IdentityHubRecordQuery(index, value, limit, continuation);
    }

    /// <summary>
    /// Creates the same query for the page after a given one.
    /// </summary>
    /// <param name="continuation">The <see cref="IdentityHubRecordPage.Continuation"/> of the page to continue after.</param>
    /// <returns>The query for the next page.</returns>
    /// <exception cref="ArgumentException"><paramref name="continuation"/> is <see langword="null"/> or empty.</exception>
    public IdentityHubRecordQuery After(string continuation)
    {
        ArgumentException.ThrowIfNullOrEmpty(continuation);
        return new IdentityHubRecordQuery(Index, Value, Limit, continuation);
    }
}
