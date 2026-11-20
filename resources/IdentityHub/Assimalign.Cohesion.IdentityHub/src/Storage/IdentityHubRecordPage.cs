using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// One page of the records an <see cref="IdentityHubRecordQuery"/> selects.
/// </summary>
/// <remarks>
/// Only a <see langword="null"/> <see cref="Continuation"/> ends an enumeration. A page may
/// hold fewer records than the query's limit, or none, and still carry a continuation: a store
/// that drops expired records after it reads them returns a short page instead of reading on.
/// </remarks>
public sealed class IdentityHubRecordPage
{
    private static readonly IdentityHubRecordPage _empty = new(Array.Empty<IdentityHubStoredRecord>(), null);

    /// <summary>
    /// Initializes a page.
    /// </summary>
    /// <param name="records">The page's records. The list is copied.</param>
    /// <param name="continuation">
    /// The token that continues the enumeration after this page, or <see langword="null"/> when
    /// this is the last page.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="records"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="records"/> holds a <see langword="null"/> record, or
    /// <paramref name="continuation"/> is empty.
    /// </exception>
    public IdentityHubRecordPage(IEnumerable<IdentityHubStoredRecord> records, string? continuation)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (continuation is { Length: 0 })
        {
            throw new ArgumentException(ErrorMessages.PageContinuationEmpty, nameof(continuation));
        }

        var snapshot = new List<IdentityHubStoredRecord>();
        foreach (IdentityHubStoredRecord record in records)
        {
            if (record is null)
            {
                throw new ArgumentException(ErrorMessages.PageRecordRequired, nameof(records));
            }

            snapshot.Add(record);
        }

        Records = Array.AsReadOnly(snapshot.ToArray());
        Continuation = continuation;
    }

    /// <summary>
    /// Gets a last page that holds no record.
    /// </summary>
    public static IdentityHubRecordPage Empty => _empty;

    /// <summary>
    /// Gets the page's records.
    /// </summary>
    public IReadOnlyList<IdentityHubStoredRecord> Records { get; }

    /// <summary>
    /// Gets the token that continues the enumeration after this page, or <see langword="null"/>
    /// when this is the last page. The token is opaque and belongs to the store and query that
    /// produced it.
    /// </summary>
    public string? Continuation { get; }
}
